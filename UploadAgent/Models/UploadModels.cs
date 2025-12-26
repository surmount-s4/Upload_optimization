namespace UploadAgent.Models;

/// <summary>
/// Represents the current state of an upload job.
/// </summary>
public enum UploadStatus
{
    Pending,
    InProgress,
    Paused,
    Completed,
    Failed,
    Cancelled
}

/// <summary>
/// Represents the status of an individual chunk/part.
/// </summary>
public enum ChunkStatus
{
    Pending,
    Uploading,
    Completed,
    Failed
}

/// <summary>
/// Represents a complete upload job with all metadata.
/// </summary>
public class UploadJob
{
    public string UploadId { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string FileFingerprint { get; set; } = string.Empty;
    public string Bucket { get; set; } = string.Empty;
    public string ObjectKey { get; set; } = string.Empty;
    public int ChunkSizeBytes { get; set; }
    public int TotalParts { get; set; }
    public UploadStatus Status { get; set; } = UploadStatus.Pending;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}

/// <summary>
/// Represents a single chunk/part of the upload.
/// </summary>
public class ChunkInfo
{
    public string UploadId { get; set; } = string.Empty;
    public int PartNumber { get; set; }
    public long ByteOffset { get; set; }
    public int ByteLength { get; set; }
    public string? ETag { get; set; }
    public ChunkStatus Status { get; set; } = ChunkStatus.Pending;
    public int RetryCount { get; set; } = 0;
    public string? PresignedUrl { get; set; }
    public DateTime? UrlExpiresAt { get; set; }
}

/// <summary>
/// Presigned URL information from backend.
/// </summary>
public class PresignedUrlInfo
{
    public int PartNumber { get; set; }
    public string Url { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
}
