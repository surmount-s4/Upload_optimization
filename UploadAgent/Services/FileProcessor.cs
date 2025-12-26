using System.Security.Cryptography;
using UploadAgent.Models;

namespace UploadAgent.Services;

/// <summary>
/// Handles file operations: locking, chunking, and fingerprinting.
/// Opens files with FileShare.Read to prevent modifications during transfer.
/// </summary>
public class FileProcessor : IDisposable
{
    private readonly AppConfig _config;
    private FileStream? _fileStream;
    private string? _currentFilePath;

    public FileProcessor(AppConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// Lock a file for reading (prevents deletion/modification).
    /// </summary>
    public bool LockFile(string filePath)
    {
        try
        {
            if (_fileStream != null)
            {
                throw new InvalidOperationException("Another file is already locked. Release it first.");
            }

            // FileShare.Read allows others to read but not write/delete
            _fileStream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.SequentialScan
            );
            _currentFilePath = filePath;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Release the file lock.
    /// </summary>
    public void ReleaseFile()
    {
        _fileStream?.Dispose();
        _fileStream = null;
        _currentFilePath = null;
    }

    /// <summary>
    /// Get file information and generate fingerprint.
    /// </summary>
    public (string FileName, long FileSize, string Fingerprint) GetFileInfo(string filePath)
    {
        var fileInfo = new FileInfo(filePath);
        
        // Fingerprint = combination of size and last modified time
        // Fast to compute, good enough for detecting changes
        var fingerprint = $"{fileInfo.Length}:{fileInfo.LastWriteTimeUtc.Ticks}";
        
        return (fileInfo.Name, fileInfo.Length, fingerprint);
    }

    /// <summary>
    /// Calculate full SHA256 hash of file (optional, slow for large files).
    /// </summary>
    public async Task<string> CalculateFileHashAsync(string filePath, CancellationToken cancellationToken)
    {
        using var sha256 = SHA256.Create();
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        
        var hash = await sha256.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Generate chunk metadata for all parts.
    /// </summary>
    public List<ChunkInfo> GenerateChunks(string uploadId, long fileSize)
    {
        var chunks = new List<ChunkInfo>();
        var chunkSize = _config.ChunkSizeBytes;
        var totalParts = (int)Math.Ceiling((double)fileSize / chunkSize);

        long offset = 0;
        for (int part = 1; part <= totalParts; part++)
        {
            var length = (int)Math.Min(chunkSize, fileSize - offset);
            chunks.Add(new ChunkInfo
            {
                UploadId = uploadId,
                PartNumber = part,
                ByteOffset = offset,
                ByteLength = length,
                Status = ChunkStatus.Pending
            });
            offset += length;
        }

        return chunks;
    }

    /// <summary>
    /// Read a specific chunk from the locked file.
    /// </summary>
    public async Task<byte[]> ReadChunkAsync(long offset, int length, CancellationToken cancellationToken)
    {
        if (_fileStream == null)
        {
            throw new InvalidOperationException("No file is currently locked.");
        }

        var buffer = new byte[length];
        _fileStream.Seek(offset, SeekOrigin.Begin);
        
        int totalRead = 0;
        while (totalRead < length)
        {
            var bytesRead = await _fileStream.ReadAsync(
                buffer.AsMemory(totalRead, length - totalRead),
                cancellationToken
            );
            
            if (bytesRead == 0) break; // EOF
            totalRead += bytesRead;
        }

        if (totalRead < length)
        {
            // Return only what we read
            return buffer[..totalRead];
        }

        return buffer;
    }

    /// <summary>
    /// Calculate optimal chunk size based on file size.
    /// </summary>
    public int CalculateOptimalChunkSize(long fileSize)
    {
        var maxParts = _config.MaxParts;
        var preferredSize = _config.ChunkSizeBytes;
        var minSize = _config.MinChunkSizeMB * 1024 * 1024;
        var maxSize = _config.MaxChunkSizeMB * 1024 * 1024;

        // Check if preferred size works
        var partsNeeded = (int)Math.Ceiling((double)fileSize / preferredSize);
        if (partsNeeded <= maxParts)
        {
            return preferredSize;
        }

        // Need larger chunks
        var minRequired = (int)Math.Ceiling((double)fileSize / maxParts);
        
        // Round up to nearest 16 MB for alignment
        var aligned = ((minRequired / (16 * 1024 * 1024)) + 1) * (16 * 1024 * 1024);
        
        return Math.Min(aligned, maxSize);
    }

    public void Dispose()
    {
        ReleaseFile();
    }
}
