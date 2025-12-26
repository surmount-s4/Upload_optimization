# Testing the Upload Pipeline (Without Frontend)

This guide helps you test the entire pipeline using command-line tools.

---

## Prerequisites for Testing

1. **.NET 8 SDK** installed on client machine
2. **Python 3.10+** on server machine  
3. **MinIO** running on server
4. A **test file** (start with something small like 500 MB - 1 GB)

---

## Test Setup Order

### Step 1: Start MinIO (Server)

```bash
# If MinIO is not already running
./minio server /data --console-address ":9001"

# Verify it's running
curl http://localhost:9000/minio/health/live
# Should return: OK
```

---

### Step 2: Start Backend API (Server)

```bash
cd backend

# Activate virtual environment
source venv/bin/activate   # Linux
# OR
.\venv\Scripts\activate    # Windows

# Run the server
python main.py
```

**Verify backend:**
```bash
curl http://localhost:8000/health
# Should return: {"status":"healthy","minio":"connected","bucket":"uploads"}
```

---

### Step 3: Start Upload Agent (Client)

```powershell
cd UploadAgent

# Make sure .env has correct BACKEND_URL
# BACKEND_URL=http://YOUR_SERVER_IP:8000

dotnet run
```

You should see:
```
Upload Agent starting...
Configuration: ChunkSize=128MB, Threads=6, WsPort=8765
WebSocket server started on ws://localhost:8765
```

---

### Step 4: Test with PowerShell WebSocket Client

Open a **new PowerShell window** on the client and run this test script:

```powershell
# Save this as test_upload.ps1 and run it

# WebSocket Test Client
$uri = "ws://localhost:8765"

# Create WebSocket client
Add-Type -AssemblyName System.Net.WebSockets

$ws = New-Object System.Net.WebSockets.ClientWebSocket
$cts = New-Object System.Threading.CancellationTokenSource

Write-Host "Connecting to Agent..." -ForegroundColor Yellow
$ws.ConnectAsync([Uri]$uri, $cts.Token).Wait()
Write-Host "Connected!" -ForegroundColor Green

# Background job to receive messages
$receiveJob = Start-Job -ScriptBlock {
    param($wsUri)
    Add-Type -AssemblyName System.Net.WebSockets
    
    $ws = New-Object System.Net.WebSockets.ClientWebSocket
    $cts = New-Object System.Threading.CancellationTokenSource
    $ws.ConnectAsync([Uri]$wsUri, $cts.Token).Wait()
    
    $buffer = New-Object byte[] 4096
    while ($ws.State -eq [System.Net.WebSockets.WebSocketState]::Open) {
        $result = $ws.ReceiveAsync([ArraySegment[byte]]$buffer, $cts.Token).Result
        if ($result.MessageType -eq [System.Net.WebSockets.WebSocketMessageType]::Text) {
            $message = [System.Text.Encoding]::UTF8.GetString($buffer, 0, $result.Count)
            Write-Output $message
        }
    }
} -ArgumentList $uri

# Give a moment for connection
Start-Sleep -Seconds 1

# Prompt for file path
$filePath = Read-Host "Enter full file path to upload (e.g., C:\test\file.zip)"

# Send start command
$command = @{
    action = "start"
    filePath = $filePath
    backendUrl = "http://localhost:8000"  # Change if server is remote
} | ConvertTo-Json

$bytes = [System.Text.Encoding]::UTF8.GetBytes($command)
$ws.SendAsync([ArraySegment[byte]]$bytes, [System.Net.WebSockets.WebSocketMessageType]::Text, $true, $cts.Token).Wait()

Write-Host "Upload command sent!" -ForegroundColor Green
Write-Host "Receiving progress updates (Ctrl+C to stop):" -ForegroundColor Yellow

# Monitor the receive job
while ($true) {
    $output = Receive-Job -Job $receiveJob
    if ($output) {
        foreach ($msg in $output) {
            $json = $msg | ConvertFrom-Json
            switch ($json.type) {
                "config" {
                    Write-Host "Config: ChunkSize=$($json.chunkSizeMB)MB, Threads=$($json.maxThreads)" -ForegroundColor Cyan
                }
                "progress" {
                    Write-Host "`rProgress: $([math]::Round($json.percent,1))% | Speed: $([math]::Round($json.speed/1MB,1)) MB/s | ETA: $($json.eta)s | Parts: $($json.completedParts)/$($json.totalParts)" -NoNewline -ForegroundColor Green
                }
                "status" {
                    Write-Host "`nStatus: $($json.status) - $($json.message)" -ForegroundColor Yellow
                }
                "error" {
                    Write-Host "`nERROR: $($json.error)" -ForegroundColor Red
                }
                "chunk" {
                    # Only show failed chunks
                    if ($json.status -eq "failed") {
                        Write-Host "`nChunk $($json.partNumber) FAILED" -ForegroundColor Red
                    }
                }
            }
        }
    }
    Start-Sleep -Milliseconds 200
}
```

---

### Alternative: Simple Python Test Client

If PowerShell WebSocket is tricky, use this Python script on the client:

```python
# Save as test_client.py

