namespace UploadAgent.Models;

using System.Text.Json.Serialization;

/// <summary>
/// Base class for all WebSocket messages.
/// </summary>
public abstract class WsMessage
{
    [JsonPropertyName("type")]
    public abstract string Type { get; }
}

/// <summary>
/// Progress update sent to frontend.
/// </summary>
public class ProgressMessage : WsMessage
{
    [JsonPropertyName("type")]
    public override string Type => "progress";

    [JsonPropertyName("uploadId")]
    public string UploadId { get; set; } = string.Empty;

    [JsonPropertyName("percent")]
    public double Percent { get; set; }

    [JsonPropertyName("speed")]
    public long Speed { get; set; } // bytes/sec

    [JsonPropertyName("eta")]
    public int Eta { get; set; } // seconds remaining

    [JsonPropertyName("bytesTransferred")]
    public long BytesTransferred { get; set; }

    [JsonPropertyName("totalBytes")]
    public long TotalBytes { get; set; }

    [JsonPropertyName("activeThreads")]
    public int ActiveThreads { get; set; }

    [JsonPropertyName("completedParts")]
    public int CompletedParts { get; set; }

    [JsonPropertyName("totalParts")]
    public int TotalParts { get; set; }
}

/// <summary>
/// Per-chunk status update.
/// </summary>
public class ChunkMessage : WsMessage
{
    [JsonPropertyName("type")]
    public override string Type => "chunk";

    [JsonPropertyName("uploadId")]
    public string UploadId { get; set; } = string.Empty;

    [JsonPropertyName("partNumber")]
    public int PartNumber { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("etag")]
    public string? ETag { get; set; }
}

/// <summary>
/// Overall status change notification.
/// </summary>
public class StatusMessage : WsMessage
{
    [JsonPropertyName("type")]
    public override string Type => "status";

    [JsonPropertyName("uploadId")]
    public string UploadId { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Error notification.
/// </summary>
public class ErrorMessage : WsMessage
{
    [JsonPropertyName("type")]
    public override string Type => "error";

    [JsonPropertyName("uploadId")]
    public string? UploadId { get; set; }

    [JsonPropertyName("error")]
    public string Error { get; set; } = string.Empty;

    [JsonPropertyName("code")]
    public string? Code { get; set; }
}

/// <summary>
/// Configuration info sent to frontend on connect.
/// </summary>
public class ConfigMessage : WsMessage
{
    [JsonPropertyName("type")]
    public override string Type => "config";

    [JsonPropertyName("chunkSizeMB")]
    public int ChunkSizeMB { get; set; }

    [JsonPropertyName("maxThreads")]
    public int MaxThreads { get; set; }

    [JsonPropertyName("presignBatchSize")]
    public int PresignBatchSize { get; set; }

    [JsonPropertyName("wsPort")]
    public int WsPort { get; set; }
}

/// <summary>
/// Command received from frontend.
/// </summary>
public class WsCommand
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    [JsonPropertyName("filePath")]
    public string? FilePath { get; set; }

    [JsonPropertyName("uploadId")]
    public string? UploadId { get; set; }

    [JsonPropertyName("backendUrl")]
    public string? BackendUrl { get; set; }
}
