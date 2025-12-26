# Upload Pipeline Backend Documentation

Python FastAPI backend for orchestrating multipart uploads to MinIO.

## Architecture

```
┌───────────────────────────────────────────────────────────────┐
│                      Backend (Orchestrator)                   │
├───────────────────────────────────────────────────────────────┤
│  ┌──────────────────┐    ┌────────────────────────────────┐  │
│  │   FastAPI App    │──▶ │   MinIO Multipart Service      │  │
│  │  (main.py)       │    │   (minio_multipart.py)         │  │
│  └────────┬─────────┘    └─────────────┬──────────────────┘  │
│           │                            │                      │
│  ┌────────┴─────────┐                  │                      │
│  │  Upload Router   │                  │                      │
│  │  (upload.py)     │                  │                      │
│  └──────────────────┘                  │                      │
└───────────────────────────────────────┬───────────────────────┘
                                        │
                                        ▼
                                   [MinIO Server]
```

## Project Structure

```
backend/
├── main.py                    # FastAPI application entry point
├── requirements.txt           # Python dependencies
├── .env.example               # Environment template
├── endpoints/
│   ├── __init__.py
│   └── upload.py              # REST API endpoints
└── services/
    ├── __init__.py
    └── minio_multipart.py     # MinIO operations
```

## Prerequisites

- Python 3.10+
- MinIO Server (running and accessible)
- pip or pipenv

## Installation

### 1. Create Virtual Environment

```bash
cd backend
python -m venv venv
source venv/bin/activate  # On Windows: venv\Scripts\activate
```

### 2. Install Dependencies

```bash
pip install -r requirements.txt
```

### 3. Configure Environment

```bash
cp .env.example .env
# Edit .env with your MinIO credentials
```

### 4. Run the Server

```bash
# Development
uvicorn main:app --reload --host 0.0.0.0 --port 8000

# Or directly
python main.py
```

## Configuration

| Variable | Default | Description |
|----------|---------|-------------|
| `MINIO_ENDPOINT` | localhost:9000 | MinIO server address |
| `MINIO_ACCESS_KEY` | minioadmin | MinIO access key |
| `MINIO_SECRET_KEY` | minioadmin | MinIO secret key |
| `MINIO_SECURE` | false | Use HTTPS |
| `MINIO_BUCKET` | uploads | Default bucket |
| `CHUNK_SIZE_MB` | 128 | Default chunk size |
| `PRESIGN_EXPIRY_HOURS` | 1 | URL validity |
| `HOST` | 0.0.0.0 | Server bind address |
| `PORT` | 8000 | Server port |

## API Reference

### Health Check

```http
GET /
GET /health
```

### Initiate Upload

```http
POST /api/upload/initiate
Content-Type: application/json

{
  "file_name": "large_file.zip",
  "file_size": 214748364800,
  "file_fingerprint": "sha256:abc123...",
  "content_type": "application/zip"
}
```

**Response:**
```json
{
  "upload_id": "2NP4...",
  "bucket": "uploads",
  "object_key": "20231225_120000_large_file.zip",
  "chunk_size": 134217728,
  "total_parts": 1600
}
```

### Get Presigned URLs

```http
GET /api/upload/presign?upload_id=XXX&bucket=uploads&object_key=XXX&part_numbers=1,2,3,4,5
```

**Response:**
```json
{
  "urls": [
    {
      "part_number": 1,
      "url": "https://minio:9000/uploads/...?X-Amz-Signature=...",
      "expires_at": "2023-12-25T13:00:00Z"
    }
  ]
}
```

### Complete Upload

```http
POST /api/upload/complete
Content-Type: application/json

{
  "upload_id": "2NP4...",
  "bucket": "uploads",
  "object_key": "20231225_120000_large_file.zip",
  "parts": [
    {"part_number": 1, "etag": "\"abc123\""},
    {"part_number": 2, "etag": "\"def456\""}
  ]
}
```

**Response:**
```json
{
  "status": "completed",
  "final_etag": "\"xyz789-1600\"",
  "verified": true
}
```

### Abort Upload

```http
POST /api/upload/abort
Content-Type: application/json

{
  "upload_id": "2NP4...",
  "bucket": "uploads",
  "object_key": "20231225_120000_large_file.zip"
}
```

## Dynamic Chunk Size Calculation

The service automatically calculates optimal chunk size:

```python
# For 200 GB file with default 128 MB chunks:
# 200 GB / 128 MB = 1600 parts (within 10,000 limit) ✓

# For 1 TB file:
# 1 TB / 128 MB = 8192 parts (within limit) ✓

# For 5 TB file:
# 5 TB / 128 MB = 40960 parts (exceeds limit!) ✗
# → Auto-increases to 512 MB chunks
# 5 TB / 512 MB = 10240 parts... still too many
# → Further adjustment to get under 10,000 parts
```

## MinIO CORS Configuration

Ensure MinIO allows PUT requests from the Agent:

```json
{
  "CORSRules": [
    {
      "AllowedOrigins": ["*"],
      "AllowedMethods": ["GET", "PUT", "POST", "DELETE"],
      "AllowedHeaders": ["*"],
      "ExposeHeaders": ["ETag"],
      "MaxAgeSeconds": 3600
    }
  ]
}
```

Apply with:
```bash
mc admin config set myminio api cors='{"enabled":"on"}'
# Or use mc policy for specific bucket
```

## Troubleshooting

### Connection refused to MinIO
- Verify MinIO is running: `curl http://localhost:9000/minio/health/live`
- Check `MINIO_ENDPOINT` in .env
- Ensure no firewall blocking

### Presigned URLs expire too quickly
- Increase `PRESIGN_EXPIRY_HOURS`
- Check clock sync between servers

### Upload completes but file is corrupted
- Verify all ETags are captured correctly
- Check network stability during upload
