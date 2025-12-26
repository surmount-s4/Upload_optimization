using System.Data.SQLite;
using UploadAgent.Models;

namespace UploadAgent.Services;

/// <summary>
/// SQLite-based state manifest for crash-safe resume capability.
/// Tracks upload jobs and individual chunk status with ETags.
/// </summary>
public class StateManifest : IDisposable
{
    private readonly string _connectionString;
    private readonly SQLiteConnection _connection;

    public StateManifest()
    {
        var dbPath = Path.Combine(AppContext.BaseDirectory, "upload_state.db");
        _connectionString = $"Data Source={dbPath};Version=3;";
        _connection = new SQLiteConnection(_connectionString);
        _connection.Open();
        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS uploads (
                upload_id TEXT PRIMARY KEY,
                file_path TEXT NOT NULL,
                file_name TEXT NOT NULL,
                file_size INTEGER NOT NULL,
                file_fingerprint TEXT NOT NULL,
                bucket TEXT NOT NULL,
                object_key TEXT NOT NULL,
                chunk_size_bytes INTEGER NOT NULL,
                total_parts INTEGER NOT NULL,
                status TEXT NOT NULL DEFAULT 'pending',
                created_at TEXT NOT NULL,
                completed_at TEXT
            );

            CREATE TABLE IF NOT EXISTS parts (
                upload_id TEXT NOT NULL,
                part_number INTEGER NOT NULL,
                byte_offset INTEGER NOT NULL,
                byte_length INTEGER NOT NULL,
                etag TEXT,
                status TEXT NOT NULL DEFAULT 'pending',
                retry_count INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (upload_id, part_number),
                FOREIGN KEY (upload_id) REFERENCES uploads(upload_id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_parts_status ON parts(upload_id, status);
        ";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Create a new upload job record.
    /// </summary>
    public void CreateUpload(UploadJob job)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO uploads (upload_id, file_path, file_name, file_size, file_fingerprint, 
                bucket, object_key, chunk_size_bytes, total_parts, status, created_at)
            VALUES (@uploadId, @filePath, @fileName, @fileSize, @fingerprint, 
                @bucket, @objectKey, @chunkSize, @totalParts, @status, @createdAt)
        ";
        cmd.Parameters.AddWithValue("@uploadId", job.UploadId);
        cmd.Parameters.AddWithValue("@filePath", job.FilePath);
        cmd.Parameters.AddWithValue("@fileName", job.FileName);
        cmd.Parameters.AddWithValue("@fileSize", job.FileSize);
        cmd.Parameters.AddWithValue("@fingerprint", job.FileFingerprint);
        cmd.Parameters.AddWithValue("@bucket", job.Bucket);
        cmd.Parameters.AddWithValue("@objectKey", job.ObjectKey);
        cmd.Parameters.AddWithValue("@chunkSize", job.ChunkSizeBytes);
        cmd.Parameters.AddWithValue("@totalParts", job.TotalParts);
        cmd.Parameters.AddWithValue("@status", job.Status.ToString().ToLower());
        cmd.Parameters.AddWithValue("@createdAt", job.CreatedAt.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Initialize all parts for an upload.
    /// </summary>
    public void InitializeParts(string uploadId, List<ChunkInfo> chunks)
    {
        using var transaction = _connection.BeginTransaction();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                INSERT INTO parts (upload_id, part_number, byte_offset, byte_length, status)
                VALUES (@uploadId, @partNumber, @byteOffset, @byteLength, 'pending')
            ";

            foreach (var chunk in chunks)
            {
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("@uploadId", uploadId);
                cmd.Parameters.AddWithValue("@partNumber", chunk.PartNumber);
                cmd.Parameters.AddWithValue("@byteOffset", chunk.ByteOffset);
                cmd.Parameters.AddWithValue("@byteLength", chunk.ByteLength);
                cmd.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <summary>
    /// Update upload job status.
    /// </summary>
    public void UpdateUploadStatus(string uploadId, UploadStatus status)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE uploads SET status = @status WHERE upload_id = @uploadId";
        cmd.Parameters.AddWithValue("@status", status.ToString().ToLower());
        cmd.Parameters.AddWithValue("@uploadId", uploadId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Mark a part as completed with its ETag.
    /// </summary>
    public void MarkPartCompleted(string uploadId, int partNumber, string etag)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            UPDATE parts SET status = 'completed', etag = @etag 
            WHERE upload_id = @uploadId AND part_number = @partNumber
        ";
        cmd.Parameters.AddWithValue("@etag", etag);
        cmd.Parameters.AddWithValue("@uploadId", uploadId);
        cmd.Parameters.AddWithValue("@partNumber", partNumber);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Mark a part as failed and increment retry count.
    /// </summary>
    public void MarkPartFailed(string uploadId, int partNumber)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            UPDATE parts SET status = 'failed', retry_count = retry_count + 1 
            WHERE upload_id = @uploadId AND part_number = @partNumber
        ";
        cmd.Parameters.AddWithValue("@uploadId", uploadId);
        cmd.Parameters.AddWithValue("@partNumber", partNumber);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Get all pending parts for an upload (for resume).
    /// </summary>
    public List<ChunkInfo> GetPendingParts(string uploadId, int maxRetries)
    {
        var parts = new List<ChunkInfo>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            SELECT part_number, byte_offset, byte_length, retry_count
            FROM parts
            WHERE upload_id = @uploadId 
              AND status IN ('pending', 'failed')
              AND retry_count < @maxRetries
            ORDER BY part_number
        ";
        cmd.Parameters.AddWithValue("@uploadId", uploadId);
        cmd.Parameters.AddWithValue("@maxRetries", maxRetries);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            parts.Add(new ChunkInfo
            {
                UploadId = uploadId,
                PartNumber = reader.GetInt32(0),
                ByteOffset = reader.GetInt64(1),
                ByteLength = reader.GetInt32(2),
                RetryCount = reader.GetInt32(3),
                Status = ChunkStatus.Pending
            });
        }
        return parts;
    }

    /// <summary>
    /// Get all completed parts with ETags (for finalization).
    /// </summary>
    public List<(int PartNumber, string ETag)> GetCompletedParts(string uploadId)
    {
        var parts = new List<(int, string)>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            SELECT part_number, etag FROM parts
            WHERE upload_id = @uploadId AND status = 'completed'
            ORDER BY part_number
        ";
        cmd.Parameters.AddWithValue("@uploadId", uploadId);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            parts.Add((reader.GetInt32(0), reader.GetString(1)));
        }
        return parts;
    }

    /// <summary>
    /// Get upload job by ID.
    /// </summary>
    public UploadJob? GetUpload(string uploadId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM uploads WHERE upload_id = @uploadId";
        cmd.Parameters.AddWithValue("@uploadId", uploadId);

        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            return new UploadJob
            {
                UploadId = reader["upload_id"].ToString()!,
                FilePath = reader["file_path"].ToString()!,
                FileName = reader["file_name"].ToString()!,
                FileSize = Convert.ToInt64(reader["file_size"]),
                FileFingerprint = reader["file_fingerprint"].ToString()!,
                Bucket = reader["bucket"].ToString()!,
                ObjectKey = reader["object_key"].ToString()!,
                ChunkSizeBytes = Convert.ToInt32(reader["chunk_size_bytes"]),
                TotalParts = Convert.ToInt32(reader["total_parts"]),
                Status = Enum.Parse<UploadStatus>(reader["status"].ToString()!, true),
                CreatedAt = DateTime.Parse(reader["created_at"].ToString()!)
            };
        }
        return null;
    }

    /// <summary>
    /// Get count of completed parts for progress tracking.
    /// </summary>
    public int GetCompletedPartCount(string uploadId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM parts WHERE upload_id = @uploadId AND status = 'completed'";
        cmd.Parameters.AddWithValue("@uploadId", uploadId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>
    /// Delete upload and all associated parts.
    /// </summary>
    public void DeleteUpload(string uploadId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM uploads WHERE upload_id = @uploadId";
        cmd.Parameters.AddWithValue("@uploadId", uploadId);
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _connection?.Dispose();
    }
}
