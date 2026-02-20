# Broadcast

A live video streaming application built with ASP.NET Core and vanilla JavaScript.

## What does it do?

Broadcast enables live video streaming to multiple viewers simultaneously through the browser:

- **Broadcaster:** Captures video and audio from the webcam/microphone and sends it to the server
- **Viewer:** Receives the live stream in real-time via Server-Sent Events (SSE) and plays it back

## Technology

- **Backend:** .NET 10.0 / ASP.NET Core Web API
- **Frontend:** Vanilla JavaScript (no frameworks)
- **Video:** WebM (VP8/VP9 + Opus audio), MediaRecorder API, Media Source Extensions (MSE)
- **Protocol:** HTTP + Server-Sent Events

## Project Structure

```
src/
├── Program.cs                  # Application entry point and configuration
├── Controllers/
│   └── StreamController.cs    # API endpoints for broadcaster and viewers
├── Services/
│   └── BroadcastHub.cs        # Central broadcast service (singleton)
├── Models/
│   └── VideoChunk.cs          # Data model for video chunks
└── wwwroot/
    └── index.html             # Frontend UI
```

## API Endpoints

| Method | Endpoint                  | Description                              |
|--------|---------------------------|------------------------------------------|
| POST   | `/api/stream/broadcast`   | Sends a video chunk to the server        |
| GET    | `/api/stream/watch`       | SSE stream for viewers                   |
| POST   | `/api/stream/reset`       | Resets the broadcast session             |
| GET    | `/api/stream/info`        | MIME type and active status              |
| GET    | `/api/stream/status`      | Broadcast status and viewer count        |

## Architecture

### BroadcastHub (singleton)
- Manages the initialization segment (WebM EBML header) so late-joining viewers can start correctly
- Caches the most recent keyframe as a catch-up point for new viewers
- Gives each viewer their own bounded channel (max. 150 chunks ≈ 37 seconds buffer)
- Slow viewers skip frames instead of consuming unbounded memory (`DropOldest`)

### Frontend
- **Broadcaster side:** Manually parses WebM bytes to detect keyframes (EBML structure)
- **Viewer side:** Uses the Media Source Extensions API for smooth playback, manages the buffer automatically, and syncs to the live edge

## Running Locally

```bash
cd src
dotnet run
```

The application is available at:
- HTTPS: `https://localhost:57624`
- HTTP: `http://localhost:57625`

Open the URL in two browser tabs: one as broadcaster, one as viewer.
