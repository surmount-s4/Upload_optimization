# Frontend Integration Guide

This guide explains how to integrate the Upload Agent with your existing frontend.

## Overview

```
┌─────────────────────────────────────────────────────────────┐
│                        Your Frontend                         │
├─────────────────────────────────────────────────────────────┤
│  ┌──────────────────┐    ┌──────────────────────────────┐  │
│  │   Upload Button  │──▶ │   UploadAgentConnection      │  │
│  │   (existing)     │    │   (new WebSocket module)     │  │
│  └──────────────────┘    └─────────────┬────────────────┘  │
│                                        │                    │
│  ┌──────────────────┐                  │                    │
│  │   Progress UI    │◀─────────────────┘                    │
│  │   (new component)│                                       │
│  └──────────────────┘                                       │
└─────────────────────────────────────────────────────────────┘
               │                 ▲
               │ WebSocket       │ Progress Updates
               ▼                 │
        [Upload Agent @ localhost:8765]
```

## Step 1: Add WebSocket Connection Module

Create a new file to handle Agent communication:

```javascript
// services/uploadAgent.js

class UploadAgentConnection {
    constructor(port = 8765) {
        this.ws = null;
        this.port = port;
        this.reconnectAttempts = 0;
        this.maxReconnectAttempts = 5;
        
        // Callbacks - set these from your UI
        this.onConnectionChange = null;  // (connected: boolean) => void
        this.onProgress = null;          // (data: ProgressData) => void
        this.onChunkUpdate = null;       // (data: ChunkData) => void
        this.onStatusChange = null;      // (data: StatusData) => void
        this.onError = null;             // (data: ErrorData) => void
        this.onConfig = null;            // (data: ConfigData) => void
    }

    connect() {
        if (this.ws?.readyState === WebSocket.OPEN) {
            console.log('Already connected');
            return;
        }

        try {
            this.ws = new WebSocket(`ws://localhost:${this.port}`);

            this.ws.onopen = () => {
                console.log('Connected to Upload Agent');
                this.reconnectAttempts = 0;
                this.onConnectionChange?.(true);
            };

            this.ws.onmessage = (event) => {
                try {
                    const message = JSON.parse(event.data);
                    this._handleMessage(message);
                } catch (e) {
                    console.error('Failed to parse message:', e);
                }
            };

            this.ws.onclose = () => {
                console.log('Disconnected from Upload Agent');
                this.onConnectionChange?.(false);
                this._attemptReconnect();
            };

            this.ws.onerror = (error) => {
                console.error('WebSocket error:', error);
            };

        } catch (error) {
            console.error('Failed to connect:', error);
            this._attemptReconnect();
        }
    }

    _attemptReconnect() {
        if (this.reconnectAttempts < this.maxReconnectAttempts) {
            this.reconnectAttempts++;
            const delay = Math.min(1000 * Math.pow(2, this.reconnectAttempts), 30000);
            console.log(`Reconnecting in ${delay}ms (attempt ${this.reconnectAttempts})`);
            setTimeout(() => this.connect(), delay);
        }
    }

    _handleMessage(message) {
        switch (message.type) {
            case 'config':
                this.onConfig?.(message);
                break;
            case 'progress':
                this.onProgress?.(message);
                break;
            case 'chunk':
                this.onChunkUpdate?.(message);
                break;
            case 'status':
                this.onStatusChange?.(message);
                break;
            case 'error':
                this.onError?.(message);
                break;
            default:
                console.log('Unknown message type:', message.type);
        }
    }

    // === COMMANDS ===

    startUpload(filePath, backendUrl = 'http://localhost:8000') {
        if (this.ws?.readyState !== WebSocket.OPEN) {
            throw new Error('Not connected to agent');
        }
        this.ws.send(JSON.stringify({
            action: 'start',
            filePath: filePath,
            backendUrl: backendUrl
        }));
    }

    pauseUpload(uploadId) {
        if (this.ws?.readyState !== WebSocket.OPEN) return;
        this.ws.send(JSON.stringify({
            action: 'pause',
            uploadId: uploadId
        }));
    }

    resumeUpload(uploadId) {
        if (this.ws?.readyState !== WebSocket.OPEN) return;
        this.ws.send(JSON.stringify({
            action: 'resume',
            uploadId: uploadId
        }));
    }

    cancelUpload(uploadId) {
        if (this.ws?.readyState !== WebSocket.OPEN) return;
        this.ws.send(JSON.stringify({
            action: 'cancel',
            uploadId: uploadId
        }));
    }

    disconnect() {
        if (this.ws) {
            this.maxReconnectAttempts = 0; // Prevent reconnect
            this.ws.close();
            this.ws = null;
        }
    }
}

