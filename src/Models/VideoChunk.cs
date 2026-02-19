namespace Broadcast.Models;

/// <summary>
/// Represents one binary WebM chunk from the broadcaster.
/// IsInit is true only for the very first chunk (the WebM EBML header +
/// Segment/Info/Tracks cluster), which new viewers must receive before any
/// media data or they cannot decode the stream.
/// </summary>
public sealed class VideoChunk
{
    public required byte[] Data { get; init; }
    public bool IsInit { get; init; }
    public bool IsKeyframe { get; init; }
    public long SequenceNumber { get; init; }
}
