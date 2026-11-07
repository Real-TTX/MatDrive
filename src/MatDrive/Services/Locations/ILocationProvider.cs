using MatDrive.Data;

namespace MatDrive.Services.Locations;

/// <summary>A file or folder of a location. <see cref="Path"/> is relative to the folder of the location, with slashes and without a leading one (<c>photos/2026</c>; the top is empty).</summary>
public sealed record LocationEntry(string Name, string Path, bool IsFolder, long Size, DateTime Modified, bool IsHidden = false, bool IsLink = false);

/// <summary>The space of a location in bytes (what the server tells; not every protocol does).</summary>
public sealed record LocationSpace(long Total, long Free)
{
    public long Used => Math.Max(0, Total - Free);

    public double Share => Total <= 0 ? 0 : Math.Clamp(Used / (double)Total, 0, 1);
}

/// <summary>POSIX permissions of an entry: the nine rwx bits (<c>0..0777</c>), owner and group as the server names them (names or numbers).</summary>
public sealed record LocationPermissions(int Mode, string? Owner = null, string? Group = null)
{
    /// <summary>Only the nine rwx bits; special bits (setuid …) are never touched.</summary>
    public const int Mask = 0b111_111_111;

    public static bool IsValid(int mode) => mode is >= 0 and <= Mask;

    /// <summary>"755" for 493.</summary>
    public static string ToText(int mode) => Convert.ToString(mode & Mask, 8).PadLeft(3, '0');

    /// <summary>"rwxr-xr-x" for 493.</summary>
    public static string ToSymbols(int mode)
    {
        var chars = new char[9];
        for (var i = 0; i < 9; i++)
        {
            var bit = 1 << (8 - i);
            chars[i] = (mode & bit) == 0 ? '-' : (i % 3) switch { 0 => 'r', 1 => 'w', _ => 'x' };
        }

        return new string(chars);
    }

    /// <summary>The mode of the nine flags (owner, group, others; read, write, execute each).</summary>
    public static int FromFlags(bool ur, bool uw, bool ux, bool gr, bool gw, bool gx, bool or, bool ow, bool ox)
        => (ur ? 256 : 0) | (uw ? 128 : 0) | (ux ? 64 : 0) | (gr ? 32 : 0) | (gw ? 16 : 0) | (gx ? 8 : 0) | (or ? 4 : 0) | (ow ? 2 : 0) | (ox ? 1 : 0);
}

/// <summary>What went wrong at a location: a code the business layer words for the person (<c>Locations.Error.{Code}</c>), and what the server said (for remote errors).</summary>
public sealed class LocationException(LocationError code, string? detail = null, Exception? inner = null)
    : Exception(detail ?? code.ToString(), inner)
{
    public LocationError Code { get; } = code;

    /// <summary>The words of the server (shown with remote errors only; never a local path of the container).</summary>
    public string? Detail { get; } = detail;
}

public enum LocationError
{
    NotFound,
    Conflict,
    InvalidPath,
    InvalidName,
    ReadOnly,
    AccessDenied,
    Unreachable,
    Timeout,
    ShareLevel,
    NotSupported,
    InsideItself,
    TooLarge,
    FolderMissing,

    /// <summary>The detail is the whole message (an error of a drive during a copy into it, already worded).</summary>
    Worded,
    Remote,
}

/// <summary>
/// One storage location, the same for every protocol (local folder, SMB, SFTP, FTP, WebDAV). Every path is relative to the folder of the location and checked by the
/// provider (<see cref="PathGuard"/>): nothing outside it can be reached. A provider is made per request (<see cref="LocationConnections.Create"/>); remote ones take
/// their connections from a pool. Errors are <see cref="LocationException"/>s; anything else the business layer turns into "the server answered with an error".
/// </summary>
public interface ILocationProvider
{
    LocationKind Kind { get; }

    /// <summary>Whether the protocol knows POSIX permissions (local on Linux, SFTP, FTP with a Unix listing).</summary>
    bool HasPermissions { get; }

    /// <summary>Connects and looks at the top folder; throws when that fails.</summary>
    Task CheckAsync(CancellationToken ct);

    /// <summary>The entries of a folder (unsorted).</summary>
    Task<IReadOnlyList<LocationEntry>> ListAsync(string path, CancellationToken ct);

    /// <summary>One entry, or null when there is none.</summary>
    Task<LocationEntry?> GetAsync(string path, CancellationToken ct);

    /// <summary>A file to read from <paramref name="offset"/> on (a range of a video). The stream holds the connection until it is disposed.</summary>
    Task<Stream> OpenReadAsync(string path, long offset, CancellationToken ct);

    /// <summary>Writes a file from a stream (created, or replaced when <paramref name="overwrite"/>); missing folders above it are made.</summary>
    Task WriteAsync(string path, Stream content, long? length, bool overwrite, CancellationToken ct);

    Task CreateFolderAsync(string path, CancellationToken ct);

    /// <summary>Moves or renames inside the location (a folder with everything in it).</summary>
    Task MoveAsync(string from, string to, bool overwrite, CancellationToken ct);

    /// <summary>Whether <see cref="MoveAsync"/> can do this in one step (an SMB server without a fixed share cannot move between shares).</summary>
    bool CanMove(string from, string to) => true;

    /// <summary>A copy made by the server itself (a local folder, WebDAV COPY); false when the protocol has none and the bytes must go through MatDrive.</summary>
    Task<bool> TryCopyAsync(string from, string to, CancellationToken ct) => Task.FromResult(false);

    /// <summary>Deletes a file, or a folder with everything in it (for good).</summary>
    Task DeleteAsync(string path, CancellationToken ct);

    Task<LocationSpace?> SpaceAsync(CancellationToken ct);

    Task<LocationPermissions?> PermissionsAsync(string path, CancellationToken ct) => Task.FromResult<LocationPermissions?>(null);

    Task SetPermissionsAsync(string path, int mode, CancellationToken ct) => throw new LocationException(LocationError.NotSupported);
}
