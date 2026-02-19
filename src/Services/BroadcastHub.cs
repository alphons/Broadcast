using System.Collections.Concurrent;
using System.Threading.Channels;
using Broadcast.Models;

namespace Broadcast.Services;

/// <summary>
/// Singleton service that manages the live broadcast.
/// Stores the WebM init segment and the most recent keyframe chunk so
/// late-joining viewers always start at a clean decode boundary.
/// Maintains one bounded Channel per viewer for fan-out delivery.
/// </summary>
public sealed class BroadcastHub
{
    // The very first WebM chunk — EBML header + Segment/Info/Tracks.
    // Stored so late-joining viewers receive it before any media data.
    private VideoChunk? _initSegment;

    // The most recent chunk flagged as a keyframe by the broadcaster.
    // A late-joining viewer gets: init → _keyframeChunk → live stream.
    // This guarantees the viewer's decoder always starts at a valid I-frame.
    private VideoChunk? _keyframeChunk;

    private readonly object _stateLock = new();

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
    /// isKeyframe comes from the broadcaster's own SimpleBlock flag inspection,
    /// which is more reliable than server-side EBML parsing.
    /// </summary>
    public Task BroadcastChunkAsync(byte[] data, string mimeType, bool isInit, bool isKeyframe, CancellationToken ct)
    {
        long seq = Interlocked.Increment(ref _sequence);

        var chunk = new VideoChunk
        {
            Data = data,
            IsInit = isInit,
            IsKeyframe = isKeyframe,
            SequenceNumber = seq
        };

        lock (_stateLock)
        {
            if (isInit)
            {
                _initSegment = chunk;           // always overwrite — broadcaster said so
                _keyframeChunk = null;          // new session: discard old keyframe
                MimeType = mimeType;
            }
            else if (isKeyframe)
            {
                // Keep only the most recent keyframe chunk as the catch-up entry point.
                _keyframeChunk = chunk;
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
    /// Registers a new viewer. Pre-queues init and the most recent keyframe chunk
    /// so the viewer can start decoding immediately without waiting for the next keyframe.
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

        VideoChunk? initSnap;
        VideoChunk? keySnap;
        lock (_stateLock)
        {
            initSnap = _initSegment;
            keySnap  = _keyframeChunk;
        }

        if (initSnap is not null)
        {
            channel.Writer.TryWrite(initSnap);
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
    /// </summary>
    public void ResetBroadcast()
    {
        lock (_stateLock)
        {
            _initSegment   = null;
            _keyframeChunk = null;
            Interlocked.Exchange(ref _sequence, 0);
            MimeType = string.Empty;
        }
    }
}