// Export singleton instance
const uploadAgent = new UploadAgentConnection();
export default uploadAgent;
```

## Step 2: File Path Input

Since files can exceed 4GB, the standard `<input type="file">` won't work. Options:

### Option A: Manual Path Input (Simplest)

```html
<div class="file-input">
    <input 
        type="text" 
        id="filePath" 
        placeholder="C:\Data\large_file.zip"
    />
    <button onclick="startUpload()">Upload</button>
</div>
```

```javascript
function startUpload() {
    const filePath = document.getElementById('filePath').value;
    if (!filePath) {
        alert('Please enter a file path');
        return;
    }
    uploadAgent.startUpload(filePath, 'http://your-backend:8000');
}
```

### Option B: Drag & Drop (for path extraction)

```javascript
document.addEventListener('drop', (e) => {
    e.preventDefault();
    const files = e.dataTransfer.files;
    if (files.length > 0) {
        // Note: This only works in Electron/Tauri, not regular browsers
        const filePath = files[0].path;
        document.getElementById('filePath').value = filePath;
    }
});
```

## Step 3: Progress Display Component

### HTML Structure

```html
<div id="upload-progress" class="upload-progress hidden">
    <div class="file-info">
        <span id="fileName">filename.zip</span>
        <span id="fileSize">200 GB</span>
    </div>
    
    <div class="progress-bar">
        <div id="progressFill" class="progress-fill" style="width: 0%"></div>
    </div>
    
    <div class="stats">
        <span>⚡ <span id="speed">0 MB/s</span></span>
        <span>⏱️ <span id="eta">--:--:--</span></span>
        <span>📊 <span id="parts">0/0</span></span>
    </div>
    
    <div class="status">
        <span id="statusText">Preparing...</span>
    </div>
    
    <div class="controls">
        <button id="pauseBtn" onclick="togglePause()">⏸️ Pause</button>
        <button id="cancelBtn" onclick="cancelUpload()">❌ Cancel</button>
    </div>
</div>
```

### CSS Styling

```css
.upload-progress {
    background: #1a1a2e;
    border-radius: 12px;
    padding: 24px;
    max-width: 500px;
    margin: 20px auto;
}

.upload-progress.hidden {
    display: none;
}

.file-info {
    display: flex;
    justify-content: space-between;
    margin-bottom: 16px;
    color: #fff;
}

.progress-bar {
    height: 24px;
    background: #16213e;
    border-radius: 12px;
    overflow: hidden;
    margin-bottom: 16px;
}

.progress-fill {
    height: 100%;
    background: linear-gradient(90deg, #0f3460, #e94560);
    transition: width 0.3s ease;
    display: flex;
    align-items: center;
    justify-content: flex-end;
    padding-right: 8px;
    color: white;
    font-weight: bold;
}

.stats {
    display: flex;
    justify-content: space-around;
    color: #888;
    margin-bottom: 16px;
}

.status {
    text-align: center;
    color: #4ecca3;
    margin-bottom: 16px;
}

.controls {
    display: flex;
    gap: 12px;
    justify-content: center;
}

.controls button {
    padding: 8px 24px;
    border: none;
    border-radius: 8px;
    cursor: pointer;
    font-size: 14px;
}

#pauseBtn {
    background: #f39c12;
    color: #000;
}

#cancelBtn {
    background: #e74c3c;
    color: #fff;
}
```

### JavaScript Logic

```javascript
import uploadAgent from './services/uploadAgent.js';

let currentUploadId = null;
let isPaused = false;

// Initialize on page load
document.addEventListener('DOMContentLoaded', () => {
    initializeUploadAgent();
});

