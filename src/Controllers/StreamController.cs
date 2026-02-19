using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using Broadcast.Services;
using Microsoft.AspNetCore.Mvc;

namespace Broadcast.Controllers;

[ApiController]
[Route("api/stream")]
public class StreamController : ControllerBase
{
    private readonly BroadcastHub _hub;

    public StreamController(BroadcastHub hub)
    {
        _hub = hub;
    }

    // -------------------------------------------------------------------------
    // POST /api/stream/broadcast
    // Receives one binary WebM chunk from the broadcaster's MediaRecorder.
    // Content-Type: application/octet-stream
    // -------------------------------------------------------------------------
    [HttpPost("broadcast")]
    [DisableRequestSizeLimit]
    [Consumes("application/octet-stream")]
    public async Task<IActionResult> Broadcast(CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await Request.Body.CopyToAsync(ms, ct);
        byte[] data = ms.ToArray();

        if (data.Length == 0)
            return BadRequest(new { error = "Empty chunk received." });

        // Broadcaster sends its actual mimeType in X-Mime-Type header so viewers
        // can call addSourceBuffer() with the exact same codec string.
        var mimeType   = Request.Headers["X-Mime-Type"].FirstOrDefault() ?? "video/webm;codecs=vp8,opus";
        var isInit     = Request.Headers["X-Is-Init"].FirstOrDefault() == "true";
        var isKeyframe = Request.Headers["X-Is-Keyframe"].FirstOrDefault() == "true";

        await _hub.BroadcastChunkAsync(data, mimeType, isInit, isKeyframe, ct);

        return Ok(new { received = data.Length, viewers = _hub.ViewerCount });
    }

    // -------------------------------------------------------------------------
    // POST /api/stream/reset
    // Allows the broadcaster to signal a new broadcast session start.
    // -------------------------------------------------------------------------
    [HttpPost("reset")]
    public IActionResult Reset()
    {
        _hub.ResetBroadcast();
        return Ok(new { reset = true });
    }

    // -------------------------------------------------------------------------
    // GET /api/stream/watch
    // Opens a Server-Sent Events connection for a viewer.
    // Stays open until the viewer disconnects or cancellation fires.
    // Each chunk is base64-encoded (SSE is text-only).
    // eventType "init" = WebM initialization segment (must be appended first)
    // eventType "chunk" = regular media cluster
    // -------------------------------------------------------------------------
    [HttpGet("watch")]
    public IResult Watch(CancellationToken ct)
    {
        var (viewerId, reader) = _hub.AddViewer();

        // Clean up viewer channel when the SSE connection closes.
        ct.Register(() => _hub.RemoveViewer(viewerId));

        async IAsyncEnumerable<SseItem<string>> StreamChunks(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            // OperationCanceledException is expected and normal when the viewer
            // closes the browser tab or navigates away — do not propagate it.
            var enumerator = reader.ReadAllAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
            try
            {
                while (true)
                {
                    bool hasNext;
                    try
                    {
                        hasNext = await enumerator.MoveNextAsync();
                    }
                    catch (OperationCanceledException)
                    {
                        yield break;
                    }

                    if (!hasNext) yield break;

                    var chunk = enumerator.Current;
                    var base64 = Convert.ToBase64String(chunk.Data);
                    var eventType = chunk.IsInit ? "init"
                                 : chunk.IsKeyframe ? "keyframe"
                                 : "chunk";

                    yield return new SseItem<string>(base64, eventType)
                    {
                        EventId = chunk.SequenceNumber.ToString()
                    };
                }
            }
            finally
            {
                await enumerator.DisposeAsync();
            }
        }

        return TypedResults.ServerSentEvents(StreamChunks(ct));
    }

    // -------------------------------------------------------------------------
    // GET /api/stream/info
    // Returns the broadcaster's mimeType so viewers can open a matching SourceBuffer.
    // -------------------------------------------------------------------------
    [HttpGet("info")]
    public IActionResult Info()
    {
        return Ok(new
        {
            active = _hub.IsBroadcastActive,
            mimeType = _hub.MimeType
        });
    }

    // -------------------------------------------------------------------------
    // GET /api/stream/status
    // Returns current broadcast state and viewer count.
    // -------------------------------------------------------------------------
    [HttpGet("status")]
    public IActionResult Status()
    {
        return Ok(new
        {
            active = _hub.IsBroadcastActive,
            viewers = _hub.ViewerCount
        });
    }
}
