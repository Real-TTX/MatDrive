using System.Security.Cryptography;
using System.Text;
using MatDrive.Data;
using MatDrive.Localization;
using MatDrive.Services.Files;

namespace MatDrive.Services.Locations;

/// <summary>A location opened by a person: the location, their role on it and the provider to reach it.</summary>
public sealed record LocationContext(StorageLocation Location, AccessRole Role, ILocationProvider Provider)
{
    public bool CanEdit => Role >= AccessRole.Editor;
}

/// <summary>What the list of a folder asks for (as it comes from the toolbar).</summary>
public sealed record LocationQuery(string? Search, string? Type, bool ShowHidden, string Sort, bool Descending, int Page, int PageSize);

/// <summary>A folder of a location with the page of its entries; <see cref="Error"/> when the server could not be read (the page shows it instead of the list).</summary>
public sealed record LocationFolder(LocationContext Context, string Path, IReadOnlyList<LocationEntry> Items, int Total, int Matching, string? Error);

/// <summary>A file to stream (the entry with its size and date, and the open location).</summary>
public sealed record LocationFile(LocationContext Context, LocationEntry Entry);

/// <summary>What happens when an upload meets a file of the same name: refuse (the browser asks the person), replace it, or keep both ("name (2).ext").</summary>
public enum LocationConflict
{
    Ask,
    Replace,
    KeepBoth,
}

/// <summary>The answer of saving the text of the editor.</summary>
public enum LocationSave
{
    Saved,
    Conflict,
    Failed,
    Denied,
}

/// <summary>The answer of an upload: the file, or that a file of the name is there (the browser asks), or an error.</summary>
public sealed record LocationWrite(LocationEntry? Entry, string? Error = null, bool Exists = false, bool NotAllowed = false);

/// <summary>The details of an entry for the dialog "Properties".</summary>
public sealed record LocationProperties(LocationEntry Entry, string Where, string KindName, int? Items, LocationPermissions? Permissions, bool CanChangePermissions);

/// <summary>
/// Browsing a storage location and everything done with its files: list, read (with ranges), upload (streamed into a temporary name and renamed when complete,
/// so a broken upload never leaves half a file), new folders, rename, delete for good, text for the editor, properties and rwx permissions. Every function opens
/// the location through <see cref="LocationAccess"/> (Viewer to read, Editor to change; a read-only location is never more than Viewer) and answers "not found"
/// when the person may not. Errors of the servers become worded messages (<see cref="LocationErrors"/>). Changes are written to the activity log with the name of
/// the location. Copies, moves, zip and the way to and from the drives are in <c>LocationService.Transfers.cs</c>, the search in <see cref="LocationSearch"/>.
/// </summary>
public static partial class LocationService
{
    public const int MaxNameLength = NodeService.NameMax;

    // ---- opening ------------------------------------------------------------------------------------------------------------------

    /// <summary>The location with the role of the person and its provider, or null when they may not (or it is switched off).</summary>
    public static async Task<LocationContext?> OpenAsync(AppDbContext db, LocationConnections connections, long userId, long locationId, AccessRole minimum = AccessRole.Viewer, CancellationToken ct = default)
    {
        if (await LocationAccess.FindAsync(db, userId, locationId, minimum, ct) is not { } found)
        {
            return null;
        }

        try
        {
            return new LocationContext(found.Location, found.Role, connections.Create(found.Location));
        }
        catch (LocationException)
        {
            // a location whose settings cannot make a provider (an address that is not valid any more): it is there, but cannot be reached
            return new LocationContext(found.Location, found.Role, new BrokenProvider(found.Location.Kind));
        }
    }

    // ---- browsing -----------------------------------------------------------------------------------------------------------------

