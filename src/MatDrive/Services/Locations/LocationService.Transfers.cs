using System.IO.Compression;
using MatDrive.Data;
using MatDrive.Localization;
using MatDrive.Services.Files;

namespace MatDrive.Services.Locations;

/// <summary>What a zip download takes: the open location, the entries chosen and the name of the zip file.</summary>
public sealed record LocationZip(LocationContext Context, IReadOnlyList<LocationEntry> Roots, string Name);

public static partial class LocationService
{
    /// <summary>At most this many entries go into one unpacked zip file (a guard against a zip bomb of tiny files).</summary>
    public const int MaxZipEntries = 100_000;

    // ---- move and copy ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Moves or copies entries into a folder of the same or of another location. Inside one location a move is a rename on the server (an SMB server without a fixed
    /// share copies between shares); between locations the bytes are streamed through MatDrive and, for a move, the source is deleted when everything arrived.
    /// A name that is taken in the target gets a number ("name (2).ext"). Copying needs Viewer on the source, moving Editor; the target needs Editor.
    /// </summary>
    public static async Task<Outcome<int>> TransferAsync(AppDbContext db, LocationConnections connections, long userId, long sourceId, IReadOnlyList<string> paths, long targetId,
        string? targetPath, bool move, CancellationToken ct = default)
    {
        var source = await OpenAsync(db, connections, userId, sourceId, move ? AccessRole.Editor : AccessRole.Viewer, ct);
        var target = sourceId == targetId && source is { CanEdit: true } ? source : await OpenAsync(db, connections, userId, targetId, AccessRole.Editor, ct);
        if (source is null || target is null)
        {
            return Outcome<int>.Denied();
        }

        var same = sourceId == targetId;
        return await Guard(connections, target, async () =>
        {
            var folder = PathGuard.Check(targetPath);
            if (folder.Length > 0 && await target.Provider.GetAsync(folder, ct) is not { IsFolder: true })
            {
                throw new LocationException(LocationError.NotFound);
            }

            var count = 0;
            foreach (var path in Distinct(paths))
            {
                ct.ThrowIfCancellationRequested();
                var entry = await source.Provider.GetAsync(path, ct) ?? throw new LocationException(LocationError.NotFound);
                if (path.Length == 0)
                {
                    throw new LocationException(LocationError.InvalidPath);
                }

                if (same && entry.IsFolder && PathGuard.IsSameOrBelow(folder, path))
                {
                    throw new LocationException(LocationError.InsideItself);
                }

                if (same && move && (PathGuard.Parent(path) ?? string.Empty) == folder)
                {
                    continue; // already there
                }

                var destination = await FreeNameAsync(target.Provider, folder, entry.Name, ct);
                if (same && move && source.Provider.CanMove(path, destination))
                {
                    await source.Provider.MoveAsync(path, destination, overwrite: false, ct);
                }
                else if (same && !move && await source.Provider.TryCopyAsync(path, destination, ct))
                {
                    // the server copied it itself
                }
                else
                {
                    await CopyTreeAsync(source.Provider, entry, target.Provider, destination, ct);
                    if (move)
                    {
                        await source.Provider.DeleteAsync(path, ct);
                    }
                }

                var where = (same ? string.Empty : target.Location.Name) + PathGuard.Display(destination);
                await LogAsync(db, userId, source, move ? "move" : "copy", path, "→ " + where, ct);
                count++;
            }

            return count;
        });
    }

    /// <summary>Copies a file or a folder with everything in it from one provider to another (links are not followed).</summary>
    internal static async Task CopyTreeAsync(ILocationProvider from, LocationEntry entry, ILocationProvider to, string destination, CancellationToken ct)
    {
        if (!entry.IsFolder)
        {
            await using var input = await from.OpenReadAsync(entry.Path, 0, ct);
            await SafeWriteAsync(to, destination, input, entry.Size, overwrite: false, ct);
            return;
        }

        try
        {
            await to.CreateFolderAsync(destination, ct);
        }
        catch (LocationException ex) when (ex.Code == LocationError.Conflict)
        {
            // the folder is there: the content goes into it
        }

        foreach (var child in await from.ListAsync(entry.Path, ct))
        {
            ct.ThrowIfCancellationRequested();
            if (child.IsLink && child.IsFolder)
            {
                continue;
            }

            var target = child.IsFolder ? PathGuard.Combine(destination, child.Name) : await FreeNameAsync(to, destination, child.Name, ct);
            await CopyTreeAsync(from, child, to, target, ct);
        }
    }

