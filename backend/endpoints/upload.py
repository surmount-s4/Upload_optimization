"""
Upload API Endpoints

Provides REST endpoints for the upload pipeline:
- POST /api/upload/initiate - Start multipart upload
- GET /api/upload/presign - Get presigned URLs for parts
- POST /api/upload/complete - Complete upload
- POST /api/upload/abort - Cancel upload
"""

from fastapi import APIRouter, HTTPException, Query
from pydantic import BaseModel
from typing import List, Optional
from services.minio_multipart import get_minio_service

router = APIRouter(prefix="/api/upload", tags=["upload"])


# Request/Response Models

class InitiateRequest(BaseModel):
    file_name: str
    file_size: int
    file_fingerprint: str
    content_type: str = "application/octet-stream"


class InitiateResponse(BaseModel):
    upload_id: str
    bucket: str
    object_key: str
    chunk_size: int
    total_parts: int


class PresignedUrl(BaseModel):
    part_number: int
    url: str
    expires_at: str


class PresignResponse(BaseModel):
    urls: List[PresignedUrl]


class PartInfo(BaseModel):
    part_number: int
    etag: str


class CompleteRequest(BaseModel):
    upload_id: str
    bucket: str
    object_key: str
    parts: List[PartInfo]


class CompleteResponse(BaseModel):
    status: str
    final_etag: Optional[str] = None
    verified: bool = False


class AbortRequest(BaseModel):
    upload_id: str
    bucket: str
    object_key: str


# Endpoints

@router.post("/initiate", response_model=InitiateResponse)
async def initiate_upload(request: InitiateRequest):
    """
    Initiate a new multipart upload.
    
    Returns upload_id and configuration for the agent to use.
    """
    try:
        service = get_minio_service()
        result = service.initiate_upload(
            file_name=request.file_name,
            file_size=request.file_size,
            content_type=request.content_type
        )
        
        return InitiateResponse(
            upload_id=result["upload_id"],
            bucket=result["bucket"],
            object_key=result["object_key"],
            chunk_size=result["chunk_size"],
            total_parts=result["total_parts"]
        )
    except Exception as e:
        raise HTTPException(status_code=500, detail=str(e))


@router.get("/presign", response_model=PresignResponse)
async def get_presigned_urls(
    upload_id: str = Query(..., description="Multipart upload ID"),
    bucket: str = Query(..., description="Bucket name"),
    object_key: str = Query(..., description="Object key"),
    part_numbers: str = Query(..., description="Comma-separated part numbers")
):
    """
    Generate presigned URLs for a batch of parts.
    
    Agent requests URLs in batches (e.g., 20 at a time) as it progresses.
    """
    try:
        # Parse part numbers
        parts = [int(p.strip()) for p in part_numbers.split(",") if p.strip()]
        
        if not parts:
            raise HTTPException(status_code=400, detail="No valid part numbers provided")
        
        if len(parts) > 100:
            raise HTTPException(status_code=400, detail="Maximum 100 parts per request")
        
        service = get_minio_service()
        urls = service.generate_batch_presigned_urls(
            bucket=bucket,
            object_key=object_key,
            upload_id=upload_id,
            part_numbers=parts
        )
        
        return PresignResponse(
            urls=[PresignedUrl(**u) for u in urls]
        )
    except ValueError:
        raise HTTPException(status_code=400, detail="Invalid part numbers format")
    except Exception as e:
        raise HTTPException(status_code=500, detail=str(e))


@router.post("/complete", response_model=CompleteResponse)
async def complete_upload(request: CompleteRequest):
    """
    Complete the multipart upload.
    
    Called after all parts are uploaded. MinIO will stitch the parts together.
    """
    try:
        service = get_minio_service()
        
        # Convert to list of dicts
        parts = [{"part_number": p.part_number, "etag": p.etag} for p in request.parts]
        
        result = service.complete_upload(
            bucket=request.bucket,
            object_key=request.object_key,
            upload_id=request.upload_id,
            parts=parts
        )
        
        return CompleteResponse(
            status=result["status"],
            final_etag=result.get("final_etag"),
            verified=result.get("verified", False)
        )
    except Exception as e:
        raise HTTPException(status_code=500, detail=str(e))


@router.post("/abort")
async def abort_upload(request: AbortRequest):
    """
    Abort/cancel a multipart upload.
    
    Cleans up any uploaded parts from MinIO.
    """
    try:
        service = get_minio_service()
        success = service.abort_upload(
            bucket=request.bucket,
            object_key=request.object_key,
            upload_id=request.upload_id
        )
        
        if success:
            return {"status": "aborted"}
        else:
            raise HTTPException(status_code=500, detail="Failed to abort upload")
    except Exception as e:
        raise HTTPException(status_code=500, detail=str(e))