    /// <summary>A folder: its entries filtered, sorted (folders first) and paged; null when the person may not see the location.</summary>
    public static async Task<LocationFolder?> BrowseAsync(AppDbContext db, LocationConnections connections, long userId, long locationId, string? path, LocationQuery q, CancellationToken ct = default)
    {
        var context = await OpenAsync(db, connections, userId, locationId, ct: ct);
        if (context is null)
        {
            return null;
        }

        string normalized;
        try
        {
            normalized = PathGuard.Check(path);
        }
        catch (LocationException ex)
        {
            return new LocationFolder(context, string.Empty, [], 0, 0, LocationErrors.Message(ex));
        }

        IReadOnlyList<LocationEntry> entries;
        try
        {
            entries = await context.Provider.ListAsync(normalized, ct);
            connections.Seen(locationId, true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Report(connections, locationId, ex);
            return new LocationFolder(context, normalized, [], 0, 0, LocationErrors.Message(ex));
        }

        var visible = entries.Where(e => q.ShowHidden || !e.IsHidden).ToList();
        IEnumerable<LocationEntry> items = visible;
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var s = q.Search.Trim();
            items = items.Where(e => e.Name.Contains(s, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(q.Type))
        {
            items = q.Type == FileTypes.Folder ? items.Where(e => e.IsFolder) : items.Where(e => !e.IsFolder && FileTypes.CategoryOf(e.Name) == q.Type);
        }

        var name = StringComparer.Create(System.Globalization.CultureInfo.CurrentUICulture, System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.NumericOrdering);
        var folders = items.OrderBy(e => e.IsFolder ? 0 : 1);
        var sorted = (q.Sort, q.Descending) switch
        {
            ("size", false) => folders.ThenBy(e => e.Size).ThenBy(e => e.Name, name),
            ("size", true) => folders.ThenByDescending(e => e.Size).ThenBy(e => e.Name, name),
            ("modified", false) => folders.ThenBy(e => e.Modified).ThenBy(e => e.Name, name),
            ("modified", true) => folders.ThenByDescending(e => e.Modified).ThenBy(e => e.Name, name),
            ("name", true) => folders.ThenByDescending(e => e.Name, name),
            _ => folders.ThenBy(e => e.Name, name),
        };
        var matching = sorted.ToList();
        var page = matching.Skip(Math.Max(0, q.Page - 1) * q.PageSize).Take(q.PageSize).ToList();
        return new LocationFolder(context, normalized, page, visible.Count, matching.Count, null);
    }

    /// <summary>The sub-folders of a folder (the pickers of "Move to" and "Copy to").</summary>
    public static async Task<Outcome<IReadOnlyList<LocationEntry>>> FoldersAsync(AppDbContext db, LocationConnections connections, long userId, long locationId, string? path, CancellationToken ct = default)
    {
        var context = await OpenAsync(db, connections, userId, locationId, ct: ct);
        if (context is null)
        {
            return Outcome<IReadOnlyList<LocationEntry>>.Denied();
        }

        return await Guard(connections, context, async () =>
        {
            var name = StringComparer.Create(System.Globalization.CultureInfo.CurrentUICulture, System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.NumericOrdering);
            var entries = await context.Provider.ListAsync(PathGuard.Check(path), ct);
            return (IReadOnlyList<LocationEntry>)entries.Where(e => e.IsFolder && !e.IsHidden).OrderBy(e => e.Name, name).ToList();
        });
    }

    /// <summary>A file to download or preview.</summary>
    public static async Task<Outcome<LocationFile>> FileAsync(AppDbContext db, LocationConnections connections, long userId, long locationId, string? path, CancellationToken ct = default)
    {
        var context = await OpenAsync(db, connections, userId, locationId, ct: ct);
        if (context is null)
        {
            return Outcome<LocationFile>.Denied();
        }

        var entry = await Guard(connections, context, () => context.Provider.GetAsync(PathGuard.Check(path), ct));
        if (!entry.Ok)
        {
            return Outcome<LocationFile>.Fail(entry.Error!);
        }

        return entry.Value is { IsFolder: false } file ? Outcome<LocationFile>.Success(new LocationFile(context, file)) : Outcome<LocationFile>.Denied();
    }

    /// <summary>The ETag of an entry (its size and its date: a server has no hash).</summary>
    public static string ETagOf(LocationEntry entry)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{entry.Size}:{entry.Modified.Ticks}:{entry.Path}")))[..32];

    // ---- writing ------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// An upload into a location: <paramref name="path"/> is the folder and the relative path of the file (an upload of a folder: <c>photos/2026/a.jpg</c>; the
    /// folders are made as needed). The bytes are streamed (never held in memory) into a temporary name and renamed when they are complete.
    /// </summary>
    public static async Task<LocationWrite> UploadAsync(AppDbContext db, LocationConnections connections, long userId, long locationId, string? folder, string? relativePath, Stream content,
        long? length, LocationConflict conflict, CancellationToken ct = default)
    {
        var context = await OpenAsync(db, connections, userId, locationId, AccessRole.Editor, ct);
        if (context is null)
        {
            return new LocationWrite(null, NotAllowed: true);
        }

        var parts = (relativePath ?? string.Empty).Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Select(NodeService.CleanName).ToList();
        if (parts.Count == 0 || parts.Any(p => p is null))
        {
            return new LocationWrite(null, Text.T("Locations.Error.InvalidName"));
        }

        var max = await FileService.MaxUploadAsync(db, ct);
        if (max > 0 && length > max)
        {
            return new LocationWrite(null, Text.T("Locations.Error.TooLarge"));
        }

        try
        {
            var target = PathGuard.Combine(PathGuard.Check(folder), string.Join('/', parts!));
            PathGuard.Check(target);
            var existing = await context.Provider.GetAsync(target, ct);
            if (existing is not null)
            {
                if (existing.IsFolder || conflict == LocationConflict.Ask)
                {
                    return new LocationWrite(existing, Text.T("Locations.Error.Exists", existing.Name), Exists: !existing.IsFolder);
                }

                if (conflict == LocationConflict.KeepBoth)
                {
                    target = await FreeNameAsync(context.Provider, PathGuard.Parent(target) ?? string.Empty, PathGuard.NameOf(target), ct);
                }
            }

            await using var limited = new LimitStream(content, max);
            await SafeWriteAsync(context.Provider, target, limited, length, overwrite: existing is not null && conflict == LocationConflict.Replace, ct);
            var entry = await context.Provider.GetAsync(target, ct) ?? new LocationEntry(PathGuard.NameOf(target), target, false, length ?? 0, DateTime.UtcNow);
            connections.Seen(locationId, true);
            await LogAsync(db, userId, context, "upload", target, ct: ct);
            return new LocationWrite(entry);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Report(connections, locationId, ex);
            return new LocationWrite(null, LocationErrors.Message(ex));
        }
    }

    /// <summary>
    /// Which files of an upload are there already, asked once before the first byte goes out (the browser then asks the person: replace, keep both, skip). Each
    /// folder is listed once; names are compared without case (a share of Windows does not tell them apart). At most <see cref="MaxExistsCheck"/> paths.
    /// </summary>
    public static async Task<Outcome<IReadOnlyList<string>>> ExistingAsync(AppDbContext db, LocationConnections connections, long userId, long locationId, string? folder,
        IReadOnlyList<string> relativePaths, CancellationToken ct = default)
    {
        var context = await OpenAsync(db, connections, userId, locationId, AccessRole.Editor, ct);
        if (context is null)
        {
            return Outcome<IReadOnlyList<string>>.Denied();
        }

        return await Guard(connections, context, async () =>
        {
            var start = PathGuard.Check(folder);
            var listed = new Dictionary<string, HashSet<string>?>(StringComparer.Ordinal);
            var found = new List<string>();
            foreach (var relative in relativePaths.Take(MaxExistsCheck))
            {
                var parts = (relative ?? string.Empty).Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Select(NodeService.CleanName).ToList();
                if (parts.Count == 0 || parts.Any(p => p is null))
                {
                    continue;
                }

                var target = PathGuard.Combine(start, string.Join('/', parts!));
                var parent = PathGuard.Parent(target) ?? string.Empty;
                if (!listed.TryGetValue(parent, out var names))
                {
                    try
                    {
                        names = (await context.Provider.ListAsync(parent, ct)).Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    }
                    catch (LocationException ex) when (ex.Code is LocationError.NotFound or LocationError.InvalidPath)
                    {
                        names = null; // a folder that is not there yet: nothing in it is taken
                    }

                    listed[parent] = names;
                }

                if (names?.Contains(PathGuard.NameOf(target)) == true)
                {
                    found.Add(relative!);
                }
            }

            return (IReadOnlyList<string>)found;
        });
    }

    /// <summary>The most paths <see cref="ExistingAsync"/> looks at (an upload of a large folder: the rest is checked file by file while it arrives).</summary>
    public const int MaxExistsCheck = 20_000;

    public static async Task<Outcome<LocationEntry>> CreateFolderAsync(AppDbContext db, LocationConnections connections, long userId, long locationId, string? parent, string? name, CancellationToken ct = default)
    {
        var context = await OpenAsync(db, connections, userId, locationId, AccessRole.Editor, ct);
        if (context is null)
        {
            return Outcome<LocationEntry>.Denied();
        }

        if (NodeService.CleanName(name) is not { } clean)
        {
            return Outcome<LocationEntry>.Fail(Text.T("Locations.Error.InvalidName"));
        }

        return await Guard(connections, context, async () =>
        {
            var path = PathGuard.Combine(PathGuard.Check(parent), clean);
            if (await context.Provider.GetAsync(path, ct) is not null)
            {
                throw new LocationException(LocationError.Conflict);
            }

            await context.Provider.CreateFolderAsync(path, ct);
            await LogAsync(db, userId, context, "folder", path, ct: ct);
            return await context.Provider.GetAsync(path, ct) ?? new LocationEntry(clean, path, true, 0, DateTime.UtcNow);
        });
    }

    public static async Task<Outcome<LocationEntry>> RenameAsync(AppDbContext db, LocationConnections connections, long userId, long locationId, string? path, string? name, CancellationToken ct = default)
    {
        var context = await OpenAsync(db, connections, userId, locationId, AccessRole.Editor, ct);
        if (context is null)
        {
            return Outcome<LocationEntry>.Denied();
        }

        if (NodeService.CleanName(name) is not { } clean)
        {
            return Outcome<LocationEntry>.Fail(Text.T("Locations.Error.InvalidName"));
        }

        return await Guard(connections, context, async () =>
        {
            var source = PathGuard.Check(path);
            if (source.Length == 0)
            {
                throw new LocationException(LocationError.InvalidPath);
            }

            var entry = await context.Provider.GetAsync(source, ct) ?? throw new LocationException(LocationError.NotFound);
            var target = PathGuard.Combine(PathGuard.Parent(source), clean);
            if (target == source)
            {
                return entry;
            }

            // a change of case only is a rename onto itself; anything else must be free
            if (!string.Equals(target, source, StringComparison.OrdinalIgnoreCase) && await context.Provider.GetAsync(target, ct) is not null)
            {
                throw new LocationException(LocationError.Conflict);
            }

            await context.Provider.MoveAsync(source, target, overwrite: false, ct);
            await LogAsync(db, userId, context, "rename", source, "→ " + PathGuard.Display(target), ct);
            return await context.Provider.GetAsync(target, ct) ?? entry with { Name = clean, Path = target };
        });
    }

    /// <summary>Deletes files and folders for good (a location has no trash); answers how many.</summary>
    public static async Task<Outcome<int>> DeleteAsync(AppDbContext db, LocationConnections connections, long userId, long locationId, IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        var context = await OpenAsync(db, connections, userId, locationId, AccessRole.Editor, ct);
        if (context is null)
        {
            return Outcome<int>.Denied();
        }

        return await Guard(connections, context, async () =>
        {
            var count = 0;
            foreach (var path in Distinct(paths))
            {
                ct.ThrowIfCancellationRequested();
                if (path.Length == 0)
                {
                    throw new LocationException(LocationError.InvalidPath);
                }

                await context.Provider.DeleteAsync(path, ct);
                await LogAsync(db, userId, context, "delete", path, ct: ct);
                count++;
            }

            return count;
        });
    }

    // ---- text ---------------------------------------------------------------------------------------------------------------------

    /// <summary>The text of a file for the editor (UTF-8, at most 5 MB) with its ETag.</summary>
    public static async Task<Outcome<(string Text, string ETag, LocationEntry Entry, bool CanEdit)>> ReadTextAsync(AppDbContext db, LocationConnections connections, long userId, long locationId, string? path, CancellationToken ct = default)
    {
        var file = await FileAsync(db, connections, userId, locationId, path, ct);
        if (!file.Ok)
        {
            return file.NotAllowed ? Outcome<(string, string, LocationEntry, bool)>.Denied() : Outcome<(string, string, LocationEntry, bool)>.Fail(file.Error!);
        }

        var (context, entry) = file.Value!;
        if (entry.Size > FileService.MaxTextEdit)
        {
            return Outcome<(string, string, LocationEntry, bool)>.Fail(Text.T("Locations.Error.TooLargeForEditor"));
        }

        return await Guard(connections, context, async () =>
        {
            await using var stream = await context.Provider.OpenReadAsync(entry.Path, 0, ct);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var text = await reader.ReadToEndAsync(ct);
            return (text, ETagOf(entry), entry, context.CanEdit && FileTypes.IsEditableText(entry.Name, entry.Size));
        });
    }

    /// <summary>Saves the text of the editor; refuses (Conflict) when the file changed since <paramref name="etag"/>.</summary>
    public static async Task<(LocationSave Status, string? ETag, string? Error)> SaveTextAsync(AppDbContext db, LocationConnections connections, long userId, long locationId, string? path, string text, string? etag, CancellationToken ct = default)
    {
        var context = await OpenAsync(db, connections, userId, locationId, AccessRole.Editor, ct);
        if (context is null)
        {
            return (LocationSave.Denied, null, null);
        }

        var bytes = new UTF8Encoding(false).GetBytes(text);
        if (bytes.Length > FileService.MaxTextEdit)
        {
            return (LocationSave.Failed, null, Text.T("Locations.Error.TooLargeForEditor"));
        }

        try
        {
            var normalized = PathGuard.Check(path);
            var current = await context.Provider.GetAsync(normalized, ct);
            if (current is { IsFolder: true } || (current is null && !string.IsNullOrEmpty(etag)))
            {
                return (LocationSave.Denied, null, null);
            }

            if (current is not null && !string.IsNullOrEmpty(etag) && ETagOf(current) != etag)
            {
                return (LocationSave.Conflict, null, Text.T("Locations.Editor.Conflict"));
            }

            await using (var stream = new MemoryStream(bytes))
            {
                await SafeWriteAsync(context.Provider, normalized, stream, bytes.Length, overwrite: current is not null, ct);
            }

            await LogAsync(db, userId, context, "edit", normalized, ct: ct);
            var saved = await context.Provider.GetAsync(normalized, ct);
            return (LocationSave.Saved, saved is null ? null : ETagOf(saved), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Report(connections, locationId, ex);
            return (LocationSave.Failed, null, LocationErrors.Message(ex));
        }
    }

    // ---- properties ---------------------------------------------------------------------------------------------------------------

    public static async Task<Outcome<LocationProperties>> PropertiesAsync(AppDbContext db, LocationConnections connections, long userId, long locationId, string? path, CancellationToken ct = default)
    {
        var context = await OpenAsync(db, connections, userId, locationId, ct: ct);
        if (context is null)
        {
            return Outcome<LocationProperties>.Denied();
        }

        return await Guard(connections, context, async () =>
        {
            var normalized = PathGuard.Check(path);
            var entry = normalized.Length == 0
                ? new LocationEntry(context.Location.Name, string.Empty, true, 0, DateTime.MinValue)
                : await context.Provider.GetAsync(normalized, ct) ?? throw new LocationException(LocationError.NotFound);
            int? items = entry.IsFolder ? (await context.Provider.ListAsync(normalized, ct)).Count : null;
            LocationPermissions? permissions = null;
            if (context.Provider.HasPermissions)
            {
                try
                {
                    permissions = await context.Provider.PermissionsAsync(normalized, ct);
                }
                catch (LocationException ex) when (ex.Code is LocationError.NotSupported or LocationError.Remote)
                {
                    // the server does not tell (an FTP server without a Unix listing)
                }
            }

            return new LocationProperties(entry, context.Location.Name + PathGuard.Display(PathGuard.Parent(normalized) ?? string.Empty), KindName(context.Location.Kind), items,
                permissions, permissions is not null && context.CanEdit && normalized.Length > 0);
        });
    }

    /// <summary>Sets the rwx bits of an entry (local on Linux, SFTP, FTP with SITE CHMOD).</summary>
    public static async Task<Outcome<LocationPermissions>> SetPermissionsAsync(AppDbContext db, LocationConnections connections, long userId, long locationId, string? path, int mode, CancellationToken ct = default)
    {
        var context = await OpenAsync(db, connections, userId, locationId, AccessRole.Editor, ct);
        if (context is null)
        {
            return Outcome<LocationPermissions>.Denied();
        }

        if (!LocationPermissions.IsValid(mode))
        {
            return Outcome<LocationPermissions>.Fail(Text.T("Locations.Error.ModeInvalid"));
        }

        if (!context.Provider.HasPermissions)
        {
            return Outcome<LocationPermissions>.Fail(Text.T("Locations.Error.NotSupported"));
        }

        return await Guard(connections, context, async () =>
        {
            var normalized = PathGuard.Check(path);
            if (normalized.Length == 0)
            {
                throw new LocationException(LocationError.InvalidPath);
            }

            await context.Provider.SetPermissionsAsync(normalized, mode, ct);
            await LogAsync(db, userId, context, "permissions", normalized, LocationPermissions.ToText(mode), ct);
            return await context.Provider.PermissionsAsync(normalized, ct) ?? new LocationPermissions(mode);
        });
    }

    /// <summary>The name of a kind for people (SMB, SFTP …).</summary>
    public static string KindName(LocationKind kind) => Text.T("Locations.Kind." + kind);

    // ---- helpers ------------------------------------------------------------------------------------------------------------------

    /// <summary>The normalised, checked paths without repeats and without paths below another of the list (deleting a folder takes its content).</summary>
    internal static List<string> Distinct(IReadOnlyList<string> paths)
    {
        var list = paths.Select(PathGuard.Check).Distinct(StringComparer.Ordinal).ToList();
        return list.Where(p => !list.Any(o => o != p && o.Length > 0 && PathGuard.IsSameOrBelow(p, o))).ToList();
    }

    /// <summary>The name itself when it is free in the folder, else "name (2).ext", "name (3).ext" … (like the drives).</summary>
    internal static async Task<string> FreeNameAsync(ILocationProvider provider, string folder, string name, CancellationToken ct)
    {
        var path = PathGuard.Combine(folder, name);
        if (await provider.GetAsync(path, ct) is null)
        {
            return path;
        }

        var ext = Path.GetExtension(name);
        var stem = ext.Length > 0 && ext.Length < name.Length ? name[..^ext.Length] : name;
        if (ext == name)
        {
            ext = string.Empty;
        }

        for (var i = 2; i < 1000; i++)
        {
            var candidate = PathGuard.Combine(folder, $"{stem} ({i}){ext}");
            if (await provider.GetAsync(candidate, ct) is null)
            {
                return candidate;
            }
        }

        return PathGuard.Combine(folder, $"{stem} ({Guid.NewGuid().ToString("N")[..6]}){ext}");
    }

    /// <summary>Writes into a temporary name next to the target and renames it when the bytes are complete; a broken write leaves nothing behind.</summary>
    internal static async Task SafeWriteAsync(ILocationProvider provider, string path, Stream content, long? length, bool overwrite, CancellationToken ct)
    {
        var temp = PathGuard.Combine(PathGuard.Parent(path) ?? string.Empty, $".{Guid.NewGuid():N}.matdrive-part");
        try
        {
            await provider.WriteAsync(temp, content, length, overwrite: false, ct);
            await provider.MoveAsync(temp, path, overwrite, ct);
        }
        catch
        {
            try
            {
                await provider.DeleteAsync(temp, CancellationToken.None);
            }
            catch (Exception)
            {
                // nothing was written, or the server is gone: the temporary name is hidden (a dot file) and harmless
            }

            throw;
        }
    }

    /// <summary>Runs an operation of a location; its errors become a worded message, and the reachability of the location is updated.</summary>
    internal static async Task<Outcome<T>> Guard<T>(LocationConnections connections, LocationContext context, Func<Task<T>> action)
    {
        try
        {
            var value = await action();
            connections.Seen(context.Location.Id, true);
            return Outcome<T>.Success(value);
        }
        catch (Exception ex) when (ex is not OperationCanceledException oce || !oce.CancellationToken.IsCancellationRequested)
        {
            Report(connections, context.Location.Id, ex);
            return Outcome<T>.Fail(LocationErrors.Message(ex));
        }
    }

    internal static void Report(LocationConnections connections, long locationId, Exception ex)
    {
        var (code, detail) = LocationErrors.Classify(ex);
        if (code is LocationError.Unreachable or LocationError.Timeout)
        {
            connections.Seen(locationId, false, code, detail);
        }
    }

    /// <summary>An entry of the activity log: the action "location", the name of the location as the target, what was done and where as the detail.</summary>
    internal static async Task LogAsync(AppDbContext db, long userId, LocationContext context, string what, string path, string? extra = null, CancellationToken ct = default)
    {
        var detail = what + " " + PathGuard.Display(path) + (extra is null ? string.Empty : " " + extra);
        ActivityLog.Add(db, userId, "location", detail: detail, target: context.Location.Name);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Counts the bytes of an upload and stops at the largest file of the installation (0 = no limit).</summary>
    private sealed class LimitStream(Stream inner, long max) : Stream
    {
        private long _read;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _read;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => Count(await inner.ReadAsync(buffer, cancellationToken));

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Count(await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken));

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Count(int read)
        {
            _read += read;
            if (max > 0 && _read > max)
            {
                throw new LocationException(LocationError.TooLarge);
            }

            return read;
        }
    }

    /// <summary>The provider of a location whose settings are broken: every call answers "not reachable".</summary>
    private sealed class BrokenProvider(LocationKind kind) : ILocationProvider
    {
        public LocationKind Kind => kind;

        public bool HasPermissions => false;

        private static LocationException Fail => new(LocationError.Unreachable);

        public Task CheckAsync(CancellationToken ct) => throw Fail;

        public Task<IReadOnlyList<LocationEntry>> ListAsync(string path, CancellationToken ct) => throw Fail;

        public Task<LocationEntry?> GetAsync(string path, CancellationToken ct) => throw Fail;

        public Task<Stream> OpenReadAsync(string path, long offset, CancellationToken ct) => throw Fail;

        public Task WriteAsync(string path, Stream content, long? length, bool overwrite, CancellationToken ct) => throw Fail;

        public Task CreateFolderAsync(string path, CancellationToken ct) => throw Fail;

        public Task MoveAsync(string from, string to, bool overwrite, CancellationToken ct) => throw Fail;

        public Task DeleteAsync(string path, CancellationToken ct) => throw Fail;

        public Task<LocationSpace?> SpaceAsync(CancellationToken ct) => throw Fail;
    }
}
