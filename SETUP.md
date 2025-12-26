# 200 GB Upload Pipeline - Setup Guide

Complete setup instructions for deploying the high-throughput upload pipeline.

---

## System Overview

```
┌─────────────────────────────────────────────────────────────────────────┐
│                            CLIENT MACHINE                               │
│                         (64 GB RAM, Windows)                            │
├─────────────────────────────────────────────────────────────────────────┤
│  1. Upload Agent (C# Windows Service)                                   │
│     - Runs on: ws://localhost:8765                                      │
│     - Reads files, uploads chunks directly to MinIO                     │
│                                                                         │
│  2. Frontend (Browser)                                                  │
│     - Your existing web app                                             │
│     - Connects to Agent via WebSocket                                   │
└─────────────────────────────────────────────────────────────────────────┘
                          │
                          │ HTTP (LAN)
                          ▼
┌─────────────────────────────────────────────────────────────────────────┐
│                            SERVER MACHINE                               │
│                        (128 GB RAM, Linux/Windows)                      │
├─────────────────────────────────────────────────────────────────────────┤
│  3. Backend API (Python FastAPI)                                        │
│     - Runs on: http://server-ip:8000                                    │
│     - Manages upload metadata, issues presigned URLs                    │
│                                                                         │
│  4. MinIO Storage                                                       │
│     - Runs on: http://server-ip:9000                                    │
│     - Receives chunks directly from Agent via presigned URLs            │
└─────────────────────────────────────────────────────────────────────────┘
```

---

## Prerequisites

### CLIENT Machine (Windows)

| Requirement | Version | Download |
|-------------|---------|----------|
| .NET SDK | 8.0+ | https://dotnet.microsoft.com/download/dotnet/8.0 |
| Windows | 10/11 or Server 2019+ | — |

### SERVER Machine

| Requirement | Version | Download |
|-------------|---------|----------|
| Python | 3.10+ | https://python.org |
| MinIO | Latest | https://min.io/download |
| pip | Latest | Comes with Python |

---

## Step-by-Step Setup

### STEP 1: Server - MinIO Storage

```bash
# Download and run MinIO (Linux example)
wget https://dl.min.io/server/minio/release/linux-amd64/minio
chmod +x minio
./minio server /data --console-address ":9001"

# Default credentials: minioadmin / minioadmin
# API: http://server-ip:9000
# Console: http://server-ip:9001
```

**Create upload bucket:**
```bash
# Using mc (MinIO Client)
mc alias set myminio http://localhost:9000 minioadmin minioadmin
mc mb myminio/uploads
```

---

### STEP 2: Server - Backend API

```bash
# Navigate to backend folder
cd backend/

# Create virtual environment
python -m venv venv
source venv/bin/activate        # Linux/Mac
# OR
.\venv\Scripts\activate         # Windows

# Install dependencies
pip install -r requirements.txt

# Configure environment
cp .env.example .env
```

**Edit `.env`:**
```env
MINIO_ENDPOINT=localhost:9000      # Change if MinIO is remote
MINIO_ACCESS_KEY=minioadmin
MINIO_SECRET_KEY=minioadmin
MINIO_SECURE=false
MINIO_BUCKET=uploads
PORT=8000
```

**Run the server:**
```bash
python main.py
# OR for production
uvicorn main:app --host 0.0.0.0 --port 8000
```

**Verify:** `curl http://server-ip:8000/health`

---

### STEP 3: Client - Upload Agent

```powershell
# Navigate to agent folder
cd UploadAgent/

# Restore and build
dotnet restore
dotnet build -c Release
```

**Edit `.env`:**
```env
BACKEND_URL=http://server-ip:8000   # Point to your server
CHUNK_SIZE_MB=128
UPLOAD_THREADS_MAX=8
WS_PORT=8765
```

**Run (Development):**
```powershell
dotnet run
```

**Run (Production - Windows Service):**
```powershell
# Publish
dotnet publish -c Release -o ./publish

# Install as service
sc.exe create UploadAgent binPath="C:\full\path\to\publish\UploadAgent.exe"
sc.exe start UploadAgent
```

---

### STEP 4: Client - Frontend Integration

Add the WebSocket module from `docs/FRONTEND_INTEGRATION.md` to your existing frontend.

**Quick test (browser console):**
```javascript
ws = new WebSocket('ws://localhost:8765');
ws.onmessage = (e) => console.log(JSON.parse(e.data));
ws.onopen = () => console.log('Connected!');
```

---

## Startup Order

| Order | Component | Machine | Command |
|-------|-----------|---------|---------|
| 1 | MinIO | Server | `./minio server /data` |
| 2 | Backend API | Server | `python main.py` |
| 3 | Upload Agent | Client | `dotnet run` |
| 4 | Frontend | Client | Open in browser |

---

## File Reference

### What Runs on SERVER

| File | Purpose | Port |
|------|---------|------|
| `backend/main.py` | FastAPI entry point | 8000 |
| `backend/endpoints/upload.py` | REST endpoints | — |
| `backend/services/minio_multipart.py` | MinIO operations | — |
| MinIO | Object storage | 9000, 9001 |

### What Runs on CLIENT

| File | Purpose | Port |
|------|---------|------|
| `UploadAgent/Program.cs` | Agent entry point | — |
| `UploadAgent/AgentWorker.cs` | Main orchestrator | — |
| `UploadAgent/Services/*.cs` | All services | 8765 (WS) |
| Frontend (your code) | User interface | Browser |

---

## Network Requirements

| From | To | Port | Protocol |
|------|----|------|----------|
| Agent | Backend API | 8000 | HTTP |
| Agent | MinIO | 9000 | HTTP/HTTPS |
| Frontend | Agent | 8765 | WebSocket |

Ensure firewall allows these connections on your LAN.

---

## Quick Verification Checklist

- [ ] MinIO accessible: `curl http://server-ip:9000/minio/health/live`
- [ ] Backend running: `curl http://server-ip:8000/health`
- [ ] Agent running: Check if port 8765 responds
- [ ] Frontend connects: See "Connected!" in browser console

---

## Troubleshooting

| Issue | Check |
|-------|-------|
| Agent can't reach backend | Verify `BACKEND_URL` in Agent `.env` |
| Upload fails at 0% | Check MinIO credentials in backend `.env` |
| WebSocket won't connect | Ensure Agent is running, check port 8765 |
| Slow speeds | Increase `UPLOAD_THREADS_MAX`, check network |
