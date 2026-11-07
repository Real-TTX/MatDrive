using System.Collections.Concurrent;
using FluentFTP;
using MatDrive.Data;
using Microsoft.AspNetCore.DataProtection;
using Renci.SshNet;

namespace MatDrive.Services.Locations;

/// <summary>Everything needed to reach a location, with the secrets in clear text (only in memory, never sent to a browser).</summary>
public sealed record LocationSettings(
    LocationKind Kind,
    string RootPath,
    string? Host = null,
    int? Port = null,
    string? Share = null,
    string? Username = null,
    string? Password = null,
    string? Domain = null,
    string? Url = null,
    bool UseTls = false,
    bool AcceptAnyCertificate = false,
    string? PrivateKey = null);

/// <summary>What the last check of a location found: reachable or not (with the reason), and its space when the protocol tells it.</summary>
public sealed record LocationStatus(bool Reachable, DateTime CheckedUtc, LocationError? Error = null, string? Detail = null, LocationSpace? Space = null)
{
    /// <summary>Why it is not reachable, in the language of the request that shows it.</summary>
    public string? Message => Error is { } code ? LocationErrors.Message(code, Detail) : null;
}

/// <summary>
/// The live side of the storage locations (one per app): it makes the provider of a location (with its secrets decrypted), keeps the pools of connections, and
/// remembers the reachability of every location (<see cref="LocationMonitor"/> checks them every few minutes; a browse that fails or succeeds updates it too).
/// The secrets are encrypted with DataProtection (keys in the data volume).
/// </summary>
public sealed class LocationConnections : IDisposable
{
    public const string WebDavClient = "matdrive-locations-webdav";
    public const string WebDavClientAnyCertificate = "matdrive-locations-webdav-any";

    private readonly IDataProtector _protector;
    private readonly IHttpClientFactory _http;
    private readonly ConcurrentDictionary<long, LocationStatus> _status = new();

    public LocationConnections(IDataProtectionProvider protection, IHttpClientFactory http, IConfiguration configuration)
    {
        _protector = protection.CreateProtector("MatDrive.Locations.Secrets");
        _http = http;
        StorageRoot = Path.GetFullPath(configuration["StorageRoot"] is { Length: > 0 } root ? root : "/storage");
        DataDirectory = Path.GetFullPath(configuration["DataDirectory"] ?? "/data");
        Sftp = new ConnectionPool<SftpClient>(c => c.IsConnected, c => c.Dispose());
        Ftp = new ConnectionPool<AsyncFtpClient>(c => c.IsConnected, c => c.Dispose());
        Smb = new ConnectionPool<SmbSession>(s => s.IsAlive, s => s.Dispose());
    }

    /// <summary>The folder below which local locations must lie (setting <c>StorageRoot</c>, default <c>/storage</c>: the bind mounts of the NAS).</summary>
    public string StorageRoot { get; }

    /// <summary>The data folder of MatDrive: never part of a location.</summary>
    public string DataDirectory { get; }

    public ConnectionPool<SftpClient> Sftp { get; }

    public ConnectionPool<AsyncFtpClient> Ftp { get; }

    public ConnectionPool<SmbSession> Smb { get; }

    // ---- secrets ------------------------------------------------------------------------------------------------------------------

    public string? Protect(string? secret) => string.IsNullOrEmpty(secret) ? null : _protector.Protect(secret);

    /// <summary>The secret in clear text; null when there is none or the keys changed (the administrator enters it again).</summary>
    public string? Unprotect(string? protectedSecret)
    {
        if (string.IsNullOrEmpty(protectedSecret))
        {
            return null;
        }

        try
        {
            return _protector.Unprotect(protectedSecret);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    public LocationSettings SettingsOf(StorageLocation l) => new(
        l.Kind, l.RootPath, l.Host, l.Port, l.Share, l.Username, Unprotect(l.SecretProtected), l.Domain, l.Url, l.UseTls, l.AcceptAnyCertificate, Unprotect(l.PrivateKeyProtected));

    // ---- providers ----------------------------------------------------------------------------------------------------------------

    public ILocationProvider Create(StorageLocation location) => Create(SettingsOf(location));

    public ILocationProvider Create(LocationSettings settings) => settings.Kind switch
    {
        LocationKind.Local => new LocalProvider(settings.RootPath, DataDirectory),
        LocationKind.Sftp => new SftpProvider(settings, Sftp),
        LocationKind.Ftp => new FtpProvider(settings, Ftp),
        LocationKind.Smb => new SmbProvider(settings, Smb),
        LocationKind.WebDav => new WebDavProvider(settings, _http.CreateClient(settings.AcceptAnyCertificate ? WebDavClientAnyCertificate : WebDavClient)),
        _ => throw new LocationException(LocationError.NotSupported),
    };

    /// <summary>Whether a local folder may be a location: inside the storage root (links resolved), not the data folder of MatDrive or inside it.</summary>
    public bool IsAllowedLocalFolder(string fullPath)
    {
        var real = PathGuard.RealPath(fullPath);
        var root = PathGuard.RealPath(StorageRoot) ?? StorageRoot;
        var data = PathGuard.RealPath(DataDirectory) ?? DataDirectory;
        return real is not null && PathGuard.IsInside(root, real) && !PathGuard.IsInside(data, real) && !PathGuard.IsInside(real, data);
    }

    // ---- reachability -------------------------------------------------------------------------------------------------------------

    public LocationStatus? StatusOf(long id) => _status.TryGetValue(id, out var status) ? status : null;

    public void Report(long id, LocationStatus status) => _status[id] = status;

    /// <summary>A browse that worked or failed tells about the reachability too (the space of the last check is kept).</summary>
    public void Seen(long id, bool reachable, LocationError? error = null, string? detail = null)
    {
        var old = StatusOf(id);
        if (old is not null && old.Reachable == reachable && DateTime.UtcNow - old.CheckedUtc < TimeSpan.FromMinutes(1))
        {
            return;
        }

        _status[id] = reachable ? new LocationStatus(true, DateTime.UtcNow, Space: old?.Space) : new LocationStatus(false, DateTime.UtcNow, error ?? LocationError.Unreachable, detail);
    }

    public void Forget(long id) => _status.TryRemove(id, out _);

    /// <summary>Closes connections nobody used for a while.</summary>
    public void Sweep()
    {
        Sftp.Sweep();
        Ftp.Sweep();
        Smb.Sweep();
    }

    public void Dispose()
    {
        Sftp.Dispose();
        Ftp.Dispose();
        Smb.Dispose();
    }
}
