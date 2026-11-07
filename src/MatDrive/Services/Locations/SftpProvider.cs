using System.Net.Sockets;
using System.Text;
using MatDrive.Data;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace MatDrive.Services.Locations;

/// <summary>
/// A folder on an SFTP server (SSH.NET), signed in with a password or a private key (the password is then the passphrase of the key). The connections come from a
/// pool: signing in costs a few hundred milliseconds. An empty folder of the location is the home folder of the account.
/// </summary>
public sealed class SftpProvider(LocationSettings settings, ConnectionPool<SftpClient> pool) : ILocationProvider
{
    public const int DefaultPort = 22;

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(60);

    private readonly string _base = PathGuard.RemoteBase(settings.RootPath);

    /// <summary>No folder given: the folder the account starts in (an explicit "/" is the top of the server).</summary>
    private readonly bool _useHome = string.IsNullOrWhiteSpace(settings.RootPath);

    public LocationKind Kind => LocationKind.Sftp;

    public bool HasPermissions => true;

    private string Key => ConnectionPool<SftpClient>.KeyOf("sftp", settings.Host, Port.ToString(System.Globalization.CultureInfo.InvariantCulture), settings.Username, settings.Password, settings.PrivateKey);

    private int Port => settings.Port is > 0 and < 65536 ? settings.Port.Value : DefaultPort;

    public Task CheckAsync(CancellationToken ct) => Run(async c =>
    {
        var top = Remote(c, string.Empty);
        if (!await c.ExistsAsync(top, ct))
        {
            throw new LocationException(LocationError.NotFound);
        }
    }, ct);

    public Task<IReadOnlyList<LocationEntry>> ListAsync(string path, CancellationToken ct) => Run<IReadOnlyList<LocationEntry>>(async c =>
    {
        var normalized = PathGuard.Check(path);
        var remote = Remote(c, normalized);
        var entries = new List<LocationEntry>();
        await foreach (var file in c.ListDirectoryAsync(remote, ct))
        {
            if (file.Name is "." or "..")
            {
                continue;
            }

            entries.Add(ToEntry(file, PathGuard.Combine(normalized, file.Name)));
        }

        return entries;
    }, ct);

    public Task<LocationEntry?> GetAsync(string path, CancellationToken ct) => Run(async c =>
    {
        var normalized = PathGuard.Check(path);
        try
        {
            var file = await c.GetAsync(Remote(c, normalized), ct);
            return (LocationEntry?)ToEntry(file, normalized);
        }
        catch (SftpPathNotFoundException)
        {
            return null;
        }
    }, ct);

