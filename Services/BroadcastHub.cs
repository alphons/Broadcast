using System.Collections.Concurrent;
using System.Threading.Channels;
using Broadcast.Models;

namespace Broadcast.Services;

/// <summary>
/// Singleton service that manages the live broadcast.
/// Stores the WebM init segment so late-joining viewers get a decodable stream.
/// Maintains one bounded Channel per viewer for fan-out delivery.
/// </summary>
public sealed class BroadcastHub
{
    // The very first WebM chunk — EBML header + Segment/Info/Tracks.
    // Stored so late-joining viewers receive it before any media data.
    private VideoChunk? _initSegment;
    private readonly object _initLock = new();

    // The exact mimeType string the broadcaster's MediaRecorder is using.
    // Viewers MUST use this same codec string for addSourceBuffer().
    public string MimeType { get; private set; } = string.Empty;

    // Monotonically increasing sequence number. seq==1 identifies the init segment.
    private long _sequence = 0;

    // One bounded channel per connected viewer.
    private readonly ConcurrentDictionary<Guid, Channel<VideoChunk>> _viewers = new();

    // 150 chunks × 250ms timeslice ≈ 37 seconds of buffer per viewer.
    // DropOldest: slow viewers skip forward rather than causing memory growth.
    private const int ViewerBufferCapacity = 150;

    /// <summary>
    /// Called by the broadcaster POST endpoint for each binary WebM chunk.
    /// Stores the init segment on first call, then fans the chunk to all viewers.
    /// mimeType is the codec string from the broadcaster's MediaRecorder.
    /// </summary>
    public Task BroadcastChunkAsync(byte[] data, string mimeType, CancellationToken ct)
    {
        long seq = Interlocked.Increment(ref _sequence);
        bool isInit = seq == 1;

        var chunk = new VideoChunk
        {
            Data = data,
            IsInit = isInit,
            SequenceNumber = seq
        };

        // Store the init segment and mimeType exactly once.
        if (isInit)
        {
            lock (_initLock)
            {
                if (_initSegment is null)
                {
                    _initSegment = chunk;
                    MimeType = mimeType;
                }
            }
        }

        // Fan out to all connected viewer channels.
        // TryWrite is non-blocking; DropOldest handles full channels automatically.
        foreach (var (_, channel) in _viewers)
        {
            channel.Writer.TryWrite(chunk);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Registers a new viewer. If broadcast has already started, pre-queues the
    /// init segment so the viewer receives it immediately upon connection.
    /// </summary>
    public (Guid viewerId, ChannelReader<VideoChunk> reader) AddViewer()
    {
        var viewerId = Guid.NewGuid();

        var options = new BoundedChannelOptions(ViewerBufferCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        };

        var channel = Channel.CreateBounded<VideoChunk>(options);
        _viewers[viewerId] = channel;

        // Pre-queue the init segment for late-joining viewers.
        VideoChunk? existingInit;
        lock (_initLock)
        {
            existingInit = _initSegment;
        }

        if (existingInit is not null)
        {
            channel.Writer.TryWrite(existingInit);
        }

        return (viewerId, channel.Reader);
    }

    /// <summary>
    /// Removes a viewer's channel when their SSE connection closes.
    /// Completing the writer unblocks ReadAllAsync on the reader side.
    /// </summary>
    public void RemoveViewer(Guid viewerId)
    {
        if (_viewers.TryRemove(viewerId, out var channel))
        {
            channel.Writer.TryComplete();
        }
    }

    /// <summary>Number of currently connected viewers.</summary>
    public int ViewerCount => _viewers.Count;

    /// <summary>True once the broadcaster has sent at least one chunk.</summary>
    public bool IsBroadcastActive => _initSegment is not null;

    /// <summary>
    /// Resets the broadcast state so a new broadcast can start.
    /// Clears the stored init segment and resets the sequence counter.
    /// Connected viewers are NOT disconnected — they will receive an error
    /// when they try to append new chunks with a mismatched init.
    /// </summary>
    public void ResetBroadcast()
    {
        lock (_initLock)
        {
            _initSegment = null;
        }
        Interlocked.Exchange(ref _sequence, 0);
        MimeType = string.Empty;
    }
}
