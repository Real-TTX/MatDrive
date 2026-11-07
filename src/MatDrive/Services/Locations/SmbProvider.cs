using System.Net;
using System.Net.Sockets;
using MatDrive.Data;
using SMBLibrary;
using SMBLibrary.Client;
using FileAttributes = SMBLibrary.FileAttributes;

namespace MatDrive.Services.Locations;

/// <summary>A signed-in SMB connection with one share attached (pooled per server, account and share).</summary>
public sealed class SmbSession : IDisposable
{
    public required PortClient Client { get; init; }

    public ISMBFileStore? Store { get; init; }

    public bool IsAlive => Client.IsConnected;

    public void Dispose()
    {
        try
        {
            Store?.Disconnect();
            Client.Logoff();
        }
        catch (Exception)
        {
            // the connection is gone anyway
        }
        finally
        {
            Client.Disconnect();
        }
    }
}

/// <summary>The SMB2 client of SMBLibrary with a port of choice (the library has the call, but only for its own kind).</summary>
public sealed class PortClient(int responseTimeoutMilliseconds) : SMB2Client(responseTimeoutMilliseconds)
{
    public bool ConnectTo(IPAddress address, int port) => Connect(address, SMBTransportType.DirectTCPTransport, port);
}

/// <summary>
/// A share of an SMB server (SMBLibrary, SMB 2/3): no mount of the operating system, no extra right of the container. Without a share the location is the whole
/// server: the top level lists its shares as folders (they cannot be made, renamed or deleted here), and the first segment of a path is the share.
/// </summary>
public sealed class SmbProvider(LocationSettings settings, ConnectionPool<SmbSession> pool) : ILocationProvider
{
    public const int DefaultPort = 445;

    private const int ResponseTimeout = 20000;

    private readonly string _base = PathGuard.Normalize(settings.RootPath);

    public LocationKind Kind => LocationKind.Smb;

    public bool HasPermissions => false;

    private bool ServerMode => string.IsNullOrWhiteSpace(settings.Share);

    private int Port => settings.Port is > 0 and < 65536 ? settings.Port.Value : DefaultPort;

    public Task CheckAsync(CancellationToken ct) => Task.Run(() =>
    {
        if (ServerMode)
        {
            ListShares();
            return;
        }

        using var lease = Rent(string.Empty);
        if (!Exists(lease.Client, Resolve(string.Empty), out var folder) || !folder)
        {
            throw new LocationException(LocationError.NotFound);
        }
    }, ct);

    public Task<IReadOnlyList<LocationEntry>> ListAsync(string path, CancellationToken ct) => Task.Run<IReadOnlyList<LocationEntry>>(() =>
    {
        var normalized = PathGuard.Check(path);
        if (ServerMode && normalized.Length == 0)
        {
            return ListShares().Select(s => new LocationEntry(s, s, true, 0, DateTime.MinValue)).ToList();
        }

        using var lease = Rent(normalized);
        return Run(lease, s => List(s, normalized, ct));
    }, ct);

    public Task<LocationEntry?> GetAsync(string path, CancellationToken ct) => Task.Run(() =>
    {
        var normalized = PathGuard.Check(path);
        if (normalized.Length == 0)
        {
            return new LocationEntry(string.Empty, string.Empty, true, 0, DateTime.MinValue);
        }

        if (ServerMode && !normalized.Contains('/'))
        {
            var share = ListShares().FirstOrDefault(s => string.Equals(s, normalized, StringComparison.OrdinalIgnoreCase));
            return share is null ? null : new LocationEntry(share, share, true, 0, DateTime.MinValue);
        }

        using var lease = Rent(normalized);
        return Run(lease, s => Stat(s, normalized));
    }, ct);