    // ---- to and from the drives ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Copies entries of a location into My Drive or a shared drive (a folder of it). The files go through <see cref="FileService.UploadAsync"/>: the right of the
    /// person on the drive, the largest file and the quota are checked there; the space is also checked once before the first byte.
    /// </summary>
    public static async Task<Outcome<int>> ToDriveAsync(AppDbContext db, LocationConnections connections, BlobStore store, long userId, long locationId, IReadOnlyList<string> paths,
        long driveId, long? parentId, CancellationToken ct = default)
    {
        var context = await OpenAsync(db, connections, userId, locationId, ct: ct);
        var actor = Actor.Of(userId);
        var target = await NodeService.TargetAsync(db, actor, driveId, parentId, ct);
        if (context is null || target is null)
        {
            return Outcome<int>.Denied();
        }

        var drive = target.Value.DriveId;
        var entries = new List<LocationEntry>();
        var collected = await Guard(connections, context, async () =>
        {
            long bytes = 0;
            foreach (var path in Distinct(paths))
            {
                var entry = await context.Provider.GetAsync(path, ct) ?? throw new LocationException(LocationError.NotFound);
                entries.Add(entry);
                bytes += await SizeOfAsync(context.Provider, entry, ct);
            }

            return bytes;
        });
        if (!collected.Ok)
        {
            return Outcome<int>.Fail(collected.Error!);
        }

        if (!await FileService.HasRoomAsync(db, drive, collected.Value, ct))
        {
            return Outcome<int>.Fail(Text.T("Locations.Error.QuotaFull"));
        }

        var copied = await Guard(connections, context, async () =>
        {
            foreach (var entry in entries)
            {
                var error = await CopyToDriveAsync(db, store, actor, context.Provider, entry, drive, target.Value.Parent?.Id, ct);
                if (error is not null)
                {
                    throw new LocationException(LocationError.Worded, error);
                }

                var driveName = (await DriveService.SharedDrivesAsync(db, userId, ct)).FirstOrDefault(d => d.Id == drive)?.Name ?? Text.T("Locations.MyDrive");
                await LogAsync(db, userId, context, "copy-to-drive", entry.Path, "→ " + driveName, ct);
            }

            return entries.Count;
        });
        return copied;
    }

    private static async Task<long> SizeOfAsync(ILocationProvider provider, LocationEntry entry, CancellationToken ct)
    {
        if (!entry.IsFolder)
        {
            return entry.Size;
        }

        long total = 0;
        foreach (var child in await provider.ListAsync(entry.Path, ct))
        {
            if (!(child.IsLink && child.IsFolder))
            {
                total += await SizeOfAsync(provider, child, ct);
            }
        }

        return total;
    }

    /// <summary>One entry into a folder of a drive; answers an error of the drive (quota, size, right) or null.</summary>
    private static async Task<string?> CopyToDriveAsync(AppDbContext db, BlobStore store, Actor actor, ILocationProvider provider, LocationEntry entry, long driveId, long? parentId, CancellationToken ct)
    {
        if (NodeService.CleanName(entry.Name) is not { } name)
        {
            return null; // a name no drive can hold (only control characters): left out
        }

        if (!entry.IsFolder)
        {
            await using var input = await provider.OpenReadAsync(entry.Path, 0, ct);
            var up = await FileService.UploadAsync(db, store, actor, driveId, parentId, name, input, UploadMode.KeepBoth, "location", ct);
            return up.Ok ? null : up.Error ?? Text.T("Common.NotAllowed");
        }

        // a folder of the same name is used (its content goes into it), like an upload of a folder
        var existing = await NodeService.FindByNameAsync(db, driveId, parentId, name, ct: ct);
        long folderId;
        if (existing is { Kind: NodeKind.Folder })
        {
            folderId = existing.Id;
        }
        else
        {
            var free = existing is null ? name : await NodeService.FreeNameAsync(db, driveId, parentId, name, null, ct);
            var made = await NodeService.CreateFolderAsync(db, actor, driveId, parentId, free, ct);
            if (!made.Ok)
            {
                return made.Error ?? Text.T("Common.NotAllowed");
            }

            folderId = made.Value!.Id;
        }

        foreach (var child in await provider.ListAsync(entry.Path, ct))
        {
            ct.ThrowIfCancellationRequested();
            if (child.IsLink && child.IsFolder)
            {
                continue;
            }

            if (await CopyToDriveAsync(db, store, actor, provider, child, driveId, folderId, ct) is { } error)
            {
                return error;
            }
        }

        return null;
    }

