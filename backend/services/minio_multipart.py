"""
MinIO Multipart Upload Service

Handles multipart upload operations:
- Initiate uploads
- Generate presigned URLs for parts
- Complete/abort uploads
- Calculate optimal chunk sizes
"""

import os
import math
from datetime import timedelta, datetime
from typing import List, Dict, Optional
from minio import Minio
from minio.error import S3Error
from dotenv import load_dotenv

load_dotenv()


class MinioMultipartService:
    """Service for managing MinIO multipart uploads."""
    
    def __init__(self):
        self.client = Minio(
            endpoint=os.getenv("MINIO_ENDPOINT", "localhost:9000"),
            access_key=os.getenv("MINIO_ACCESS_KEY", "minioadmin"),
            secret_key=os.getenv("MINIO_SECRET_KEY", "minioadmin"),
            secure=os.getenv("MINIO_SECURE", "false").lower() == "true"
        )
        self.default_bucket = os.getenv("MINIO_BUCKET", "uploads")
        self.chunk_size_mb = int(os.getenv("CHUNK_SIZE_MB", "128"))
        self.max_parts = int(os.getenv("MAX_PARTS", "10000"))
        self.presign_expiry_hours = int(os.getenv("PRESIGN_EXPIRY_HOURS", "24"))
        
        # Ensure bucket exists
        self._ensure_bucket()
    
    def _ensure_bucket(self):
        """Create the default bucket if it doesn't exist."""
        try:
            if not self.client.bucket_exists(self.default_bucket):
                self.client.make_bucket(self.default_bucket)
        except S3Error as e:
            print(f"Warning: Could not ensure bucket exists: {e}")
    
    def calculate_optimal_chunk_size(self, file_size: int) -> int:
        """
        Dynamically calculate optimal chunk size based on file size.
        
        Args:
            file_size: Total file size in bytes
            
        Returns:
            Optimal chunk size in bytes
        """
        min_chunk = 5 * 1024 * 1024      # 5 MB minimum (S3/MinIO requirement)
        max_chunk = 512 * 1024 * 1024    # 512 MB maximum
        preferred = self.chunk_size_mb * 1024 * 1024
        
        # Check if preferred size works within part limit
        parts_needed = math.ceil(file_size / preferred)
        
        if parts_needed <= self.max_parts:
            return preferred
        
        # Need larger chunks - calculate minimum required
        min_required = math.ceil(file_size / self.max_parts)
        
        # Round up to nearest 16 MB for alignment
        aligned = ((min_required // (16 * 1024 * 1024)) + 1) * (16 * 1024 * 1024)
        
        return min(aligned, max_chunk)
    
    def initiate_upload(
        self,
        file_name: str,
        file_size: int,
        content_type: str = "application/octet-stream",
        bucket: Optional[str] = None,
        object_key: Optional[str] = None
    ) -> Dict:
        """
        Initiate a new multipart upload.
        
        Args:
            file_name: Original file name
            file_size: Total file size in bytes
            content_type: MIME type of the file
            bucket: Target bucket (defaults to configured bucket)
            object_key: Object key (defaults to file_name with timestamp)
            
        Returns:
            Dict with upload_id, bucket, object_key, chunk_size, total_parts
        """
        bucket = bucket or self.default_bucket
        
        # Generate unique object key if not provided
        if not object_key:
            timestamp = datetime.utcnow().strftime("%Y%m%d_%H%M%S")
            object_key = f"{timestamp}_{file_name}"
        
        # Calculate optimal chunk size and total parts
        chunk_size = self.calculate_optimal_chunk_size(file_size)
        total_parts = math.ceil(file_size / chunk_size)
        
        # Initiate multipart upload using MinIO's internal API
        # The MinIO Python client doesn't directly expose create_multipart_upload
        # We'll use presigned URLs which work with the multipart mechanism
        upload_id = self._create_multipart_upload(bucket, object_key, content_type)
        
        return {
            "upload_id": upload_id,
            "bucket": bucket,
            "object_key": object_key,
            "chunk_size": chunk_size,
            "total_parts": total_parts
        }
    
    def _create_multipart_upload(
        self,
        bucket: str,
        object_key: str,
        content_type: str
    ) -> str:
        """
        Create a multipart upload and return the upload ID.
        Uses MinIO's internal API.
        """
        # MinIO client's internal method for multipart upload
        from minio.api import _DEFAULT_USER_AGENT
        import urllib.parse
        
        # Build request
        url = self.client._base_url._url.geturl()
        headers = {"Content-Type": content_type}
        
        # Use the internal _execute method to initiate multipart upload
        response = self.client._execute(
            "POST",
            bucket,
            object_key,
            headers=headers,
            query_params={"uploads": ""}
        )
        
        # Parse the response to get upload ID
        from xml.etree import ElementTree
        root = ElementTree.fromstring(response.data.decode())
        
        # Handle namespace
        ns = {"s3": "http://s3.amazonaws.com/doc/2006-03-01/"}
        upload_id_elem = root.find(".//s3:UploadId", ns)
        
        if upload_id_elem is None:
            # Try without namespace
            upload_id_elem = root.find(".//UploadId")
        
        if upload_id_elem is not None:
            return upload_id_elem.text
        
        raise Exception("Failed to get upload ID from response")
    
    def generate_presigned_url_for_part(
        self,
        bucket: str,
        object_key: str,
        upload_id: str,
        part_number: int,
        expires: Optional[timedelta] = None
    ) -> str:
        """
        Generate a presigned URL for uploading a specific part.
        
        Args:
            bucket: Bucket name
            object_key: Object key
            upload_id: Multipart upload ID
            part_number: Part number (1-indexed)
            expires: URL expiry duration
            
        Returns:
            Presigned PUT URL for the part
        """
        expires = expires or timedelta(hours=self.presign_expiry_hours)
        
        # Generate presigned URL with multipart query params
        url = self.client.get_presigned_url(
            method="PUT",
            bucket_name=bucket,
            object_name=object_key,
            expires=expires,
            extra_query_params={
                "uploadId": upload_id,
                "partNumber": str(part_number)
            }
        )
        
        return url
    
    def generate_batch_presigned_urls(
        self,
        bucket: str,
        object_key: str,
        upload_id: str,
        part_numbers: List[int],
        expires: Optional[timedelta] = None
    ) -> List[Dict]:
        """
        Generate presigned URLs for multiple parts at once.
        
        Args:
            bucket: Bucket name
            object_key: Object key
            upload_id: Multipart upload ID
            part_numbers: List of part numbers to generate URLs for
            expires: URL expiry duration
            
        Returns:
            List of dicts with part_number, url, and expires_at
        """
        expires = expires or timedelta(hours=self.presign_expiry_hours)
        expires_at = datetime.utcnow() + expires
        
        result = []
        for pn in part_numbers:
            url = self.generate_presigned_url_for_part(
                bucket, object_key, upload_id, pn, expires
            )
            result.append({
                "part_number": pn,
                "url": url,
                "expires_at": expires_at.isoformat() + "Z"
            })
        
        return result
    
    def complete_upload(
        self,
        bucket: str,
        object_key: str,
        upload_id: str,
        parts: List[Dict]
    ) -> Dict:
        """
        Complete the multipart upload.
        
        Args:
            bucket: Bucket name
            object_key: Object key
            upload_id: Multipart upload ID
            parts: List of {part_number, etag} dicts
            
        Returns:
            Dict with status and final_etag
        """
        from xml.etree import ElementTree
        
        # Sort parts by part number
        sorted_parts = sorted(parts, key=lambda x: x["part_number"])
        
        # Build XML payload for completing multipart upload
        root = ElementTree.Element("CompleteMultipartUpload")
        for part in sorted_parts:
            part_elem = ElementTree.SubElement(root, "Part")
            pn_elem = ElementTree.SubElement(part_elem, "PartNumber")
            pn_elem.text = str(part["part_number"])
            etag_elem = ElementTree.SubElement(part_elem, "ETag")
            etag_elem.text = part["etag"]
        
        body = ElementTree.tostring(root, encoding="unicode")
        
        # Execute complete multipart request
        response = self.client._execute(
            "POST",
            bucket,
            object_key,
            body=body.encode(),
            headers={"Content-Type": "application/xml"},
            query_params={"uploadId": upload_id}
        )
        
        # Parse response
        result_root = ElementTree.fromstring(response.data.decode())
        
        # Get ETag from response
        ns = {"s3": "http://s3.amazonaws.com/doc/2006-03-01/"}
        etag_elem = result_root.find(".//s3:ETag", ns)
        if etag_elem is None:
            etag_elem = result_root.find(".//ETag")
        
        final_etag = etag_elem.text if etag_elem is not None else None
        
        return {
            "status": "completed",
            "final_etag": final_etag,
            "verified": True
        }
    
    def abort_upload(
        self,
        bucket: str,
        object_key: str,
        upload_id: str
    ) -> bool:
        """
        Abort/cancel a multipart upload.
        
        Args:
            bucket: Bucket name
            object_key: Object key
            upload_id: Multipart upload ID
            
        Returns:
            True if successful
        """
        try:
            self.client._execute(
                "DELETE",
                bucket,
                object_key,
                query_params={"uploadId": upload_id}
            )
            return True
        except S3Error:
            return False


# Singleton instance
_minio_service: Optional[MinioMultipartService] = None


def get_minio_service() -> MinioMultipartService:
    """Get or create the MinIO service singleton."""
    global _minio_service
    if _minio_service is None:
        _minio_service = MinioMultipartService()
    return _minio_service
