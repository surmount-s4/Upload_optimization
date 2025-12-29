using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using UploadAgent.Models;

namespace UploadAgent.Services;

/// <summary>
/// Parallel upload worker pool with presigned URL queue management.
/// Handles multi-threaded chunk uploads with retry logic.
/// </summary>
public class UploadWorkerPool : IDisposable
{
    private readonly AppConfig _config;
    private readonly StateManifest _manifest;
    private readonly FileProcessor _fileProcessor;
    private readonly ILogger<UploadWorkerPool> _logger;
    private readonly HttpClient _httpClient;
    
    // Progress tracking
    private long _bytesTransferred;
    private readonly Stopwatch _speedTimer = new();
    private readonly ConcurrentQueue<long> _speedSamples = new();
    
    // Control
    private CancellationTokenSource? _uploadCts;
    private bool _isPaused;
    private readonly SemaphoreSlim _pauseSemaphore = new(1, 1);
    
    // Presigned URL queue
    private readonly ConcurrentQueue<PresignedUrlInfo> _urlQueue = new();
    
    // Events
    public event Action<ProgressMessage>? OnProgress;
    public event Action<ChunkMessage>? OnChunkUpdate;

    public UploadWorkerPool(
        AppConfig config,
        StateManifest manifest,
        FileProcessor fileProcessor,
        ILogger<UploadWorkerPool> logger)
    {
        _config = config;
        _manifest = manifest;
        _fileProcessor = fileProcessor;
        _logger = logger;
        
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(_config.HttpTimeoutSeconds)
        };
    }

    /// <summary>
    /// Execute upload for all pending chunks.
    /// </summary>
    public async Task ExecuteUploadAsync(
        UploadJob job,
        string backendUrl,
        CancellationToken cancellationToken)
    {
        _uploadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _bytesTransferred = CalculateInitialBytesTransferred(job.UploadId, job.ChunkSizeBytes);
        _speedTimer.Start();
        _isPaused = false;

        var pendingParts = _manifest.GetPendingParts(job.UploadId, _config.RetryMaxAttempts);
        
        if (pendingParts.Count == 0)
        {
            _logger.LogInformation("No pending parts to upload");
            return;
        }

        _logger.LogInformation("Starting upload with {ThreadCount} threads for {PartCount} parts",
            _config.OptimalThreadCount, pendingParts.Count);

        // Start URL prefetch task
        var urlFetchTask = PrefetchPresignedUrlsAsync(
            job, backendUrl, pendingParts.Select(p => p.PartNumber).ToList(), _uploadCts.Token);

        // Create worker tasks
        var partQueue = new ConcurrentQueue<ChunkInfo>(pendingParts);
        var workers = new Task[_config.OptimalThreadCount];
        
        for (int i = 0; i < workers.Length; i++)
        {
            workers[i] = WorkerLoopAsync(job, partQueue, _uploadCts.Token);
        }

        // Start progress reporting
        var progressTask = ReportProgressAsync(job, _uploadCts.Token);

        try
        {
            await Task.WhenAll(workers);
            await progressTask;
        }
        catch (OperationCanceledException) when (_uploadCts.IsCancellationRequested)
        {
            _logger.LogInformation("Upload cancelled");
            throw;
        }
        finally
        {
            _speedTimer.Stop();
        }
    }

    private async Task WorkerLoopAsync(
        UploadJob job,
        ConcurrentQueue<ChunkInfo> partQueue,
        CancellationToken cancellationToken)
    {
        while (partQueue.TryDequeue(out var chunk))
        {
            // Check for pause
            while (_isPaused && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(500, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();

            // Wait for presigned URL
            var url = await GetPresignedUrlAsync(chunk.PartNumber, cancellationToken);
            if (url == null)
            {
                _logger.LogError("Failed to get presigned URL for part {PartNumber}", chunk.PartNumber);
                _manifest.MarkPartFailed(job.UploadId, chunk.PartNumber);
                continue;
            }

            // Upload chunk with retry
            var success = await UploadChunkWithRetryAsync(job, chunk, url, cancellationToken);
            
            if (!success && chunk.RetryCount < _config.RetryMaxAttempts)
            {
                // Re-queue for retry
                chunk.RetryCount++;
                partQueue.Enqueue(chunk);
            }
        }
    }

    private async Task<bool> UploadChunkWithRetryAsync(
        UploadJob job,
        ChunkInfo chunk,
        string presignedUrl,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt <= _config.RetryMaxAttempts; attempt++)
        {
            try
            {
                OnChunkUpdate?.Invoke(new ChunkMessage
                {
                    UploadId = job.UploadId,
                    PartNumber = chunk.PartNumber,
                    Status = "uploading"
                });

                // Read chunk data
                var data = await _fileProcessor.ReadChunkAsync(chunk.ByteOffset, chunk.ByteLength, cancellationToken);

                // Upload to presigned URL
                using var content = new ByteArrayContent(data);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                
                using var response = await _httpClient.PutAsync(presignedUrl, content, cancellationToken);
                
                if (response.IsSuccessStatusCode)
                {
                    // Extract ETag from response
                    var etag = response.Headers.ETag?.Tag ?? $"\"{Guid.NewGuid()}\"";
                    
                    // Update manifest
                    _manifest.MarkPartCompleted(job.UploadId, chunk.PartNumber, etag);
                    
                    // Update progress
                    Interlocked.Add(ref _bytesTransferred, chunk.ByteLength);
                    
                    OnChunkUpdate?.Invoke(new ChunkMessage
                    {
                        UploadId = job.UploadId,
                        PartNumber = chunk.PartNumber,
                        Status = "completed",
                        ETag = etag
                    });

                    _logger.LogDebug("Part {PartNumber} completed with ETag {ETag}", chunk.PartNumber, etag);
                    return true;
                }
                else
                {
                    _logger.LogWarning("Part {PartNumber} failed with status {StatusCode}", 
                        chunk.PartNumber, response.StatusCode);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Part {PartNumber} upload failed, attempt {Attempt}", 
                    chunk.PartNumber, attempt + 1);
            }

            // Exponential backoff
            if (attempt < _config.RetryMaxAttempts)
            {
                var delay = Math.Min(
                    _config.RetryBaseDelayMs * (int)Math.Pow(2, attempt),
                    _config.RetryMaxDelayMs
                );
                await Task.Delay(delay, cancellationToken);
            }
        }

        _manifest.MarkPartFailed(job.UploadId, chunk.PartNumber);
        OnChunkUpdate?.Invoke(new ChunkMessage
        {
            UploadId = job.UploadId,
            PartNumber = chunk.PartNumber,
            Status = "failed"
        });
        
        return false;
    }

    private async Task PrefetchPresignedUrlsAsync(
        UploadJob job,
        string backendUrl,
        List<int> partNumbers,
        CancellationToken cancellationToken)
    {
        var batches = partNumbers
            .Chunk(_config.PresignBatchSize)
            .ToList();

        foreach (var batch in batches)
        {
            // Wait if queue is full
            while (_urlQueue.Count >= _config.PresignLookahead && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(100, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var partList = string.Join(",", batch);
                var url = $"{backendUrl}/api/upload/presign?upload_id={job.UploadId}&bucket={Uri.EscapeDataString(job.Bucket)}&object_key={Uri.EscapeDataString(job.ObjectKey)}&part_numbers={partList}";
                
                _logger.LogDebug("Fetching presigned URLs: {Url}", url);
                
                var response = await _httpClient.GetAsync(url, cancellationToken);
                
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync(cancellationToken);
                    var result = JsonSerializer.Deserialize<PresignResponse>(json);
                    
                    if (result?.Urls != null)
                    {
                        _logger.LogInformation("Received {Count} presigned URLs for parts: {Parts}", 
                            result.Urls.Count, partList);
                        
                        foreach (var urlInfo in result.Urls)
                        {
                            _urlQueue.Enqueue(new PresignedUrlInfo
                            {
                                PartNumber = urlInfo.PartNumber,
                                Url = urlInfo.Url,
                                ExpiresAt = DateTime.Parse(urlInfo.ExpiresAt)
                            });
                        }
                    }
                    else
                    {
                        _logger.LogWarning("Presign response had no URLs. JSON: {Json}", json);
                    }
                }
                else
                {
                    var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                    _logger.LogError("Presign request failed: {Status} - {Body}", response.StatusCode, errorBody);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch presigned URLs");
            }
        }
    }

    private async Task<string?> GetPresignedUrlAsync(int partNumber, CancellationToken cancellationToken)
    {
        // Try to get from queue with timeout
        var timeout = DateTime.UtcNow.AddSeconds(30);
        
        while (DateTime.UtcNow < timeout)
        {
            while (_urlQueue.TryDequeue(out var info))
            {
                if (info.PartNumber == partNumber && info.ExpiresAt > DateTime.UtcNow)
                {
                    return info.Url;
                }
                
                // Put back if not our part and not expired
                if (info.PartNumber != partNumber && info.ExpiresAt > DateTime.UtcNow)
                {
                    _urlQueue.Enqueue(info);
                    break;
                }
            }
            
            await Task.Delay(50, cancellationToken);
        }
        
        return null;
    }

    private async Task ReportProgressAsync(UploadJob job, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(_config.ProgressUpdateIntervalMs, cancellationToken);

            var completedParts = _manifest.GetCompletedPartCount(job.UploadId);
            var bytesNow = Interlocked.Read(ref _bytesTransferred);
            var elapsed = _speedTimer.Elapsed.TotalSeconds;
            
            // Calculate speed (bytes/sec)
            var speed = elapsed > 0 ? (long)(bytesNow / elapsed) : 0;
            
            // Calculate ETA
            var remaining = job.FileSize - bytesNow;
            var eta = speed > 0 ? (int)(remaining / speed) : 0;
            
            var progress = new ProgressMessage
            {
                UploadId = job.UploadId,
                Percent = job.FileSize > 0 ? (double)bytesNow / job.FileSize * 100 : 0,
                Speed = speed,
                Eta = eta,
                BytesTransferred = bytesNow,
                TotalBytes = job.FileSize,
                ActiveThreads = _config.OptimalThreadCount,
                CompletedParts = completedParts,
                TotalParts = job.TotalParts
            };

            OnProgress?.Invoke(progress);

            // Check if complete
            if (completedParts >= job.TotalParts)
            {
                break;
            }
        }
    }

    private long CalculateInitialBytesTransferred(string uploadId, int chunkSize)
    {
        var completedCount = _manifest.GetCompletedPartCount(uploadId);
        return (long)completedCount * chunkSize;
    }

    public void Pause()
    {
        _isPaused = true;
        _logger.LogInformation("Upload paused");
    }

    public void Resume()
    {
        _isPaused = false;
        _logger.LogInformation("Upload resumed");
    }

    public void Cancel()
    {
        _uploadCts?.Cancel();
        _logger.LogInformation("Upload cancelled");
    }

    public void Dispose()
    {
        _uploadCts?.Dispose();
        _pauseSemaphore.Dispose();
        _httpClient.Dispose();
    }

    // Helper classes for JSON deserialization
    private class PresignResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("urls")]
        public List<PresignUrlItem>? Urls { get; set; }
    }

    private class PresignUrlItem
    {
        [System.Text.Json.Serialization.JsonPropertyName("part_number")]
        public int PartNumber { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;
        
        [System.Text.Json.Serialization.JsonPropertyName("expires_at")]
        public string ExpiresAt { get; set; } = string.Empty;
    }
}