import asyncio
import websockets
import json

async def test_upload():
    uri = "ws://localhost:8765"
    
    async with websockets.connect(uri) as ws:
        print("Connected to Agent!")
        
        # Get file path from user
        file_path = input("Enter full file path to upload: ")
        backend_url = input("Enter backend URL [http://localhost:8000]: ").strip()
        if not backend_url:
            backend_url = "http://localhost:8000"
        
        # Send start command
        command = {
            "action": "start",
            "filePath": file_path,
            "backendUrl": backend_url
        }
        await ws.send(json.dumps(command))
        print("Upload started!")
        
        # Listen for updates
        try:
            while True:
                message = await ws.recv()
                data = json.loads(message)
                
                if data["type"] == "config":
                    print(f"Config: {data}")
                elif data["type"] == "progress":
                    print(f"\rProgress: {data['percent']:.1f}% | "
                          f"Speed: {data['speed']/1024/1024:.1f} MB/s | "
                          f"Parts: {data['completedParts']}/{data['totalParts']}", end="")
                elif data["type"] == "status":
                    print(f"\nStatus: {data['status']} - {data['message']}")
                    if data["status"] == "completed":
                        print("\n✅ UPLOAD COMPLETE!")
                        break
                elif data["type"] == "error":
                    print(f"\n❌ ERROR: {data['error']}")
                    break
        except KeyboardInterrupt:
            print("\nTest cancelled by user")

if __name__ == "__main__":
    # Install websockets: pip install websockets
    asyncio.run(test_upload())
```

Run it:
```bash
pip install websockets
python test_client.py
```

---

## Test Pause/Resume/Cancel

Once upload starts, you can send these commands:

**Pause:**
```python
await ws.send(json.dumps({"action": "pause", "uploadId": "YOUR_UPLOAD_ID"}))
```

**Resume:**
```python
await ws.send(json.dumps({"action": "resume", "uploadId": "YOUR_UPLOAD_ID"}))
```

**Cancel:**
```python
await ws.send(json.dumps({"action": "cancel", "uploadId": "YOUR_UPLOAD_ID"}))
```

---

## Verify Upload in MinIO

After successful upload:

1. Open MinIO Console: `http://server-ip:9001`
2. Login with credentials (default: minioadmin/minioadmin)
3. Navigate to "uploads" bucket
4. Your file should appear with the correct size

---

## Test Checklist

| Test | Expected Result |
|------|-----------------|
| Backend health check | `{"status":"healthy"}` |
| Agent starts | "WebSocket server started" message |
| Client connects | "Connected!" in test script |
| Small file (100 MB) | Completes in under a minute |
| Medium file (1 GB) | Shows progress, ~10-60s depending on network |
| Pause/Resume | Progress stops and resumes |
| Cancel | Upload stops, status shows "cancelled" |
| Verify in MinIO | File exists with correct size |

---

## Troubleshooting Tests

| Issue | Solution |
|-------|----------|
| "Connection refused" on port 8765 | Agent not running, start it first |
| "Connection refused" on port 8000 | Backend not running or wrong IP |
| "File not found" error | Use absolute path with escaped backslashes |
| Upload hangs at 0% | Check MinIO credentials in backend .env |
| Very slow speed | Check network, reduce chunk size for testing |
