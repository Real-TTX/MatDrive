using System.Security.Cryptography;

namespace MatDrive.Services.Files;

/// <summary>What was written: the name of the bytes in the store, how many, and their SHA-256.</summary>
public sealed record StoredBlob(Guid BlobId, long Size, string Sha256);

/// <summary>Thrown when an upload is larger than allowed (the limit of the installation or the free space).</summary>
public sealed class TooLargeException(string message) : Exception(message);

/// <summary>
/// The bytes of the files: one file per version in the data volume, <c>{data}/files/ab/cd/&lt;blob id&gt;</c>. The database only knows the id. A blob is written to a
/// temporary file first and moved into place when it is complete, so a broken upload never leaves half a file behind.
/// </summary>
public sealed class BlobStore
{
    private readonly string _root;
    private readonly string _temp;

    public BlobStore(IConfiguration configuration)
    {
        var data = configuration["DataDirectory"] ?? "/data";
        _root = Path.Combine(data, "files");
        _temp = Path.Combine(data, "temp");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_temp);
    }

    /// <summary>The folder of the temporary files (uploads in pieces, zip files being made).</summary>
    public string TempFolder => _temp;

    public string PathOf(Guid blob)
    {
        var name = blob.ToString("N");
        return Path.Combine(_root, name[..2], name[2..4], name);
    }

    public bool Exists(Guid blob) => File.Exists(PathOf(blob));

    /// <summary>Writes a stream into a new blob; at most <paramref name="maxBytes"/> (0 = no limit).</summary>
    public async Task<StoredBlob> WriteAsync(Stream input, long maxBytes = 0, CancellationToken ct = default)
    {
        var blob = Guid.NewGuid();
        var temp = Path.Combine(_temp, blob.ToString("N") + ".part");
        long size = 0;
        string hash;
        try
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    size += read;
                    if (maxBytes > 0 && size > maxBytes)
                    {
                        throw new TooLargeException(Localization.Text.T("Files.TooLarge"));
                    }

                    sha.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }

            hash = Convert.ToHexStringLower(sha.GetHashAndReset());
            var target = PathOf(blob);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(temp, target);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }

        return new StoredBlob(blob, size, hash);
    }

    /// <summary>Takes a finished temporary file (an upload in pieces) into the store.</summary>
    public async Task<StoredBlob> AdoptAsync(string tempFile, CancellationToken ct = default)
    {
        string hash;
        await using (var file = File.OpenRead(tempFile))
        {
            hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));
        }

        var blob = Guid.NewGuid();
        var size = new FileInfo(tempFile).Length;
        var target = PathOf(blob);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Move(tempFile, target);
        return new StoredBlob(blob, size, hash);
    }

    /// <summary>A copy of a blob under a new id (copying a file copies its bytes, so deleting one never touches the other).</summary>
    public async Task<StoredBlob> CopyAsync(Guid blob, CancellationToken ct = default)
    {
        await using var source = OpenRead(blob);
        return await WriteAsync(source, 0, ct);
    }

    public Stream OpenRead(Guid blob) => new FileStream(PathOf(blob), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);

    public void Delete(Guid blob) => TryDelete(PathOf(blob));

    /// <summary>The free space of the volume of the store (bytes).</summary>
    public long FreeSpace()
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_root))!).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return -1;
        }
    }

    /// <summary>Can the store be written (the health check)?</summary>
    public bool CanWrite()
    {
        try
        {
            var probe = Path.Combine(_temp, ".probe-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Temporary files older than a day go (broken uploads, zip files that were downloaded).</summary>
    public int CleanTemp()
    {
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(_temp))
        {
            if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-1))
            {
                TryDelete(file);
                count++;
            }
        }

        return count;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the housekeeping tries again
        }
    }
}
