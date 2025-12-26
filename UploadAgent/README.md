# Upload Agent Documentation

A Windows Service that handles high-throughput file uploads to MinIO via the backend orchestrator.

## Architecture

```
┌──────────────────────────────────────────────────────────────────┐
│                         Upload Agent                              │
├──────────────────────────────────────────────────────────────────┤
│  ┌─────────────┐    ┌──────────────┐    ┌──────────────────┐    │
│  │ WebSocket   │◄──►│ AgentWorker  │◄──►│ UploadWorkerPool │    │
│  │ Server      │    │ (Orchestrator│    │ (Parallel Upload)│    │
│  └──────┬──────┘    └──────┬───────┘    └────────┬─────────┘    │
│         │                  │                     │               │
│         │           ┌──────┴───────┐      ┌──────┴──────┐       │
│         │           │StateManifest │      │FileProcessor│       │
│         │           │  (SQLite)    │      │(File Lock)  │       │
│         │           └──────────────┘      └─────────────┘       │
│         ▼                                                        │
│    Frontend (Browser)                                            │
└──────────────────────────────────────────────────────────────────┘
        │                      │
        ▼                      ▼
   [Backend API]          [MinIO Storage]
```

## Project Structure

```
UploadAgent/
├── UploadAgent.csproj        # Project file with dependencies
├── Program.cs                # Entry point & DI setup  
├── AgentWorker.cs            # Main background service
├── .env                      # Configuration (copy and customize)
├── Models/
│   ├── UploadModels.cs       # Job and chunk data models
│   └── WsMessages.cs         # WebSocket message types
└── Services/
    ├── AppConfig.cs          # Environment configuration loader
    ├── StateManifest.cs      # SQLite persistence layer
    ├── FileProcessor.cs      # File locking and chunking
    ├── WebSocketServer.cs    # Local WebSocket for frontend
    ├── UploadWorkerPool.cs   # Parallel upload engine
    └── BackendClient.cs      # Backend API client
```

## Prerequisites

- .NET 8 SDK
- Windows OS (for Windows Service features)

## Installation

### 1. Install .NET 8 SDK

Download from: https://dotnet.microsoft.com/download/dotnet/8.0

### 2. Build the Agent

```powershell
cd UploadAgent
dotnet restore
dotnet build -c Release
```

### 3. Configure Environment

Copy and edit the `.env` file:

```env
# Key settings to customize:
BACKEND_URL=http://your-server:8000
CHUNK_SIZE_MB=128
UPLOAD_THREADS_MAX=8
WS_PORT=8765
```

### 4. Run as Console (Development)

```powershell
dotnet run
```

### 5. Install as Windows Service (Production)

```powershell
# Build for release
dotnet publish -c Release -o ./publish

# Create Windows Service
sc.exe create UploadAgent binPath="C:\path\to\publish\UploadAgent.exe"

# Start the service
sc.exe start UploadAgent
```

## Configuration Reference

| Variable | Default | Description |
|----------|---------|-------------|
| `CHUNK_SIZE_MB` | 128 | Size of each upload chunk |
| `UPLOAD_THREADS_MIN` | 2 | Minimum parallel threads |
| `UPLOAD_THREADS_MAX` | 8 | Maximum parallel threads |
| `UPLOAD_THREADS_AUTO` | true | Auto-detect optimal threads |
| `PRESIGN_BATCH_SIZE` | 20 | URLs to fetch per batch |
| `PRESIGN_LOOKAHEAD` | 50 | Pre-fetched URLs to maintain |
| `RETRY_MAX_ATTEMPTS` | 3 | Retries per failed chunk |
| `HTTP_TIMEOUT_SECONDS` | 300 | Upload timeout per chunk |
| `WS_PORT` | 8765 | WebSocket server port |
| `BACKEND_URL` | http://localhost:8000 | Backend API URL |

## WebSocket API

### Connection

Connect to `ws://localhost:8765` from your frontend.

### Commands (Frontend → Agent)

**Start Upload:**
```json
{
  "action": "start",
  "filePath": "D:\\Data\\large_file.zip",
  "backendUrl": "http://localhost:8000"
}
```

**Pause Upload:**
```json
{
  "action": "pause",
  "uploadId": "upload-id-here"
}
```

**Resume Upload:**
```json
{
  "action": "resume", 
  "uploadId": "upload-id-here"
}
```

**Cancel Upload:**
```json
{
  "action": "cancel",
  "uploadId": "upload-id-here"
}
```

### Messages (Agent → Frontend)

**Config (on connect):**
```json
{
  "type": "config",
  "chunkSizeMB": 128,
  "maxThreads": 6,
  "presignBatchSize": 20,
  "wsPort": 8765
}
```

**Progress (every 500ms):**
```json
{
  "type": "progress",
  "uploadId": "...",
  "percent": 45.2,
  "speed": 131072000,
  "eta": 1935,
  "bytesTransferred": 96636764160,
  "totalBytes": 214748364800,
  "activeThreads": 6,
  "completedParts": 723,
  "totalParts": 1600
}
```

**Chunk Status:**
```json
{
  "type": "chunk",
  "uploadId": "...",
  "partNumber": 42,
  "status": "completed",
  "etag": "\"abc123\""
}
```

**Status Change:**
```json
{
  "type": "status",
  "uploadId": "...",
  "status": "uploading",
  "message": "Uploading chunks..."
}
```

**Error:**
```json
{
  "type": "error",
  "uploadId": "...",
  "error": "Error message",
  "code": "ERROR_CODE"
}
```

## Resume on Crash

The Agent uses SQLite to persist upload state. If the Agent crashes or is restarted:

1. Completed chunks are tracked with their ETags
2. On restart, Agent queries the manifest for pending chunks
3. Upload resumes from where it left off

The SQLite database is stored at: `UploadAgent/upload_state.db`

## Troubleshooting

### Agent won't start
- Check if port 8765 is already in use
- Verify .NET 8 runtime is installed
- Check Windows Event Viewer for errors

### Upload fails immediately
- Verify backend URL is correct
- Check backend is running and accessible
- Ensure MinIO credentials are valid

### Slow upload speeds
- Increase `UPLOAD_THREADS_MAX`
- Check network bandwidth
- Verify chunk size is optimal for your network

### File lock errors
- Ensure no other application has the file open
- Close any editors/viewers of the file
