"""
Upload Pipeline Backend Server

FastAPI application for orchestrating multipart uploads to MinIO.
"""

import os
from fastapi import FastAPI
from fastapi.middleware.cors import CORSMiddleware
from dotenv import load_dotenv

# Load environment variables
load_dotenv()

# Import routers
from endpoints.upload import router as upload_router

# Create FastAPI app
app = FastAPI(
    title="Upload Pipeline API",
    description="Backend orchestrator for high-throughput file uploads to MinIO",
    version="1.0.0"
)

# CORS configuration - allow Agent to connect
app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],  # In production, restrict to specific origins
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)

# Include routers
app.include_router(upload_router)


@app.get("/")
async def root():
    """Health check endpoint."""
    return {"status": "ok", "service": "Upload Pipeline API"}


@app.get("/health")
async def health():
    """Detailed health check."""
    from services.minio_multipart import get_minio_service
    
    try:
        service = get_minio_service()
        # Try to check bucket exists
        bucket_ok = service.client.bucket_exists(service.default_bucket)
        return {
            "status": "healthy",
            "minio": "connected" if bucket_ok else "bucket missing",
            "bucket": service.default_bucket
        }
    except Exception as e:
        return {
            "status": "unhealthy",
            "error": str(e)
        }


if __name__ == "__main__":
    import uvicorn
    
    host = os.getenv("HOST", "0.0.0.0")
    port = int(os.getenv("PORT", "8000"))
    
    uvicorn.run(app, host=host, port=port)