    public async Task<Stream> OpenReadAsync(string path, long offset, CancellationToken ct)
    {
        var lease = await ConnectAsync(ct);
        try
        {
            var stream = await Map(() => lease.Client.OpenAsync(Remote(lease.Client, PathGuard.Check(path)), FileMode.Open, FileAccess.Read, ct), lease);
            if (offset > 0)
            {
                stream.Seek(offset, SeekOrigin.Begin);
            }

            return new HoldingStream(stream, lease);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public Task WriteAsync(string path, Stream content, long? length, bool overwrite, CancellationToken ct) => Run(async c =>
    {
        var normalized = PathGuard.Check(path);
        var remote = Remote(c, normalized);
        if (await c.ExistsAsync(remote, ct))
        {
            var existing = await c.GetAsync(remote, ct);
            if (existing.IsDirectory || !overwrite)
            {
                throw new LocationException(LocationError.Conflict);
            }
        }

        await EnsureParentAsync(c, normalized, ct);
        await c.UploadFileAsync(content, remote, true, null, ct);
    }, ct);

    public Task CreateFolderAsync(string path, CancellationToken ct) => Run(async c =>
    {
        var normalized = PathGuard.Check(path);
        var remote = Remote(c, normalized);
        if (await c.ExistsAsync(remote, ct))
        {
            throw new LocationException(LocationError.Conflict);
        }

        await EnsureParentAsync(c, normalized, ct);
        await c.CreateDirectoryAsync(remote, ct);
    }, ct);

    public Task MoveAsync(string from, string to, bool overwrite, CancellationToken ct) => Run(async c =>
    {
        var source = Remote(c, PathGuard.Check(from));
        var targetPath = PathGuard.Check(to);
        var target = Remote(c, targetPath);
        if (PathGuard.Normalize(from).Length == 0)
        {
            throw new LocationException(LocationError.InvalidPath);
        }

        if (!await c.ExistsAsync(source, ct))
        {
            throw new LocationException(LocationError.NotFound);
        }

        var caseOnly = string.Equals(source, target, StringComparison.OrdinalIgnoreCase) && source != target;
        if (!caseOnly && await c.ExistsAsync(target, ct))
        {
            var existing = await c.GetAsync(target, ct);
            if (!overwrite || existing.IsDirectory)
            {
                throw new LocationException(LocationError.Conflict);
            }

            await c.DeleteFileAsync(target, ct);
        }

        await EnsureParentAsync(c, targetPath, ct);
        await c.RenameFileAsync(source, target, ct);
    }, ct);

    public Task DeleteAsync(string path, CancellationToken ct) => Run(async c =>
    {
        var normalized = PathGuard.Check(path);
        if (normalized.Length == 0)
        {
            throw new LocationException(LocationError.InvalidPath);
        }

        var remote = Remote(c, normalized);
        var file = await c.GetAsync(remote, ct);
        await RemoveAsync(c, file, ct);
    }, ct);

    public Task<LocationSpace?> SpaceAsync(CancellationToken ct) => Run(async c =>
    {
        try
        {
            var info = await c.GetStatusAsync(Remote(c, string.Empty), ct);
            var block = (long)info.FileSystemBlockSize;
            return (LocationSpace?)new LocationSpace((long)info.TotalBlocks * block, (long)info.AvailableBlocks * block);
        }
        catch (Exception ex) when (ex is SshException or NotSupportedException)
        {
            // not every server answers statvfs@openssh.com
            return null;
        }
    }, ct);

    public Task<LocationPermissions?> PermissionsAsync(string path, CancellationToken ct) => Run(async c =>
    {
        var file = await c.GetAsync(Remote(c, PathGuard.Check(path)), ct);
        var mode = LocationPermissions.FromFlags(
            file.OwnerCanRead, file.OwnerCanWrite, file.OwnerCanExecute,
            file.GroupCanRead, file.GroupCanWrite, file.GroupCanExecute,
            file.OthersCanRead, file.OthersCanWrite, file.OthersCanExecute);

        // SFTP tells the numbers of owner and group, not their names
        return (LocationPermissions?)new LocationPermissions(mode, file.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture), file.GroupId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }, ct);

    public Task SetPermissionsAsync(string path, int mode, CancellationToken ct) => Run(async c =>
    {
        var remote = Remote(c, PathGuard.Check(path));
        if (!await c.ExistsAsync(remote, ct))
        {
            throw new LocationException(LocationError.NotFound);
        }

        // SSH.NET takes the octal digits as a number: 755, not 493
        c.ChangePermissions(remote, short.Parse(LocationPermissions.ToText(mode), System.Globalization.CultureInfo.InvariantCulture));
    }, ct);

    // ---- connection ---------------------------------------------------------------------------------------------------------------

    private Task<ConnectionPool<SftpClient>.Lease> ConnectAsync(CancellationToken ct) => pool.RentAsync(Key, () => CreateAsync(ct));

    private async Task<SftpClient> CreateAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.Host))
        {
            throw new LocationException(LocationError.Unreachable);
        }

        var user = settings.Username ?? string.Empty;
        var methods = new List<AuthenticationMethod>();
        if (!string.IsNullOrWhiteSpace(settings.PrivateKey))
        {
            try
            {
                using var keyStream = new MemoryStream(Encoding.UTF8.GetBytes(settings.PrivateKey.Trim() + "\n"));
                var key = string.IsNullOrEmpty(settings.Password) ? new PrivateKeyFile(keyStream) : new PrivateKeyFile(keyStream, settings.Password);
                methods.Add(new PrivateKeyAuthenticationMethod(user, key));
            }
            catch (Exception ex) when (ex is SshException or InvalidOperationException or ArgumentException or NotSupportedException or FormatException)
            {
                throw new LocationException(LocationError.AccessDenied, "The private key cannot be read: " + ex.Message, ex);
            }
        }
        else
        {
            methods.Add(new PasswordAuthenticationMethod(user, settings.Password ?? string.Empty));

            // servers that only ask "keyboard-interactive" (some NAS) get the password as the answer
            var interactive = new KeyboardInteractiveAuthenticationMethod(user);
            interactive.AuthenticationPrompt += (_, e) =>
            {
                foreach (var prompt in e.Prompts)
                {
                    prompt.Response = settings.Password ?? string.Empty;
                }
            };
            methods.Add(interactive);
        }

