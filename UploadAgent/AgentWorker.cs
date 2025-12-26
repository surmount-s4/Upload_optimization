using Microsoft.Extensions.Hosting;
using UploadAgent.Models;

namespace UploadAgent.Services;

/// <summary>
/// Main background worker that orchestrates the upload agent.
/// Coordinates between WebSocket server, file processor, and upload workers.
/// </summary>
public class AgentWorker : BackgroundService
{
    private readonly ILogger<AgentWorker> _logger;
    private readonly AppConfig _config;
    private readonly WebSocketServer _wsServer;
    private readonly FileProcessor _fileProcessor;
    private readonly StateManifest _manifest;
    private readonly UploadWorkerPool _workerPool;
    private readonly BackendClient _backendClient;

    private UploadJob? _currentJob;
    private string _backendUrl = string.Empty;

    public AgentWorker(
        ILogger<AgentWorker> logger,
        AppConfig config,
        WebSocketServer wsServer,
        FileProcessor fileProcessor,
        StateManifest manifest,
        UploadWorkerPool workerPool,
        ILoggerFactory loggerFactory)
    {
        _logger = logger;
        _config = config;
        _wsServer = wsServer;
        _fileProcessor = fileProcessor;
        _manifest = manifest;
        _workerPool = workerPool;
        _backendClient = new BackendClient(config, loggerFactory.CreateLogger<BackendClient>());

        // Wire up events
        _wsServer.OnStartCommand += HandleStartAsync;
        _wsServer.OnPauseCommand += HandlePauseAsync;
        _wsServer.OnResumeCommand += HandleResumeAsync;
        _wsServer.OnCancelCommand += HandleCancelAsync;

        _workerPool.OnProgress += async (msg) => await _wsServer.BroadcastProgressAsync(msg);
        _workerPool.OnChunkUpdate += async (msg) => await _wsServer.BroadcastChunkUpdateAsync(msg);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Upload Agent starting...");
        _logger.LogInformation("Configuration: ChunkSize={ChunkMB}MB, Threads={Threads}, WsPort={Port}",
            _config.ChunkSizeMB, _config.OptimalThreadCount, _config.WsPort);

        // Start WebSocket server
        await _wsServer.StartAsync(stoppingToken);
    }