    /// <summary>Copies files and folders of the drives (that the person may see) into a folder of a location the person may change.</summary>
    public static async Task<Outcome<int>> FromDriveAsync(AppDbContext db, LocationConnections connections, BlobStore store, long userId, long locationId, IReadOnlyList<long> nodeIds,
        string? folder, CancellationToken ct = default)
    {
        var context = await OpenAsync(db, connections, userId, locationId, AccessRole.Editor, ct);
        if (context is null)
        {
            return Outcome<int>.Denied();
        }

        var actor = Actor.Of(userId);
        var nodes = new List<Node>();
        foreach (var id in nodeIds.Distinct())
        {
            if (await DriveAccess.FindAsync(db, actor, id, ct: ct) is not { } access)
            {
                return Outcome<int>.Denied();
            }

            nodes.Add(access.Node);
        }

        return await Guard(connections, context, async () =>
        {
            var target = PathGuard.Check(folder);
            if (target.Length > 0 && await context.Provider.GetAsync(target, ct) is not { IsFolder: true })
            {
                throw new LocationException(LocationError.NotFound);
            }

            foreach (var node in nodes)
            {
                var destination = await FreeNameAsync(context.Provider, target, node.Name, ct);
                await CopyFromDriveAsync(db, store, actor, context.Provider, node.Id, node.Kind, node.DriveId, destination, ct);
                await LogAsync(db, userId, context, "copy-from-drive", destination, ct: ct);
            }

            return nodes.Count;
        });
    }