    public Task<Stream> OpenReadAsync(string path, long offset, CancellationToken ct) => Task.Run<Stream>(() =>
    {
        var normalized = PathGuard.Check(path);
        RequireInsideShare(normalized);
        var lease = Rent(normalized);
        try
        {
            var store = lease.Client.Store!;
            var status = store.CreateFile(out var handle, out _, Resolve(normalized), AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE, FileAttributes.Normal,
                ShareAccess.Read | ShareAccess.Write, CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);
            Check(status);
            store.GetFileInformation(out var info, handle, FileInformationClass.FileStandardInformation);
            var length = info is FileStandardInformation standard ? standard.EndOfFile : 0;
            return new SmbReadStream(lease, handle, length, (int)Math.Min(lease.Client.Client.MaxReadSize, 1024 * 1024)) { Position = offset };
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }, ct);

    public async Task WriteAsync(string path, Stream content, long? length, bool overwrite, CancellationToken ct)
    {
        var normalized = PathGuard.Check(path);
        RequireInsideShare(normalized);
        using var lease = Rent(normalized);
        var store = lease.Client.Store!;
        var remote = Resolve(normalized);
        object handle;
        try
        {
            if (Exists(lease.Client, remote, out var folder) && (folder || !overwrite))
            {
                throw new LocationException(LocationError.Conflict);
            }

            EnsureParent(lease.Client, normalized);
            var status = store.CreateFile(out handle, out _, remote, AccessMask.GENERIC_WRITE | AccessMask.SYNCHRONIZE, FileAttributes.Normal, ShareAccess.None,
                overwrite ? CreateDisposition.FILE_OVERWRITE_IF : CreateDisposition.FILE_CREATE, CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);
            Check(status);
        }
        catch (Exception ex) when (ex is not LocationException)
        {
            lease.Broken = true;
            throw Unreachable(ex);
        }

        try
        {
            var chunk = (int)Math.Min(lease.Client.Client.MaxWriteSize, 1024 * 1024);
            var buffer = new byte[chunk];
            long position = 0;
            int read;
            while ((read = await ReadFullAsync(content, buffer, ct)) > 0)
            {
                var slice = read == buffer.Length ? buffer : buffer[..read];
                var done = 0;
                while (done < read)
                {
                    var part = done == 0 ? slice : slice[done..];
                    Check(store.WriteFile(out var written, handle, position, part));
                    if (written <= 0)
                    {
                        throw new LocationException(LocationError.Remote, "The share took no data.");
                    }

                    position += written;
                    done += written;
                }
            }
        }
        catch (Exception ex) when (ex is not (LocationException or OperationCanceledException))
        {
            lease.Broken = true;
            throw Unreachable(ex);
        }
        finally
        {
            CloseQuietly(store, handle);
        }
    }

    public Task CreateFolderAsync(string path, CancellationToken ct) => Task.Run(() =>
    {
        var normalized = PathGuard.Check(path);
        RequireInsideShare(normalized);
        using var lease = Rent(normalized);
        Run(lease, s =>
        {
            if (Exists(s, Resolve(normalized), out _))
            {
                throw new LocationException(LocationError.Conflict);
            }

            EnsureParent(s, normalized);
            MakeFolder(s, Resolve(normalized));
            return true;
        });
    }, ct);

    public bool CanMove(string from, string to)
        => !ServerMode || string.Equals(ShareOf(from), ShareOf(to), StringComparison.OrdinalIgnoreCase);

    public Task MoveAsync(string from, string to, bool overwrite, CancellationToken ct) => Task.Run(() =>
    {
        var source = PathGuard.Check(from);
        var target = PathGuard.Check(to);
        RequireInsideShare(source);
        RequireInsideShare(target);
        if (!CanMove(source, target))
        {
            throw new LocationException(LocationError.NotSupported);
        }

        using var lease = Rent(source);
        Run(lease, s =>
        {
            var remoteSource = Resolve(source);
            var remoteTarget = Resolve(target);
            if (!Exists(s, remoteSource, out var folder))
            {
                throw new LocationException(LocationError.NotFound);
            }

            var caseOnly = string.Equals(remoteSource, remoteTarget, StringComparison.OrdinalIgnoreCase);
            if (!caseOnly && Exists(s, remoteTarget, out var targetFolder) && (!overwrite || folder || targetFolder))
            {
                throw new LocationException(LocationError.Conflict);
            }

            EnsureParent(s, target);
            var store = s.Store!;
            var status = store.CreateFile(out var handle, out _, remoteSource, AccessMask.GENERIC_READ | AccessMask.DELETE | AccessMask.SYNCHRONIZE, FileAttributes.Normal,
                ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete, CreateDisposition.FILE_OPEN,
                (folder ? CreateOptions.FILE_DIRECTORY_FILE : CreateOptions.FILE_NON_DIRECTORY_FILE) | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);
            Check(status);
            try
            {
                Check(store.SetFileInformation(handle, new FileRenameInformationType2 { FileName = remoteTarget, ReplaceIfExists = overwrite }));
            }
            finally
            {
                CloseQuietly(store, handle);
            }

            return true;
        });
    }, ct);

    public Task DeleteAsync(string path, CancellationToken ct) => Task.Run(() =>
    {
        var normalized = PathGuard.Check(path);
        if (normalized.Length == 0)
        {
            throw new LocationException(LocationError.InvalidPath);
        }

        RequireInsideShare(normalized);
        using var lease = Rent(normalized);
        Run(lease, s =>
        {
            Remove(s, normalized, ct);
            return true;
        });
    }, ct);

    public Task<LocationSpace?> SpaceAsync(CancellationToken ct) => Task.Run(() =>
    {
        // without a share the space belongs to each share, not to the server
        if (ServerMode)
        {
            return null;
        }

        using var lease = Rent(string.Empty);
        return Run(lease, s =>
        {
            var status = s.Store!.GetFileSystemInformation(out var info, FileSystemInformationClass.FileFsFullSizeInformation);
            if (status != NTStatus.STATUS_SUCCESS || info is not FileFsFullSizeInformation size)
            {
                return (LocationSpace?)null;
            }

            var unit = (long)size.SectorsPerAllocationUnit * size.BytesPerSector;
            return new LocationSpace(size.TotalAllocationUnits * unit, size.CallerAvailableAllocationUnits * unit);
        });
    }, ct);

    // ---- connection ---------------------------------------------------------------------------------------------------------------

    private string? ShareOf(string normalized)
    {
        if (!ServerMode)
        {
            return settings.Share!.Trim();
        }

        var cut = normalized.IndexOf('/');
        return cut < 0 ? normalized : normalized[..cut];
    }

    /// <summary>The path inside the share (backslashes, no leading one).</summary>
    private string Resolve(string normalized)
    {
        string inside;
        if (ServerMode)
        {
            var cut = normalized.IndexOf('/');
            inside = cut < 0 ? string.Empty : normalized[(cut + 1)..];
        }
        else
        {
            inside = PathGuard.Combine(_base, normalized);
        }

        return inside.Replace('/', '\\');
    }

    /// <summary>At the level of the shares nothing can be made, renamed or deleted: that is the business of the server.</summary>
    private void RequireInsideShare(string normalized)
    {
        if (ServerMode && !normalized.Contains('/'))
        {
            throw new LocationException(LocationError.ShareLevel);
        }
    }

    private ConnectionPool<SmbSession>.Lease Rent(string normalized)
    {
        var share = ShareOf(normalized);
        if (string.IsNullOrWhiteSpace(share))
        {
            throw new LocationException(LocationError.ShareLevel);
        }

        var key = ConnectionPool<SmbSession>.KeyOf("smb", settings.Host, Port.ToString(System.Globalization.CultureInfo.InvariantCulture), settings.Domain, settings.Username, settings.Password, share.ToLowerInvariant());
        return pool.RentAsync(key, () => Task.FromResult(Open(share))).GetAwaiter().GetResult();
    }

    private SmbSession Open(string share)
    {
        var client = Login();
        var store = client.TreeConnect(share, out var status);
        if (status != NTStatus.STATUS_SUCCESS || store is null)
        {
            try
            {
                client.Logoff();
            }
            finally
            {
                client.Disconnect();
            }

            throw status is NTStatus.STATUS_ACCESS_DENIED ? new LocationException(LocationError.AccessDenied) : new LocationException(LocationError.NotFound, "Share: " + status);
        }

        return new SmbSession { Client = client, Store = store };
    }

    private PortClient Login()
    {
        if (string.IsNullOrWhiteSpace(settings.Host))
        {
            throw new LocationException(LocationError.Unreachable);
        }

        IPAddress? address;
        try
        {
            var host = settings.Host.Trim();
            address = IPAddress.TryParse(host, out var ip) ? ip : Dns.GetHostAddresses(host).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                ?? Dns.GetHostAddresses(host).FirstOrDefault();
        }
        catch (SocketException ex)
        {
            throw new LocationException(LocationError.Unreachable, ex.Message, ex);
        }

        if (address is null)
        {
            throw new LocationException(LocationError.Unreachable, "The name of the server is unknown.");
        }

        var client = new PortClient(ResponseTimeout);
        bool connected;
        try
        {
            connected = client.ConnectTo(address, Port);
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            throw new LocationException(LocationError.Unreachable, ex.Message, ex);
        }

        if (!connected)
        {
            throw new LocationException(LocationError.Unreachable);
        }

        var login = client.Login(settings.Domain?.Trim() ?? string.Empty, settings.Username ?? string.Empty, settings.Password ?? string.Empty);
        if (login != NTStatus.STATUS_SUCCESS)
        {
            client.Disconnect();
            throw login is NTStatus.STATUS_LOGON_FAILURE or NTStatus.STATUS_ACCESS_DENIED or NTStatus.STATUS_ACCOUNT_DISABLED or NTStatus.STATUS_PASSWORD_EXPIRED
                ? new LocationException(LocationError.AccessDenied)
                : new LocationException(LocationError.Unreachable, login.ToString());
        }

        return client;
    }

    /// <summary>The shares of the server (administrative ones like C$ and IPC$ are left out).</summary>
    private List<string> ListShares()
    {
        var client = Login();
        try
        {
            var shares = client.ListShares(out var status);
            if (status != NTStatus.STATUS_SUCCESS || shares is null)
            {
                throw new LocationException(LocationError.Remote, status.ToString());
            }

            return shares.Where(s => !s.EndsWith('$')).Order(StringComparer.OrdinalIgnoreCase).ToList();
        }
        finally
        {
            try
            {
                client.Logoff();
            }
            catch (Exception)
            {
                // gone anyway
            }

            client.Disconnect();
        }
    }

    /// <summary>An operation on a lent session; a dropped connection marks it broken.</summary>
    private static T Run<T>(ConnectionPool<SmbSession>.Lease lease, Func<SmbSession, T> action)
    {
        try
        {
            return action(lease.Client);
        }
        catch (LocationException ex) when (ex.Code is LocationError.Unreachable or LocationError.Timeout)
        {
            lease.Broken = true;
            throw;
        }
        catch (Exception ex) when (ex is not (LocationException or OperationCanceledException))
        {
            lease.Broken = true;
            throw Unreachable(ex);
        }
    }

    private static LocationException Unreachable(Exception ex)
        => ex as LocationException ?? new LocationException(ex is TimeoutException ? LocationError.Timeout : LocationError.Unreachable, ex.Message, ex);

    // ---- operations ---------------------------------------------------------------------------------------------------------------

    private List<LocationEntry> List(SmbSession s, string normalized, CancellationToken ct)
    {
        var store = s.Store!;
        var status = store.CreateFile(out var handle, out _, Resolve(normalized), AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE, FileAttributes.Directory,
            ShareAccess.Read | ShareAccess.Write, CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE, null);
        Check(status);
        try
        {
            status = store.QueryDirectory(out var files, handle, "*", FileInformationClass.FileDirectoryInformation);
            if (status is not (NTStatus.STATUS_SUCCESS or NTStatus.STATUS_NO_MORE_FILES))
            {
                Check(status);
            }

            var entries = new List<LocationEntry>();
            foreach (var file in (files ?? []).OfType<FileDirectoryInformation>())
            {
                ct.ThrowIfCancellationRequested();
                if (file.FileName is "." or "..")
                {
                    continue;
                }

                var folder = file.FileAttributes.HasFlag(FileAttributes.Directory);
                var hidden = file.FileName.StartsWith('.') || file.FileAttributes.HasFlag(FileAttributes.Hidden);
                entries.Add(new LocationEntry(file.FileName, PathGuard.Combine(normalized, file.FileName), folder, folder ? 0 : file.EndOfFile, file.LastWriteTime.ToUniversalTime(), hidden,
                    file.FileAttributes.HasFlag(FileAttributes.ReparsePoint)));
            }

            return entries;
        }
        finally
        {
            CloseQuietly(store, handle);
        }
    }

    private LocationEntry? Stat(SmbSession s, string normalized)
    {
        var store = s.Store!;
        var status = store.CreateFile(out var handle, out _, Resolve(normalized), AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE, FileAttributes.Normal,
            ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete, CreateDisposition.FILE_OPEN, CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);
        if (IsMissing(status))
        {
            return null;
        }

        Check(status);
        try
        {
            store.GetFileInformation(out var standard, handle, FileInformationClass.FileStandardInformation);
            store.GetFileInformation(out var basic, handle, FileInformationClass.FileBasicInformation);
            var folder = basic is FileBasicInformation b && b.FileAttributes.HasFlag(FileAttributes.Directory);
            var size = standard is FileStandardInformation st ? st.EndOfFile : 0;
            var modified = basic is FileBasicInformation m && m.LastWriteTime.Time is { } time ? time.ToUniversalTime() : DateTime.MinValue;
            var name = PathGuard.NameOf(normalized);
            return new LocationEntry(name, normalized, folder, folder ? 0 : size, modified, name.StartsWith('.'));
        }
        finally
        {
            CloseQuietly(store, handle);
        }
    }

    private static bool Exists(SmbSession s, string remote, out bool folder)
    {
        folder = false;
        var store = s.Store!;
        var status = store.CreateFile(out var handle, out _, remote, AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE, FileAttributes.Normal,
            ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete, CreateDisposition.FILE_OPEN, CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);
        if (status != NTStatus.STATUS_SUCCESS)
        {
            if (IsMissing(status))
            {
                return false;
            }

            Check(status);
            return false;
        }

        try
        {
            if (store.GetFileInformation(out var info, handle, FileInformationClass.FileBasicInformation) == NTStatus.STATUS_SUCCESS && info is FileBasicInformation basic)
            {
                folder = basic.FileAttributes.HasFlag(FileAttributes.Directory);
            }

            return true;
        }
        finally
        {
            CloseQuietly(store, handle);
        }
    }

    private static void MakeFolder(SmbSession s, string remote)
    {
        var status = s.Store!.CreateFile(out var handle, out _, remote, AccessMask.GENERIC_WRITE | AccessMask.SYNCHRONIZE, FileAttributes.Directory, ShareAccess.Read | ShareAccess.Write,
            CreateDisposition.FILE_CREATE, CreateOptions.FILE_DIRECTORY_FILE, null);
        Check(status);
        CloseQuietly(s.Store!, handle);
    }

    private void EnsureParent(SmbSession s, string normalized)
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
            if (ServerMode && !current.Contains('/'))
            {
                continue; // the share itself
            }

            var remote = Resolve(current);
            if (!Exists(s, remote, out _))
            {
                try
                {
                    MakeFolder(s, remote);
                }
                catch (LocationException ex) when (ex.Code == LocationError.Conflict && Exists(s, remote, out var folder) && folder)
                {
                    // made at the same moment by another upload into the same new folder
                }
            }
        }
    }

    private void Remove(SmbSession s, string normalized, CancellationToken ct)
    {
        var remote = Resolve(normalized);
        if (!Exists(s, remote, out var folder))
        {
            throw new LocationException(LocationError.NotFound);
        }

        if (folder)
        {
            // SMB deletes only empty folders: first what is inside
            foreach (var child in List(s, normalized, ct))
            {
                ct.ThrowIfCancellationRequested();
                Remove(s, child.Path, ct);
            }
        }

        var store = s.Store!;
        var status = store.CreateFile(out var handle, out _, remote, AccessMask.DELETE | AccessMask.SYNCHRONIZE, FileAttributes.Normal, ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete,
            CreateDisposition.FILE_OPEN, (folder ? CreateOptions.FILE_DIRECTORY_FILE : CreateOptions.FILE_NON_DIRECTORY_FILE) | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);
        Check(status);
        try
        {
            Check(store.SetFileInformation(handle, new FileDispositionInformation { DeletePending = true }));
        }
        finally
        {
            CloseQuietly(store, handle);
        }
    }

    private static async Task<int> ReadFullAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), ct);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static void CloseQuietly(ISMBFileStore store, object handle)
    {
        try
        {
            store.CloseFile(handle);
        }
        catch (Exception)
        {
            // the connection is gone, and the handle with it
        }
    }

    private static bool IsMissing(NTStatus status)
        => status is NTStatus.STATUS_OBJECT_NAME_NOT_FOUND or NTStatus.STATUS_OBJECT_PATH_NOT_FOUND or NTStatus.STATUS_NO_SUCH_FILE or NTStatus.STATUS_BAD_NETWORK_NAME;

    private static void Check(NTStatus status)
    {
        switch (status)
        {
            case NTStatus.STATUS_SUCCESS:
            case NTStatus.STATUS_END_OF_FILE:
                return;
            case NTStatus.STATUS_OBJECT_NAME_NOT_FOUND:
            case NTStatus.STATUS_OBJECT_PATH_NOT_FOUND:
            case NTStatus.STATUS_NO_SUCH_FILE:
            case NTStatus.STATUS_BAD_NETWORK_NAME:
                throw new LocationException(LocationError.NotFound);
            case NTStatus.STATUS_OBJECT_NAME_COLLISION:
                throw new LocationException(LocationError.Conflict);
            case NTStatus.STATUS_ACCESS_DENIED:
            case NTStatus.STATUS_LOGON_FAILURE:
            case NTStatus.STATUS_SHARING_VIOLATION:
                throw new LocationException(LocationError.AccessDenied, status.ToString());
            case NTStatus.STATUS_MEDIA_WRITE_PROTECTED:
                throw new LocationException(LocationError.ReadOnly);
            case NTStatus.STATUS_DIRECTORY_NOT_EMPTY:
                throw new LocationException(LocationError.Conflict, status.ToString());
            case NTStatus.STATUS_FILE_IS_A_DIRECTORY:
            case NTStatus.STATUS_NOT_A_DIRECTORY:
            case NTStatus.STATUS_OBJECT_NAME_INVALID:
                throw new LocationException(LocationError.InvalidPath, status.ToString());
            case NTStatus.STATUS_DISK_FULL:
                throw new LocationException(LocationError.TooLarge, status.ToString());
            default:
                throw new LocationException(LocationError.Remote, status.ToString());
        }
    }

    /// <summary>A file of a share to read, with a position (ranges of a video); it holds its session until disposed.</summary>
    private sealed class SmbReadStream(ConnectionPool<SmbSession>.Lease lease, object handle, long length, int chunk) : Stream
    {
        private long _position;
        private bool _disposed;

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position
        {
            get => _position;
            set => _position = Math.Clamp(value, 0, length);
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            // never beyond the end: some servers drop the connection then
            var remaining = length - _position;
            if (remaining <= 0 || buffer.Length == 0)
            {
                return 0;
            }

            var wanted = (int)Math.Min(Math.Min(buffer.Length, chunk), remaining);
            NTStatus status;
            byte[] data;
            try
            {
                status = lease.Client.Store!.ReadFile(out data, handle, _position, wanted);
            }
            catch (Exception ex)
            {
                lease.Broken = true;
                throw new IOException(ex.Message, ex);
            }

            if (status == NTStatus.STATUS_END_OF_FILE || data is null || data.Length == 0)
            {
                return 0;
            }

            if (status != NTStatus.STATUS_SUCCESS)
            {
                lease.Broken = true;
                throw new IOException(status.ToString());
            }

            data.AsSpan().CopyTo(buffer);
            _position += data.Length;
            return data.Length;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                _ => length + offset,
            };
            return _position;
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                CloseQuietly(lease.Client.Store!, handle);
                lease.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
