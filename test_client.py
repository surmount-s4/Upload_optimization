"""
Simple WebSocket Test Client for Upload Agent

Usage:
    pip install websockets
    python test_client.py

This script connects to the Upload Agent and allows you to test
uploads without integrating with your frontend.
"""

import asyncio
import websockets
import json
import sys

async def test_upload():
    uri = "ws://localhost:8765"
    
    print("=" * 50)
    print("Upload Agent Test Client")
    print("=" * 50)
    
    try:
        async with websockets.connect(uri) as ws:
            print("✅ Connected to Agent!")
            
            # Get file path from user
            print("\nEnter the full path to the file you want to upload.")
            print("Example: C:\\Users\\Swaraj\\Downloads\\testfile.zip")
            file_path = input("\nFile path: ").strip()
            
            if not file_path:
                print("❌ No file path provided")
                return
            
            # Backend URL
            backend_url = input("Backend URL [http://localhost:8000]: ").strip()
            if not backend_url:
                backend_url = "http://localhost:8000"
            
            # Send start command
            command = {
                "action": "start",
                "filePath": file_path,
                "backendUrl": backend_url
            }
            await ws.send(json.dumps(command))
            print("\n📤 Upload command sent! Waiting for progress...\n")
            
            upload_id = None
            
            # Listen for updates
            try:
                while True:
                    message = await asyncio.wait_for(ws.recv(), timeout=30.0)
                    data = json.loads(message)
                    
                    msg_type = data.get("type", "unknown")
                    
                    if msg_type == "config":
                        print(f"⚙️  Config received: ChunkSize={data.get('chunkSizeMB')}MB, "
                              f"Threads={data.get('maxThreads')}")
                    
                    elif msg_type == "progress":
                        percent = data.get('percent', 0)
                        speed_mbs = data.get('speed', 0) / 1024 / 1024
                        completed = data.get('completedParts', 0)
                        total = data.get('totalParts', 0)
                        eta = data.get('eta', 0)
                        
                        # Format ETA as HH:MM:SS
                        eta_h = eta // 3600
                        eta_m = (eta % 3600) // 60
                        eta_s = eta % 60
                        eta_str = f"{eta_h:02d}:{eta_m:02d}:{eta_s:02d}"
                        
                        # Print progress bar
                        bar_width = 30
                        filled = int(bar_width * percent / 100)
                        bar = "█" * filled + "░" * (bar_width - filled)
                        
                        sys.stdout.write(f"\r[{bar}] {percent:5.1f}% | "
                                        f"{speed_mbs:6.1f} MB/s | "
                                        f"ETA: {eta_str} | "
                                        f"Parts: {completed}/{total}")
                        sys.stdout.flush()
                    
                    elif msg_type == "status":
                        upload_id = data.get("uploadId", upload_id)
                        status = data.get("status", "")
                        message = data.get("message", "")
                        
                        print(f"\n📋 Status: {status} - {message}")
                        
                        if status == "completed":
                            print("\n" + "=" * 50)
                            print("✅ UPLOAD COMPLETE!")
                            print("=" * 50)
                            print(f"Upload ID: {upload_id}")
                            print("Check MinIO console to verify the file.")
                            break
                    
                    elif msg_type == "error":
                        error = data.get("error", "Unknown error")
                        code = data.get("code", "")
                        print(f"\n❌ ERROR [{code}]: {error}")
                        break
                    
                    elif msg_type == "chunk":
                        # Only show failures
                        if data.get("status") == "failed":
                            part = data.get("partNumber", "?")
                            print(f"\n⚠️  Chunk {part} failed (will retry)")
                            
            except asyncio.TimeoutError:
                print("\n\n⚠️  No messages received for 30 seconds")
                print("The upload may have stalled. Check the Agent logs.")
                
    except ConnectionRefusedError:
        print("❌ Cannot connect to Agent at ws://localhost:8765")
        print("   Make sure the Upload Agent is running.")
        print("   Run: cd UploadAgent && dotnet run")
    except Exception as e:
        print(f"❌ Error: {e}")


async def interactive_mode():
    """Interactive mode for pause/resume/cancel"""
    uri = "ws://localhost:8765"
    
    async with websockets.connect(uri) as ws:
        print("Interactive Mode - Enter commands:")
        print("  start <filepath>")
        print("  pause <uploadId>")
        print("  resume <uploadId>")
        print("  cancel <uploadId>")
        print("  quit")
        
        # Start receiver task
        async def receiver():
            while True:
                try:
                    msg = await ws.recv()
                    data = json.loads(msg)
                    if data["type"] == "progress":
                        print(f"\r[Progress: {data['percent']:.1f}%]", end="")
                    else:
                        print(f"\n[{data['type']}] {data}")
                except:
                    break
        
        recv_task = asyncio.create_task(receiver())
        
        while True:
            try:
                cmd = await asyncio.get_event_loop().run_in_executor(None, input, "> ")
                parts = cmd.strip().split(maxsplit=1)
                
                if not parts:
                    continue
                    
                action = parts[0].lower()
                
                if action == "quit":
                    break
                elif action == "start" and len(parts) > 1:
                    await ws.send(json.dumps({
                        "action": "start",
                        "filePath": parts[1],
                        "backendUrl": "http://localhost:8000"
                    }))
                elif action in ["pause", "resume", "cancel"] and len(parts) > 1:
                    await ws.send(json.dumps({
                        "action": action,
                        "uploadId": parts[1]
                    }))
                else:
                    print("Invalid command")
            except EOFError:
                break
        
        recv_task.cancel()


if __name__ == "__main__":
    print("Upload Pipeline Test Client")
    print("-" * 30)
    
    if len(sys.argv) > 1 and sys.argv[1] == "--interactive":
        asyncio.run(interactive_mode())
    else:
        asyncio.run(test_upload())