    private static async Task CopyFromDriveAsync(AppDbContext db, BlobStore store, Actor actor, ILocationProvider provider, long nodeId, NodeKind kind, long driveId, string destination, CancellationToken ct)
    {
        if (kind == NodeKind.File)
        {
            var file = await FileService.OpenAsync(db, store, actor, nodeId, ct: ct) ?? throw new LocationException(LocationError.NotFound);
            await using var input = new FileStream(file.PhysicalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            await SafeWriteAsync(provider, destination, input, file.Size, overwrite: false, ct);
            return;
        }

        await provider.CreateFolderAsync(destination, ct);
        for (var page = 1; ; page++)
        {
            var children = await NodeService.ListFolderAsync(db, actor, driveId, nodeId, new NodeQuery(null, null, "name", false, page, 500), ct);
            if (children is null || children.Items.Count == 0)
            {
                break;
            }

            foreach (var child in children.Items)
            {
                ct.ThrowIfCancellationRequested();
                var target = await FreeNameAsync(provider, destination, child.Name, ct);
                await CopyFromDriveAsync(db, store, actor, provider, child.Id, child.Kind, child.DriveId, target, ct);
            }

            if (page * 500 >= children.Total)
            {
                break;
            }
        }
    }

    // ---- zip ----------------------------------------------------------------------------------------------------------------------

    /// <summary>Checks a zip download before the first byte goes out: the right, the entries and the name of the file.</summary>
    public static async Task<Outcome<LocationZip>> PrepareZipAsync(AppDbContext db, LocationConnections connections, long userId, long locationId, IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        var context = await OpenAsync(db, connections, userId, locationId, ct: ct);
        if (context is null || paths.Count == 0)
        {
            return Outcome<LocationZip>.Denied();
        }

        return await Guard(connections, context, async () =>
        {
            var roots = new List<LocationEntry>();
            foreach (var path in Distinct(paths))
            {
                roots.Add(path.Length == 0
                    ? new LocationEntry(context.Location.Name, string.Empty, true, 0, DateTime.UtcNow)
                    : await context.Provider.GetAsync(path, ct) ?? throw new LocationException(LocationError.NotFound));
            }

            var name = roots.Count == 1 ? (roots[0].IsFolder ? roots[0].Name : Path.GetFileNameWithoutExtension(roots[0].Name)) + ".zip"
                : context.Location.Name + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmm", System.Globalization.CultureInfo.InvariantCulture) + ".zip";
            await LogAsync(db, userId, context, "download", roots.Count == 1 ? roots[0].Path : PathGuard.Parent(roots[0].Path) ?? string.Empty, "zip", ct);
            return new LocationZip(context, roots, name);
        });
    }

    /// <summary>
    /// Streams the zip into <paramref name="output"/> (the response): nothing is held in memory or in a temporary file. The zip format writes the header of an
    /// entry synchronously when the next one starts: those small writes are collected (64 KB) and the caller allows synchronous writes for this response.
    /// </summary>
    public static async Task WriteZipAsync(LocationZip zip, Stream output, CancellationToken ct)
    {
        var buffered = new BufferedStream(output, 64 * 1024);
        await using (var archive = await ZipArchive.CreateAsync(buffered, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: null, ct))
        {
            foreach (var root in zip.Roots)
            {
                await AddToZipAsync(zip.Context.Provider, archive, root, root.Name.Length == 0 ? zip.Context.Location.Name : root.Name, ct);
            }
        }

        // the response is not ours to close: only what is collected goes out
        await buffered.FlushAsync(ct);
    }

    private static async Task AddToZipAsync(ILocationProvider provider, ZipArchive archive, LocationEntry entry, string name, CancellationToken ct)
    {
        if (entry.IsFolder)
        {
            archive.CreateEntry(name + "/");
            foreach (var child in await provider.ListAsync(entry.Path, ct))
            {
                ct.ThrowIfCancellationRequested();
                if (!(child.IsLink && child.IsFolder))
                {
                    await AddToZipAsync(provider, archive, child, name + "/" + child.Name, ct);
                }
            }

            return;
        }

        // pictures, films, music and archives are compressed already: they are only stored
        var level = FileTypes.CategoryOf(entry.Name) is "image" or "video" or "audio" or "archive" ? CompressionLevel.NoCompression : CompressionLevel.Fastest;
        var item = archive.CreateEntry(name, level);
        if (entry.Modified.Year is >= 1980 and < 2108)
        {
            item.LastWriteTime = entry.Modified;
        }

        await using var target = await item.OpenAsync(ct);
        await using var input = await provider.OpenReadAsync(entry.Path, 0, ct);
        await input.CopyToAsync(target, 81920, ct);
    }

    /// <summary>Unpacks a zip file into a new folder next to it (named like the zip file); answers the path of the folder.</summary>
    public static async Task<Outcome<string>> UnzipAsync(AppDbContext db, LocationConnections connections, BlobStore store, long userId, long locationId, string? path, CancellationToken ct = default)
    {
        var context = await OpenAsync(db, connections, userId, locationId, AccessRole.Editor, ct);
        if (context is null)
        {
            return Outcome<string>.Denied();
        }

        return await Guard(connections, context, async () =>
        {
            var normalized = PathGuard.Check(path);
            var entry = await context.Provider.GetAsync(normalized, ct);
            if (entry is not { IsFolder: false } || !entry.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                throw new LocationException(LocationError.NotFound);
            }

            // a zip file is read from its end: a stream of a server that cannot seek goes into a temporary file first
            await using var source = await context.Provider.OpenReadAsync(normalized, 0, ct);
            Stream readable = source;
            FileStream? temp = null;
            if (!source.CanSeek)
            {
                temp = new FileStream(Path.Combine(store.TempFolder, Guid.NewGuid().ToString("N") + ".unzip"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920,
                    FileOptions.DeleteOnClose | FileOptions.Asynchronous);
                await source.CopyToAsync(temp, 81920, ct);
                temp.Position = 0;
                readable = temp;
            }

            await using (temp)
            {
                ZipArchive archive;
                try
                {
                    archive = await ZipArchive.CreateAsync(readable, ZipArchiveMode.Read, leaveOpen: true, entryNameEncoding: null, ct);
                }
                catch (InvalidDataException)
                {
                    throw new LocationException(LocationError.Worded, Text.T("Locations.Error.ZipBroken"));
                }

                await using (archive)
                {
                    if (archive.Entries.Count > MaxZipEntries)
                    {
                        throw new LocationException(LocationError.TooLarge);
                    }

                    var total = archive.Entries.Sum(e => e.Length);
                    var space = await context.Provider.SpaceAsync(ct);
                    if (space is not null && total > space.Free)
                    {
                        throw new LocationException(LocationError.TooLarge);
                    }

                    var folder = await FreeNameAsync(context.Provider, PathGuard.Parent(normalized) ?? string.Empty, Path.GetFileNameWithoutExtension(entry.Name), ct);
                    await context.Provider.CreateFolderAsync(folder, ct);
                    foreach (var item in archive.Entries)
                    {
                        ct.ThrowIfCancellationRequested();
                        var parts = item.FullName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length == 0 || parts.Any(p => p is "." or ".."))
                        {
                            continue; // never outside the new folder (zip slip)
                        }

                        var clean = parts.Select(NodeService.CleanName).ToList();
                        if (clean.Any(p => p is null))
                        {
                            continue;
                        }

                        var target = PathGuard.Combine(folder, string.Join('/', clean!));
                        if (item.FullName.EndsWith('/') || item.FullName.EndsWith('\\'))
                        {
                            if (await context.Provider.GetAsync(target, ct) is null)
                            {
                                await context.Provider.CreateFolderAsync(target, ct);
                            }

                            continue;
                        }

                        await using var input = await item.OpenAsync(ct);
                        await SafeWriteAsync(context.Provider, target, input, item.Length, overwrite: true, ct);
                    }

                    await LogAsync(db, userId, context, "unzip", normalized, "→ " + PathGuard.Display(folder), ct);
                    return folder;
                }
            }
        });
    }
}