function initializeUploadAgent() {
    // Set up callbacks
    uploadAgent.onConnectionChange = (connected) => {
        document.getElementById('connectionStatus').textContent = 
            connected ? '🟢 Connected' : '🔴 Disconnected';
    };

    uploadAgent.onConfig = (config) => {
        console.log('Agent config:', config);
    };

    uploadAgent.onProgress = (data) => {
        updateProgress(data);
    };

    uploadAgent.onStatusChange = (data) => {
        currentUploadId = data.uploadId;
        document.getElementById('statusText').textContent = data.message;
        
        if (data.status === 'completed') {
            showCompleted();
        }
    };

    uploadAgent.onError = (data) => {
        alert(`Error: ${data.error}`);
        hideProgress();
    };

    // Connect
    uploadAgent.connect();
}

function updateProgress(data) {
    const progressDiv = document.getElementById('upload-progress');
    progressDiv.classList.remove('hidden');

    // Update progress bar
    const fill = document.getElementById('progressFill');
    fill.style.width = `${data.percent.toFixed(1)}%`;
    fill.textContent = `${data.percent.toFixed(1)}%`;

    // Update stats
    document.getElementById('speed').textContent = formatSpeed(data.speed);
    document.getElementById('eta').textContent = formatEta(data.eta);
    document.getElementById('parts').textContent = 
        `${data.completedParts}/${data.totalParts}`;
}

function formatSpeed(bytesPerSec) {
    if (bytesPerSec >= 1024 * 1024 * 1024) {
        return `${(bytesPerSec / 1024 / 1024 / 1024).toFixed(1)} GB/s`;
    }
    if (bytesPerSec >= 1024 * 1024) {
        return `${(bytesPerSec / 1024 / 1024).toFixed(1)} MB/s`;
    }
    return `${(bytesPerSec / 1024).toFixed(1)} KB/s`;
}

function formatEta(seconds) {
    if (!seconds || seconds <= 0) return '--:--:--';
    const h = Math.floor(seconds / 3600);
    const m = Math.floor((seconds % 3600) / 60);
    const s = Math.floor(seconds % 60);
    return `${h.toString().padStart(2, '0')}:${m.toString().padStart(2, '0')}:${s.toString().padStart(2, '0')}`;
}

function togglePause() {
    if (!currentUploadId) return;
    
    if (isPaused) {
        uploadAgent.resumeUpload(currentUploadId);
        document.getElementById('pauseBtn').textContent = '⏸️ Pause';
    } else {
        uploadAgent.pauseUpload(currentUploadId);
        document.getElementById('pauseBtn').textContent = '▶️ Resume';
    }
    isPaused = !isPaused;
}

function cancelUpload() {
    if (!currentUploadId) return;
    
    if (confirm('Are you sure you want to cancel the upload?')) {
        uploadAgent.cancelUpload(currentUploadId);
        hideProgress();
    }
}

function showCompleted() {
    document.getElementById('statusText').textContent = '✅ Upload Complete!';
    document.getElementById('pauseBtn').disabled = true;
    document.getElementById('cancelBtn').disabled = true;
}

function hideProgress() {
    document.getElementById('upload-progress').classList.add('hidden');
    currentUploadId = null;
    isPaused = false;
}
```

## Step 4: Integration Checklist

- [ ] Add `uploadAgent.js` to your services folder
- [ ] Add file path input (text field or Electron file picker)
- [ ] Add progress display component
- [ ] Wire up callbacks on page load
- [ ] Call `uploadAgent.connect()` on startup
- [ ] Handle connection status UI

## Message Types Reference

| Type | Direction | Purpose |
|------|-----------|---------|
| `config` | Agent → Frontend | Initial config on connect |
| `progress` | Agent → Frontend | Upload progress (every 500ms) |
| `chunk` | Agent → Frontend | Per-chunk status |
| `status` | Agent → Frontend | Status changes |
| `error` | Agent → Frontend | Error notifications |
| `start` | Frontend → Agent | Start upload |
| `pause` | Frontend → Agent | Pause upload |
| `resume` | Frontend → Agent | Resume upload |
| `cancel` | Frontend → Agent | Cancel upload |

## Troubleshooting

### "Not connected to agent"
1. Ensure Agent is running
2. Check if port 8765 is accessible
3. Verify no firewall blocking localhost

### Progress not updating
1. Check browser console for WebSocket errors
2. Verify callbacks are assigned before `connect()`
3. Ensure Agent is processing the upload

### File not found errors
1. Use absolute paths (e.g., `C:\Data\file.zip`)
2. Escape backslashes in strings: `C:\\Data\\file.zip`
3. Verify file exists and is accessible