        var info = new Renci.SshNet.ConnectionInfo(settings.Host.Trim(), Port, user, [.. methods]) { Timeout = ConnectTimeout };
        var client = new SftpClient(info) { OperationTimeout = OperationTimeout, KeepAliveInterval = TimeSpan.FromSeconds(30) };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConnectTimeout + TimeSpan.FromSeconds(5));
        try
        {
            await client.ConnectAsync(timeout.Token);
            return client;
        }
        catch (Exception ex)
        {
            client.Dispose();
            throw ex switch
            {
                SshAuthenticationException => new LocationException(LocationError.AccessDenied, ex.Message, ex),
                OperationCanceledException when !ct.IsCancellationRequested => new LocationException(LocationError.Timeout, null, ex),
                SshOperationTimeoutException or TimeoutException => new LocationException(LocationError.Timeout, null, ex),
                SocketException or SshConnectionException or ProxyException => new LocationException(LocationError.Unreachable, ex.Message, ex),
                SshException => new LocationException(LocationError.Remote, ex.Message, ex),
                _ => ex,
            };
        }
    }

    /// <summary>An operation with a lent connection; the errors of SSH.NET become worded errors, a broken connection does not go back into the pool.</summary>
    private async Task<T> Run<T>(Func<SftpClient, Task<T>> action, CancellationToken ct)
    {
        using var lease = await ConnectAsync(ct);
        return await Map(() => action(lease.Client), lease);
    }

    private Task Run(Func<SftpClient, Task> action, CancellationToken ct) => Run<object?>(async c =>
    {
        await action(c);
        return null;
    }, ct);

    private static async Task<T> Map<T>(Func<Task<T>> action, ConnectionPool<SftpClient>.Lease lease)
    {
        try
        {
            return await action();
        }
        catch (LocationException)
        {
            throw;
        }
        catch (SftpPathNotFoundException ex)
        {
            throw new LocationException(LocationError.NotFound, null, ex);
        }
        catch (SftpPermissionDeniedException ex)
        {
            throw new LocationException(LocationError.AccessDenied, ex.Message, ex);
        }
        catch (SshOperationTimeoutException ex)
        {
            lease.Broken = true;
            throw new LocationException(LocationError.Timeout, null, ex);
        }
        catch (Exception ex) when (ex is SshConnectionException or SocketException or ObjectDisposedException or IOException)
        {
            lease.Broken = true;
            throw new LocationException(LocationError.Unreachable, ex.Message, ex);
        }
        catch (SshException ex)
        {
            throw new LocationException(LocationError.Remote, ex.Message, ex);
        }
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------------------

    /// <summary>The full path on the server; an empty folder of the location is the home folder of the account.</summary>
    private string Remote(SftpClient c, string relative)
    {
        var root = _useHome ? (c.WorkingDirectory ?? "/").TrimEnd('/') : _base;
        return PathGuard.Remote(root, relative);
    }

    private async Task EnsureParentAsync(SftpClient c, string normalized, CancellationToken ct)
    {
        var parent = PathGuard.Parent(normalized);
        if (string.IsNullOrEmpty(parent))
        {
            return;
        }

        var current = string.Empty;
        foreach (var segment in parent.Split('/'))
        {
            current = PathGuard.Combine(current, segment);
            var remote = Remote(c, current);
            if (!await c.ExistsAsync(remote, ct))
            {
                try
                {
                    await c.CreateDirectoryAsync(remote, ct);
                }
                catch (SshException)
                {
                    // made at the same moment by another upload into the same new folder: fine; anything else is the error
                    if (!await c.ExistsAsync(remote, ct))
                    {
                        throw;
                    }
                }
            }
        }
    }

    private static async Task RemoveAsync(SftpClient c, ISftpFile file, CancellationToken ct)
    {
        if (!file.IsDirectory || file.IsSymbolicLink)
        {
            await c.DeleteFileAsync(file.FullName, ct);
            return;
        }

        var children = new List<ISftpFile>();
        await foreach (var child in c.ListDirectoryAsync(file.FullName, ct))
        {
            if (child.Name is not ("." or ".."))
            {
                children.Add(child);
            }
        }

        foreach (var child in children)
        {
            await RemoveAsync(c, child, ct);
        }

        await c.DeleteDirectoryAsync(file.FullName, ct);
    }

    private static LocationEntry ToEntry(ISftpFile file, string path)
        => new(file.Name.Length > 0 ? file.Name : PathGuard.NameOf(path), path, file.IsDirectory, file.IsDirectory ? 0 : file.Length, file.LastWriteTimeUtc, file.Name.StartsWith('.'), file.IsSymbolicLink);
}
