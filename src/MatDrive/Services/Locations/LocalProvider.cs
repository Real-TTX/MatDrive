using MatDrive.Data;

namespace MatDrive.Services.Locations;

/// <summary>
/// A folder of the server: in the container a bind mount of the NAS below <c>/storage</c>. Every path goes through <see cref="PathGuard.ResolveLocal"/> (no <c>..</c>,
/// no link out of the folder, also not on the way); the data folder of MatDrive is invisible even when a location lies above it. A write on a volume that is
/// mounted read-only (<c>:ro</c>) or without the right becomes a worded error, not a 500.
/// </summary>
public sealed class LocalProvider : ILocationProvider
{
    private readonly string _root;
    private readonly string? _hidden;

    /// <param name="root">The folder of the location (a full path).</param>
    /// <param name="hidden">A folder that is never shown or touched (the data folder of MatDrive).</param>
    public LocalProvider(string root, string? hidden)
    {
        _root = PathGuard.RealPath(root) ?? Path.GetFullPath(root);
        _hidden = hidden is null ? null : PathGuard.RealPath(hidden) ?? Path.GetFullPath(hidden);
    }

    public LocationKind Kind => LocationKind.Local;

    public bool HasPermissions => !OperatingSystem.IsWindows();

    public Task CheckAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_root))
        {
            throw new LocationException(LocationError.Unreachable);
        }

        // reading the folder proves the right to it (a mount without access answers here)
        Guard(() => Directory.EnumerateFileSystemEntries(_root).Take(1).ToList());
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<LocationEntry>> ListAsync(string path, CancellationToken ct)
    {
        RootMustExist();
        var full = Resolve(path);
        if (!Directory.Exists(full))
        {
            throw new LocationException(File.Exists(full) ? LocationError.InvalidPath : LocationError.NotFound);
        }

        var normalized = PathGuard.Normalize(path);
        var entries = new List<LocationEntry>();
        var infos = Guard(() => new DirectoryInfo(full).EnumerateFileSystemInfos().ToList());
        foreach (var info in infos)
        {
            ct.ThrowIfCancellationRequested();
            var child = PathGuard.Combine(normalized, info.Name);

            // a link that leads out of the location, and the data folder of MatDrive, are not there
            if (PathGuard.ResolveLocal(_root, child, _hidden) is null)
            {
                continue;
            }

            entries.Add(ToEntry(info, child));
        }

        return Task.FromResult<IReadOnlyList<LocationEntry>>(entries);
    }

    public Task<LocationEntry?> GetAsync(string path, CancellationToken ct)
    {
        RootMustExist();
        var normalized = PathGuard.Normalize(path);
        var full = Resolve(normalized);
        if (Directory.Exists(full))
        {
            return Task.FromResult<LocationEntry?>(ToEntry(new DirectoryInfo(full), normalized));
        }

        return Task.FromResult<LocationEntry?>(File.Exists(full) ? ToEntry(new FileInfo(full), normalized) : null);
    }

    public Task<Stream> OpenReadAsync(string path, long offset, CancellationToken ct)
    {
        var full = Resolve(path);
        if (!File.Exists(full))
        {
            throw new LocationException(LocationError.NotFound);
        }

        var stream = Guard(() => new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, useAsync: true));
        if (offset > 0)
        {
            stream.Seek(offset, SeekOrigin.Begin);
        }

        return Task.FromResult<Stream>(stream);
    }

    public async Task WriteAsync(string path, Stream content, long? length, bool overwrite, CancellationToken ct)
    {
        var full = Resolve(path);
        if (Directory.Exists(full) || (!overwrite && File.Exists(full)))
        {
            throw new LocationException(LocationError.Conflict);
        }

        Guard(() => Directory.CreateDirectory(Path.GetDirectoryName(full)!));
        var file = Guard(() => new FileStream(full, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true));
        await using (file)
        {
            try
            {
                await content.CopyToAsync(file, 81920, ct);
            }
            catch (IOException ex) when (IsReadOnly(ex))
            {
                throw new LocationException(LocationError.ReadOnly);
            }
        }
    }

    public Task CreateFolderAsync(string path, CancellationToken ct)
    {
        var full = Resolve(path);
        if (Directory.Exists(full) || File.Exists(full))
        {
            throw new LocationException(LocationError.Conflict);
        }

        Guard(() => Directory.CreateDirectory(full));
        return Task.CompletedTask;
    }

    public Task MoveAsync(string from, string to, bool overwrite, CancellationToken ct)
    {
        var source = Resolve(from);
        var target = Resolve(to);
        if (PathGuard.Normalize(from).Length == 0)
        {
            throw new LocationException(LocationError.InvalidPath);
        }

        Guard(() => Directory.CreateDirectory(Path.GetDirectoryName(target)!));
        if (Directory.Exists(source))
        {
            // only a change of case is allowed onto itself (Windows, macOS volumes)
            var sameEntry = string.Equals(source, target, StringComparison.OrdinalIgnoreCase);
            if (!sameEntry && (Directory.Exists(target) || File.Exists(target)))
            {
                throw new LocationException(LocationError.Conflict);
            }

            Guard(() => Directory.Move(source, target));
            return Task.CompletedTask;
        }

        if (!File.Exists(source))
        {
            throw new LocationException(LocationError.NotFound);
        }

        if (Directory.Exists(target) || (!overwrite && File.Exists(target) && !string.Equals(source, target, StringComparison.OrdinalIgnoreCase)))
        {
            throw new LocationException(LocationError.Conflict);
        }

        Guard(() => File.Move(source, target, overwrite));
        return Task.CompletedTask;
    }

    public async Task<bool> TryCopyAsync(string from, string to, CancellationToken ct)
    {
        var source = Resolve(from);
        var target = Resolve(to);
        if (Directory.Exists(target) || File.Exists(target))
        {
            throw new LocationException(LocationError.Conflict);
        }

        if (File.Exists(source))
        {
            Guard(() => Directory.CreateDirectory(Path.GetDirectoryName(target)!));
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            await WriteAsync(to, input, input.Length, overwrite: false, ct);
            File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(source));
            return true;
        }

        if (!Directory.Exists(source))
        {
            throw new LocationException(LocationError.NotFound);
        }

        await CreateFolderAsync(to, ct);
        foreach (var child in await ListAsync(from, ct))
        {
            ct.ThrowIfCancellationRequested();
            if (child.IsLink)
            {
                continue; // links are not followed into a copy
            }

            await TryCopyAsync(child.Path, PathGuard.Combine(to, child.Name), ct);
        }

        return true;
    }

    public Task DeleteAsync(string path, CancellationToken ct)
    {
        if (PathGuard.Normalize(path).Length == 0)
        {
            throw new LocationException(LocationError.InvalidPath);
        }

        var full = Resolve(path);
        var info = new FileInfo(full);
        if (info.LinkTarget is not null)
        {
            // a link goes, never what it points to (Windows removes a link to a folder like a folder; Linux unlinks it)
            Guard(() =>
            {
                if (OperatingSystem.IsWindows() && Directory.Exists(full))
                {
                    Directory.Delete(full);
                }
                else
                {
                    File.Delete(full);
                }
            });
            return Task.CompletedTask;
        }

        if (Directory.Exists(full))
        {
            Guard(() => Directory.Delete(full, recursive: true));
            return Task.CompletedTask;
        }

        if (!File.Exists(full))
        {
            throw new LocationException(LocationError.NotFound);
        }

        Guard(() => File.Delete(full));
        return Task.CompletedTask;
    }

    public Task<LocationSpace?> SpaceAsync(CancellationToken ct)
    {
        try
        {
            // on Linux any path works (statvfs of the mount of the location, a bind mount has its own); Windows wants the drive
            var drive = new DriveInfo(OperatingSystem.IsWindows() ? Path.GetPathRoot(_root)! : _root);
            return Task.FromResult<LocationSpace?>(drive.IsReady ? new LocationSpace(drive.TotalSize, drive.AvailableFreeSpace) : null);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return Task.FromResult<LocationSpace?>(null);
        }
    }

    public Task<LocationPermissions?> PermissionsAsync(string path, CancellationToken ct)
    {
        var full = Resolve(path);
        if (!Directory.Exists(full) && !File.Exists(full))
        {
            throw new LocationException(LocationError.NotFound);
        }

        if (OperatingSystem.IsWindows())
        {
            return Task.FromResult<LocationPermissions?>(null);
        }

        // .NET does not tell the owner of a file; the bits are what can be changed here (like MatFile)
        var mode = (int)File.GetUnixFileMode(full) & LocationPermissions.Mask;
        return Task.FromResult<LocationPermissions?>(new LocationPermissions(mode));
    }

    public Task SetPermissionsAsync(string path, int mode, CancellationToken ct)
    {
        var full = Resolve(path);
        if (!Directory.Exists(full) && !File.Exists(full))
        {
            throw new LocationException(LocationError.NotFound);
        }

        if (OperatingSystem.IsWindows())
        {
            throw new LocationException(LocationError.NotSupported);
        }

        // the special bits (setuid, setgid, sticky) stay as they are
        var current = (int)File.GetUnixFileMode(full);
        try
        {
            File.SetUnixFileMode(full, (UnixFileMode)((current & ~LocationPermissions.Mask) | (mode & LocationPermissions.Mask)));
        }
        catch (UnauthorizedAccessException)
        {
            throw new LocationException(LocationError.AccessDenied);
        }
        catch (IOException ex) when (IsReadOnly(ex))
        {
            throw new LocationException(LocationError.ReadOnly);
        }

        return Task.CompletedTask;
    }

    private void RootMustExist()
    {
        if (!Directory.Exists(_root))
        {
            throw new LocationException(LocationError.Unreachable);
        }
    }

    private string Resolve(string? path)
        => PathGuard.ResolveLocal(_root, path, _hidden) ?? throw new LocationException(LocationError.InvalidPath);

    private static LocationEntry ToEntry(FileSystemInfo info, string path)
        => new(info.Name, path, info is DirectoryInfo, info is FileInfo f ? f.Length : 0, info.LastWriteTimeUtc, info.Name.StartsWith('.'), info.LinkTarget is not null);

    /// <summary>The errors of the file system as worded errors: no right, a read-only volume, a full disk.</summary>
    private static T Guard<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (UnauthorizedAccessException)
        {
            throw new LocationException(LocationError.AccessDenied);
        }
        catch (DirectoryNotFoundException)
        {
            throw new LocationException(LocationError.NotFound);
        }
        catch (FileNotFoundException)
        {
            throw new LocationException(LocationError.NotFound);
        }
        catch (IOException ex) when (IsReadOnly(ex))
        {
            throw new LocationException(LocationError.ReadOnly);
        }
        catch (IOException ex) when (IsExisting(ex))
        {
            throw new LocationException(LocationError.Conflict);
        }
    }

    private static void Guard(Action action) => Guard<object?>(() =>
    {
        action();
        return null;
    });

    /// <summary>EROFS is 30; the message says it too.</summary>
    private static bool IsReadOnly(IOException ex)
        => (ex.HResult & 0xFFFF) == 30 || ex.Message.Contains("Read-only file system", StringComparison.OrdinalIgnoreCase);

    /// <summary>EEXIST (17) / ERROR_FILE_EXISTS (80) / ERROR_ALREADY_EXISTS (183).</summary>
    private static bool IsExisting(IOException ex) => (ex.HResult & 0xFFFF) is 17 or 80 or 183;
}
