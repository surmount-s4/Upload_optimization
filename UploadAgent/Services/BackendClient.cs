using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UploadAgent.Services;

/// <summary>
/// Client for communicating with the backend orchestrator API.
/// Handles upload initiation, presigned URL requests, and completion.
/// </summary>
public class BackendClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<BackendClient> _logger;
    private string _baseUrl;

    public BackendClient(AppConfig config, ILogger<BackendClient> logger)
    {
        _baseUrl = config.BackendUrl;
        _logger = logger;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    public void SetBaseUrl(string url)
    {
        _baseUrl = url.TrimEnd('/');
    }

    /// <summary>
    /// Initiate a new multipart upload.
    /// </summary>
    public async Task<InitiateResponse?> InitiateUploadAsync(
        string fileName,
        long fileSize,
        string fingerprint,
        string contentType = "application/octet-stream",
        CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new InitiateRequest
            {
                FileName = fileName,
                FileSize = fileSize,
                FileFingerprint = fingerprint,
                ContentType = contentType
            };

            var json = JsonSerializer.Serialize(request);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            
            var response = await _httpClient.PostAsync(
                $"{_baseUrl}/api/upload/initiate",
                content,
                cancellationToken
            );

            if (response.IsSuccessStatusCode)
            {
                var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
                return JsonSerializer.Deserialize<InitiateResponse>(responseJson);
            }
            else
            {
                _logger.LogError("Initiate failed: {StatusCode}", response.StatusCode);
                return null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initiate upload");
            return null;
        }
    }

    /// <summary>
    /// Get presigned URLs for a batch of parts.
    /// </summary>
    public async Task<List<PresignedUrlResponse>?> GetPresignedUrlsAsync(
        string uploadId,
        string bucket,
        string objectKey,
        List<int> partNumbers,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var partList = string.Join(",", partNumbers);
            var url = $"{_baseUrl}/api/upload/presign?upload_id={uploadId}&bucket={bucket}&object_key={objectKey}&part_numbers={partList}";
            
            var response = await _httpClient.GetAsync(url, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var result = JsonSerializer.Deserialize<PresignBatchResponse>(json);
                return result?.Urls;
            }
            else
            {
                _logger.LogError("Presign failed: {StatusCode}", response.StatusCode);
                return null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get presigned URLs");
            return null;
        }
    }

    /// <summary>
    /// Complete the multipart upload.
    /// </summary>
    public async Task<CompleteResponse?> CompleteUploadAsync(
        string uploadId,
        string bucket,
        string objectKey,
        List<(int PartNumber, string ETag)> parts,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new CompleteRequest
            {
                UploadId = uploadId,
                Bucket = bucket,
                ObjectKey = objectKey,
                Parts = parts.Select(p => new PartInfo 
                { 
                    PartNumber = p.PartNumber, 
                    ETag = p.ETag 
                }).ToList()
            };

            var json = JsonSerializer.Serialize(request);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            
            var response = await _httpClient.PostAsync(
                $"{_baseUrl}/api/upload/complete",
                content,
                cancellationToken
            );

            if (response.IsSuccessStatusCode)
            {
                var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
                return JsonSerializer.Deserialize<CompleteResponse>(responseJson);
            }
            else
            {
                _logger.LogError("Complete failed: {StatusCode}", response.StatusCode);
                return null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to complete upload");
            return null;
        }
    }

    /// <summary>
    /// Abort the multipart upload.
    /// </summary>
    public async Task<bool> AbortUploadAsync(
        string uploadId,
        string bucket,
        string objectKey,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new { upload_id = uploadId, bucket, object_key = objectKey };
            var json = JsonSerializer.Serialize(request);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            
            var response = await _httpClient.PostAsync(
                $"{_baseUrl}/api/upload/abort",
                content,
                cancellationToken
            );

            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to abort upload");
            return false;
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    // Request/Response DTOs
    public class InitiateRequest
    {
        [JsonPropertyName("file_name")]
        public string FileName { get; set; } = string.Empty;
        
        [JsonPropertyName("file_size")]
        public long FileSize { get; set; }
        
        [JsonPropertyName("file_fingerprint")]
        public string FileFingerprint { get; set; } = string.Empty;
        
        [JsonPropertyName("content_type")]
        public string ContentType { get; set; } = "application/octet-stream";
    }

    public class InitiateResponse
    {
        [JsonPropertyName("upload_id")]
        public string UploadId { get; set; } = string.Empty;
        
        [JsonPropertyName("bucket")]
        public string Bucket { get; set; } = string.Empty;
        
        [JsonPropertyName("object_key")]
        public string ObjectKey { get; set; } = string.Empty;
        
        [JsonPropertyName("chunk_size")]
        public int ChunkSize { get; set; }
        
        [JsonPropertyName("total_parts")]
        public int TotalParts { get; set; }
    }

    public class PresignBatchResponse
    {
        [JsonPropertyName("urls")]
        public List<PresignedUrlResponse>? Urls { get; set; }
    }

    public class PresignedUrlResponse
    {
        [JsonPropertyName("part_number")]
        public int PartNumber { get; set; }
        
        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;
        
        [JsonPropertyName("expires_at")]
        public string ExpiresAt { get; set; } = string.Empty;
    }

    public class CompleteRequest
    {
        [JsonPropertyName("upload_id")]
        public string UploadId { get; set; } = string.Empty;
        
        [JsonPropertyName("bucket")]
        public string Bucket { get; set; } = string.Empty;
        
        [JsonPropertyName("object_key")]
        public string ObjectKey { get; set; } = string.Empty;
        
        [JsonPropertyName("parts")]
        public List<PartInfo> Parts { get; set; } = new();
    }

    public class PartInfo
    {
        [JsonPropertyName("part_number")]
        public int PartNumber { get; set; }
        
        [JsonPropertyName("etag")]
        public string ETag { get; set; } = string.Empty;
    }

    public class CompleteResponse
    {
        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;
        
        [JsonPropertyName("final_etag")]
        public string? FinalETag { get; set; }
        
        [JsonPropertyName("verified")]
        public bool Verified { get; set; }
    }
}