    private async Task HandleStartAsync(string filePath, string? backendUrl)
    {
        try
        {
            if (_currentJob != null && _currentJob.Status == UploadStatus.InProgress)
            {
                await _wsServer.BroadcastErrorAsync(new ErrorMessage
                {
                    Error = "An upload is already in progress. Cancel it first.",
                    Code = "UPLOAD_IN_PROGRESS"
                });
                return;
            }

            _backendUrl = backendUrl ?? _config.BackendUrl;
            _backendClient.SetBaseUrl(_backendUrl);

            _logger.LogInformation("Starting upload for: {FilePath}", filePath);

            // Notify frontend
            await _wsServer.BroadcastStatusAsync(new StatusMessage
            {
                Status = "preparing",
                Message = "Locking file and gathering information..."
            });

            // Lock the file
            if (!_fileProcessor.LockFile(filePath))
            {
                await _wsServer.BroadcastErrorAsync(new ErrorMessage
                {
                    Error = "Failed to lock file. It may be in use by another application.",
                    Code = "FILE_LOCK_FAILED"
                });
                return;
            }

            // Get file info
            var (fileName, fileSize, fingerprint) = _fileProcessor.GetFileInfo(filePath);
            
            _logger.LogInformation("File: {Name}, Size: {Size} bytes", fileName, fileSize);

            // Call backend to initiate upload
            await _wsServer.BroadcastStatusAsync(new StatusMessage
            {
                Status = "preparing",
                Message = "Initiating upload with server..."
            });

            var initResult = await _backendClient.InitiateUploadAsync(
                fileName, fileSize, fingerprint);

            if (initResult == null)
            {
                _fileProcessor.ReleaseFile();
                await _wsServer.BroadcastErrorAsync(new ErrorMessage
                {
                    Error = "Failed to initiate upload with server.",
                    Code = "INITIATE_FAILED"
                });
                return;
            }

            // Create job
            _currentJob = new UploadJob
            {
                UploadId = initResult.UploadId,
                FilePath = filePath,
                FileName = fileName,
                FileSize = fileSize,
                FileFingerprint = fingerprint,
                Bucket = initResult.Bucket,
                ObjectKey = initResult.ObjectKey,
                ChunkSizeBytes = initResult.ChunkSize > 0 ? initResult.ChunkSize : _config.ChunkSizeBytes,
                TotalParts = initResult.TotalParts,
                Status = UploadStatus.InProgress
            };

            // Save to manifest
            _manifest.CreateUpload(_currentJob);

            // Generate and save chunks
            var chunks = _fileProcessor.GenerateChunks(_currentJob.UploadId, fileSize);
            _manifest.InitializeParts(_currentJob.UploadId, chunks);

            _logger.LogInformation("Upload initiated: {UploadId}, {Parts} parts", 
                _currentJob.UploadId, _currentJob.TotalParts);

            // Notify frontend
            await _wsServer.BroadcastStatusAsync(new StatusMessage
            {
                UploadId = _currentJob.UploadId,
                Status = "uploading",
                Message = $"Uploading {_currentJob.TotalParts} chunks..."
            });

            // Start upload
            try
            {
                await _workerPool.ExecuteUploadAsync(_currentJob, _backendUrl, CancellationToken.None);

                // Check if all parts completed
                var completedParts = _manifest.GetCompletedParts(_currentJob.UploadId);
                
                if (completedParts.Count == _currentJob.TotalParts)
                {
                    await _wsServer.BroadcastStatusAsync(new StatusMessage
                    {
                        UploadId = _currentJob.UploadId,
                        Status = "verifying",
                        Message = "Finalizing upload..."
                    });

                    // Complete the upload
                    var completeResult = await _backendClient.CompleteUploadAsync(
                        _currentJob.UploadId,
                        _currentJob.Bucket,
                        _currentJob.ObjectKey,
                        completedParts
                    );

                    if (completeResult?.Status == "completed")
                    {
                        _manifest.UpdateUploadStatus(_currentJob.UploadId, UploadStatus.Completed);
                        
                        await _wsServer.BroadcastStatusAsync(new StatusMessage
                        {
                            UploadId = _currentJob.UploadId,
                            Status = "completed",
                            Message = "Upload completed successfully!"
                        });

                        _logger.LogInformation("Upload completed: {UploadId}", _currentJob.UploadId);
                    }
                    else
                    {
                        throw new Exception("Failed to complete multipart upload");
                    }
                }
                else
                {
                    _manifest.UpdateUploadStatus(_currentJob.UploadId, UploadStatus.Failed);
                    
                    await _wsServer.BroadcastErrorAsync(new ErrorMessage
                    {
                        UploadId = _currentJob.UploadId,
                        Error = $"Upload incomplete: {completedParts.Count}/{_currentJob.TotalParts} parts",
                        Code = "INCOMPLETE"
                    });
                }
            }
            catch (OperationCanceledException)
            {
                _manifest.UpdateUploadStatus(_currentJob.UploadId, UploadStatus.Cancelled);
                _logger.LogInformation("Upload cancelled: {UploadId}", _currentJob.UploadId);
            }
            finally
            {
                _fileProcessor.ReleaseFile();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during upload");
            _fileProcessor.ReleaseFile();
            
            await _wsServer.BroadcastErrorAsync(new ErrorMessage
            {
                UploadId = _currentJob?.UploadId,
                Error = ex.Message,
                Code = "UPLOAD_ERROR"
            });
        }
    }

    private Task HandlePauseAsync(string uploadId)
    {
        if (_currentJob?.UploadId == uploadId)
        {
            _workerPool.Pause();
            _manifest.UpdateUploadStatus(uploadId, UploadStatus.Paused);
            
            return _wsServer.BroadcastStatusAsync(new StatusMessage
            {
                UploadId = uploadId,
                Status = "paused",
                Message = "Upload paused. In-flight chunks will complete."
            });
        }
        return Task.CompletedTask;
    }

    private Task HandleResumeAsync(string uploadId)
    {
        if (_currentJob?.UploadId == uploadId)
        {
            _workerPool.Resume();
            _manifest.UpdateUploadStatus(uploadId, UploadStatus.InProgress);
            
            return _wsServer.BroadcastStatusAsync(new StatusMessage
            {
                UploadId = uploadId,
                Status = "uploading",
                Message = "Upload resumed."
            });
        }
        return Task.CompletedTask;
    }

    private async Task HandleCancelAsync(string uploadId)
    {
        if (_currentJob?.UploadId == uploadId)
        {
            _workerPool.Cancel();
            _manifest.UpdateUploadStatus(uploadId, UploadStatus.Cancelled);
            _fileProcessor.ReleaseFile();
            
            // Abort on backend
            await _backendClient.AbortUploadAsync(
                _currentJob.Bucket,
                _currentJob.ObjectKey,
                uploadId
            );
            
            await _wsServer.BroadcastStatusAsync(new StatusMessage
            {
                UploadId = uploadId,
                Status = "cancelled",
                Message = "Upload cancelled."
            });
            
            _currentJob = null;
        }
    }

    public override void Dispose()
    {
        _wsServer.Dispose();
        _fileProcessor.Dispose();
        _manifest.Dispose();
        _workerPool.Dispose();
        _backendClient.Dispose();
        base.Dispose();
    }
}
