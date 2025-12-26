namespace UploadAgent.Services;

/// <summary>
/// Centralized configuration loaded from .env file.
/// All tunable thresholds in one place.
/// </summary>
public class AppConfig
{
    // Chunk Configuration
    public int ChunkSizeMB { get; }
    public int ChunkSizeBytes => ChunkSizeMB * 1024 * 1024;
    public int MinChunkSizeMB { get; }
    public int MaxChunkSizeMB { get; }
    public int MaxParts { get; }

    // Thread Pool
    public int UploadThreadsMin { get; }
    public int UploadThreadsMax { get; }
    public bool UploadThreadsAuto { get; }
    public int OptimalThreadCount { get; }

    // Presigned URL Batching
    public int PresignBatchSize { get; }
    public int PresignLookahead { get; }
    public int PresignExpiryHours { get; }

    // Retry & Safety
    public int RetryMaxAttempts { get; }
    public int RetryBaseDelayMs { get; }
    public int RetryMaxDelayMs { get; }

    // Network
    public int HttpTimeoutSeconds { get; }
    public int SpeedSampleWindowSeconds { get; }

    // WebSocket
    public int WsPort { get; }
    public int ProgressUpdateIntervalMs { get; }

    // Backend
    public string BackendUrl { get; }

    public AppConfig()
    {
        // Chunk Configuration
        ChunkSizeMB = GetEnvInt("CHUNK_SIZE_MB", 128);
        MinChunkSizeMB = GetEnvInt("MIN_CHUNK_SIZE_MB", 5);
        MaxChunkSizeMB = GetEnvInt("MAX_CHUNK_SIZE_MB", 512);
        MaxParts = GetEnvInt("MAX_PARTS", 10000);

        // Thread Pool
        UploadThreadsMin = GetEnvInt("UPLOAD_THREADS_MIN", 2);
        UploadThreadsMax = GetEnvInt("UPLOAD_THREADS_MAX", 8);
        UploadThreadsAuto = GetEnvBool("UPLOAD_THREADS_AUTO", true);
        OptimalThreadCount = CalculateOptimalThreads();

        // Presigned URL Batching
        PresignBatchSize = GetEnvInt("PRESIGN_BATCH_SIZE", 20);
        PresignLookahead = GetEnvInt("PRESIGN_LOOKAHEAD", 50);
        PresignExpiryHours = GetEnvInt("PRESIGN_EXPIRY_HOURS", 1);

        // Retry & Safety
        RetryMaxAttempts = GetEnvInt("RETRY_MAX_ATTEMPTS", 3);
        RetryBaseDelayMs = GetEnvInt("RETRY_BASE_DELAY_MS", 1000);
        RetryMaxDelayMs = GetEnvInt("RETRY_MAX_DELAY_MS", 30000);

        // Network
        HttpTimeoutSeconds = GetEnvInt("HTTP_TIMEOUT_SECONDS", 300);
        SpeedSampleWindowSeconds = GetEnvInt("SPEED_SAMPLE_WINDOW_SECONDS", 5);

        // WebSocket
        WsPort = GetEnvInt("WS_PORT", 8765);
        ProgressUpdateIntervalMs = GetEnvInt("PROGRESS_UPDATE_INTERVAL_MS", 500);

        // Backend
        BackendUrl = Environment.GetEnvironmentVariable("BACKEND_URL") ?? "http://localhost:8000";
    }

    private int CalculateOptimalThreads()
    {
        if (!UploadThreadsAuto)
            return UploadThreadsMax;

        // Get CPU cores and calculate 75% utilization
        int cpuCores = Environment.ProcessorCount;
        int calculated = (int)(cpuCores * 0.75);

        // Check available memory (need ChunkSizeMB * threads)
        // For now, use a simple heuristic - in production, check actual RAM
        long availableMemoryMB = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024);
        int maxByMemory = (int)(availableMemoryMB / ChunkSizeMB / 2); // Use max 50% of available

        int optimal = Math.Min(calculated, maxByMemory);
        return Math.Clamp(optimal, UploadThreadsMin, UploadThreadsMax);
    }

    private static int GetEnvInt(string key, int defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(key);
        return int.TryParse(value, out int result) ? result : defaultValue;
    }

    private static bool GetEnvBool(string key, bool defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(key);
        return bool.TryParse(value, out bool result) ? result : defaultValue;
    }
}
