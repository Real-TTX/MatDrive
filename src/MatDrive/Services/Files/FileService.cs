using System.IO.Compression;
using System.Text;
using MatDrive.Data;
using MatDrive.Localization;
using Microsoft.EntityFrameworkCore;

namespace MatDrive.Services.Files;

/// <summary>What happens when an upload meets a file of the same name: a new version of it (like Google Drive and a file system), or a second file with a number.</summary>
public enum UploadMode
{
    NewVersion,
    KeepBoth,
}

/// <summary>A file to stream to the browser.</summary>
public sealed record FileContent(string PhysicalPath, string Name, string MimeType, long Size, string ETag, DateTime LastModified, Node Node, NodeVersion Version);

public sealed record VersionRow(long Id, int Number, long Size, DateTime CreateDate, string? CreatedBy, string Source, bool KeepForever, bool IsCurrent, string? Comment);

/// <summary>The answer of writing a new version with a condition (WebDAV, MatOffice).</summary>
public enum WriteStatus
{
    Written,
    Created,
    Conflict,
    Denied,
    TooLarge,
    QuotaFull,
}

/// <summary>
/// The content of files: uploads (whole or in pieces, folders with their structure), new versions, downloads (with ranges), the version history, zip and unzip,
/// text that is edited in the browser. The bytes go through <see cref="BlobStore"/>; every function checks the right of the person itself.
/// </summary>
public static class FileService
{
    public const long MaxTextEdit = 5 * 1024 * 1024;

    /// <summary>Is there room for <paramref name="bytes"/> more in a drive (its quota)?</summary>
    public static async Task<bool> HasRoomAsync(AppDbContext db, long driveId, long bytes, CancellationToken ct = default)
    {
        var drive = await db.Drives.AsNoTracking().FirstOrDefaultAsync(d => d.Id == driveId, ct);
        if (drive is null)
        {
            return false;
        }

        var usage = await DriveService.UsageAsync(db, drive, ct);
        return usage.Quota is not { } quota || usage.Used + bytes <= quota;
    }

    /// <summary>The largest upload of the installation (0 = no limit).</summary>
    public static async Task<long> MaxUploadAsync(AppDbContext db, CancellationToken ct = default)
        => await db.SystemSettings.AsNoTracking().Where(s => s.Id == 1).Select(s => (long?)s.MaxUploadBytes).FirstOrDefaultAsync(ct) ?? 0;

    /// <summary>
    /// Stores an upload in a folder. <paramref name="relativePath"/> is the path of the file below the folder when a whole folder is uploaded
    /// (<c>holiday/2026/a.jpg</c>: the folders are made as needed); otherwise the file name.
    /// </summary>
    public static async Task<Outcome<Node>> UploadAsync(AppDbContext db, BlobStore store, Actor actor, long driveId, long? parentId, string relativePath, Stream content,
        UploadMode mode = UploadMode.NewVersion, string source = "upload", CancellationToken ct = default)
    {
        var target = await NodeService.TargetAsync(db, actor, driveId, parentId, ct);
        if (target is null)
        {
            return Outcome<Node>.Denied();
        }

        var parts = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Select(NodeService.CleanName).ToList();
        if (parts.Count == 0 || parts.Any(p => p is null))
        {
            return Outcome<Node>.Fail(Text.T("Files.NameInvalid"));
        }

        var (drive, folder) = target.Value;
        foreach (var part in parts.Take(parts.Count - 1))
        {
            folder = await EnsureFolderAsync(db, actor.UserId, drive, folder, part!, ct);
        }

        var name = parts[^1]!;
        StoredBlob blob;
        try
        {
            blob = await store.WriteAsync(content, await MaxUploadAsync(db, ct), ct);
        }
        catch (TooLargeException ex)
        {
            return Outcome<Node>.Fail(ex.Message);
        }

        if (!await HasRoomAsync(db, drive, blob.Size, ct))
        {
            store.Delete(blob.BlobId);
            return Outcome<Node>.Fail(Text.T("Files.QuotaFull"));
        }

        var existing = await NodeService.FindByNameAsync(db, drive, folder?.Id, name, track: true, ct);
        if (existing is not null && existing.Kind == NodeKind.File && mode == UploadMode.NewVersion)
        {
            await AddVersionAsync(db, store, actor.UserId, existing, blob, source, ct);
            return Outcome<Node>.Success(existing);
        }

        if (existing is not null)
        {
            name = await NodeService.FreeNameAsync(db, drive, folder?.Id, name, null, ct);
        }

        var node = await NodeService.AddNodeAsync(db, actor.UserId, drive, folder, NodeKind.File, name, ct);
        await AddVersionAsync(db, store, actor.UserId, node, blob, source, ct, firstVersion: true);
        return Outcome<Node>.Success(node);
    }

    /// <summary>A folder of a name in a folder: the one that is there, or a new one.</summary>
    internal static async Task<Node> EnsureFolderAsync(AppDbContext db, long? userId, long driveId, Node? parent, string name, CancellationToken ct)
    {
        var existing = await NodeService.FindByNameAsync(db, driveId, parent?.Id, name, ct: ct);
        if (existing is { Kind: NodeKind.Folder })
        {
            return existing;
        }

        var free = existing is null ? name : await NodeService.FreeNameAsync(db, driveId, parent?.Id, name, null, ct);
        return await NodeService.AddNodeAsync(db, userId, driveId, parent, NodeKind.Folder, free, ct);
    }

    /// <summary>Makes a stored blob the new current version of a file.</summary>
    internal static async Task AddVersionAsync(AppDbContext db, BlobStore store, long? userId, Node node, StoredBlob blob, string source, CancellationToken ct, bool firstVersion = false)
    {
        var number = firstVersion ? 1 : (await db.NodeVersions.Where(v => v.NodeId == node.Id).MaxAsync(v => (int?)v.Number, ct) ?? 0) + 1;
        var mime = FileTypes.MimeOf(node.Name);
        var version = new NodeVersion { NodeId = node.Id, Number = number, BlobId = blob.BlobId, Size = blob.Size, Sha256 = blob.Sha256, MimeType = mime, Source = source, CreateUserId = userId };
        db.NodeVersions.Add(version);
        await db.SaveChangesAsync(ct);
        await SetCurrentAsync(db, store, userId, node, version, ct);
        ActivityLog.Add(db, userId, firstVersion ? "upload" : "version", node, firstVersion ? null : "v" + number);
        await db.SaveChangesAsync(ct);
        await DriveService.AddUsedAsync(db, node.DriveId, blob.Size, ct);
    }

    private static async Task SetCurrentAsync(AppDbContext db, BlobStore store, long? userId, Node node, NodeVersion version, CancellationToken ct)
    {
        var tracked = db.Entry(node).State == EntityState.Detached ? await db.Nodes.FirstAsync(n => n.Id == node.Id, ct) : node;
        tracked.CurrentVersionId = version.Id;
        tracked.Size = version.Size;
        tracked.MimeType = version.MimeType;
        tracked.ModifiedDate = DateTime.UtcNow;
        tracked.ModifiedById = userId;
        tracked.TextContent = FileTypes.IsIndexable(tracked.Name, version.Size) ? await ReadTextStartAsync(store, version.BlobId, 20000, ct) : null;
        await db.SaveChangesAsync(ct);
        if (tracked.ParentId is { } parent)
        {
            await NodeService.TouchFolderAsync(db, parent, userId, ct);
        }
    }

    /// <summary>
    /// Writes a file at a place by name (WebDAV PUT, MatOffice save): a new file, or a new version of the file of that name. <paramref name="ifMatch"/> (an ETag)
    /// refuses the write when the file changed meanwhile. <paramref name="coalesce"/>: a save of the same person from the same source within that time replaces
    /// their last version instead of adding one (an editor that saves every few seconds).
    /// </summary>
    public static async Task<(WriteStatus Status, Node? Node, NodeVersion? Version)> WriteAsync(AppDbContext db, BlobStore store, Actor actor, long nodeId, Stream content,
        string source, string? ifMatch = null, TimeSpan? coalesce = null, CancellationToken ct = default)
    {
        var access = await DriveAccess.FindAsync(db, actor, nodeId, AccessRole.Editor, track: true, ct: ct);
        if (access is null || access.Node.Kind != NodeKind.File)
        {
            return (WriteStatus.Denied, null, null);
        }

        var node = access.Node;
        var current = node.CurrentVersionId is { } cv ? await db.NodeVersions.FirstOrDefaultAsync(v => v.Id == cv, ct) : null;
        if (!string.IsNullOrEmpty(ifMatch) && ifMatch != "*" && current is not null && Trim(ifMatch) != current.Sha256)
        {
            return (WriteStatus.Conflict, node, current);
        }

        StoredBlob blob;
        try
        {
            blob = await store.WriteAsync(content, await MaxUploadAsync(db, ct), ct);
        }
        catch (TooLargeException)
        {
            return (WriteStatus.TooLarge, node, current);
        }

        var growth = blob.Size - (current is not null && coalesce is not null ? current.Size : 0);
        if (!await HasRoomAsync(db, node.DriveId, growth, ct))
        {
            store.Delete(blob.BlobId);
            return (WriteStatus.QuotaFull, node, current);
        }

        if (current is not null && coalesce is { } window && current.Source == source && current.CreateUserId == actor.UserId
            && current.CreateDate > DateTime.UtcNow - window && !current.KeepForever)
        {
            // the same person saving again from the same editor: the last version takes the new bytes
            var oldBlob = current.BlobId;
            var oldSize = current.Size;
            current.BlobId = blob.BlobId;
            current.Size = blob.Size;
            current.Sha256 = blob.Sha256;
            await db.SaveChangesAsync(ct);
            store.Delete(oldBlob);
            await DriveService.AddUsedAsync(db, node.DriveId, blob.Size - oldSize, ct);
            await SetCurrentAsync(db, store, actor.UserId, node, current, ct);
            return (WriteStatus.Written, node, current);
        }

        await AddVersionAsync(db, store, actor.UserId, node, blob, source, ct);
        var version = await db.NodeVersions.FirstAsync(v => v.Id == node.CurrentVersionId, ct);
        return (WriteStatus.Written, node, version);
    }

    /// <summary>A file (or one of its versions) for download or preview. Records the visit of a signed-in person.</summary>
    public static async Task<FileContent?> OpenAsync(AppDbContext db, BlobStore store, Actor actor, long nodeId, long? versionId = null, CancellationToken ct = default)
    {
        var access = await DriveAccess.FindAsync(db, actor, nodeId, ct: ct);
        if (access is null || access.Node.Kind != NodeKind.File)
        {
            return null;
        }

        var node = access.Node;
        var id = versionId ?? node.CurrentVersionId;
        var version = id is { } vid ? await db.NodeVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == vid && v.NodeId == node.Id, ct) : null;
        if (version is null || !store.Exists(version.BlobId))
        {
            return null;
        }

        if (actor.UserId is { } userId && versionId is null)
        {
            await NodeService.VisitAsync(db, userId, node.Id, ct);
        }

        return new FileContent(store.PathOf(version.BlobId), node.Name, version.MimeType ?? FileTypes.MimeOf(node.Name), version.Size, version.Sha256, version.CreateDate, node, version);
    }

    // ---- versions ---------------------------------------------------------------------------------------------------------------

    public static async Task<IReadOnlyList<VersionRow>?> VersionsAsync(AppDbContext db, Actor actor, long nodeId, CancellationToken ct = default)
    {
        var access = await DriveAccess.FindAsync(db, actor, nodeId, ct: ct);
        if (access is null || access.Node.Kind != NodeKind.File)
        {
            return null;
        }

        var current = access.Node.CurrentVersionId;
        return await db.NodeVersions.AsNoTracking().Where(v => v.NodeId == nodeId).OrderByDescending(v => v.Number)
            .Select(v => new VersionRow(v.Id, v.Number, v.Size, v.CreateDate,
                db.Users.IgnoreQueryFilters().Where(u => u.Id == v.CreateUserId).Select(u => u.DisplayName).FirstOrDefault(), v.Source, v.KeepForever, v.Id == current, v.Comment))
            .ToListAsync(ct);
    }

    /// <summary>Makes an older version the current one again (as a new version: the history stays complete).</summary>
    public static async Task<bool> RestoreVersionAsync(AppDbContext db, BlobStore store, Actor actor, long nodeId, long versionId, CancellationToken ct = default)
    {
        var access = await DriveAccess.FindAsync(db, actor, nodeId, AccessRole.Editor, track: true, ct: ct);
        var version = access is null ? null : await db.NodeVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == versionId && v.NodeId == nodeId, ct);
        if (access is null || version is null || version.Id == access.Node.CurrentVersionId)
        {
            return false;
        }

        if (!await HasRoomAsync(db, access.Node.DriveId, version.Size, ct))
        {
            return false;
        }

        var copy = await store.CopyAsync(version.BlobId, ct);
        await AddVersionAsync(db, store, actor.UserId, access.Node, copy, "restore", ct);
        return true;
    }

    /// <summary>Deletes an older version (never the current one); answers its blob.</summary>
    public static async Task<Guid?> DeleteVersionAsync(AppDbContext db, Actor actor, long nodeId, long versionId, CancellationToken ct = default)
    {
        var access = await DriveAccess.FindAsync(db, actor, nodeId, AccessRole.Editor, ct: ct);
        if (access is null || access.Node.CurrentVersionId == versionId)
        {
            return null;
        }

        var version = await db.NodeVersions.FirstOrDefaultAsync(v => v.Id == versionId && v.NodeId == nodeId, ct);
        if (version is null)
        {
            return null;
        }

        db.NodeVersions.Remove(version);
        ActivityLog.Add(db, actor.UserId, "version-delete", access.Node, "v" + version.Number);
        await db.SaveChangesAsync(ct);
        await DriveService.AddUsedAsync(db, access.Node.DriveId, -version.Size, ct);
        return version.BlobId;
    }

    public static async Task<bool> KeepVersionAsync(AppDbContext db, Actor actor, long nodeId, long versionId, bool keep, CancellationToken ct = default)
    {
        if (await DriveAccess.FindAsync(db, actor, nodeId, AccessRole.Editor, ct: ct) is null)
        {
            return false;
        }

        return await db.NodeVersions.Where(v => v.Id == versionId && v.NodeId == nodeId).ExecuteUpdateAsync(s => s.SetProperty(v => v.KeepForever, keep), ct) > 0;
    }

    // ---- uploads in pieces ------------------------------------------------------------------------------------------------------

    /// <summary>Starts an upload in pieces (files above a few MB): checks the right, the limit and the space before the first byte.</summary>
    public static async Task<Outcome<UploadSession>> StartUploadAsync(AppDbContext db, Actor actor, long driveId, long? parentId, string relativePath, long size, CancellationToken ct = default)
    {
        var target = await NodeService.TargetAsync(db, actor, driveId, parentId, ct);
        if (target is null || actor.UserId is null)
        {
            return Outcome<UploadSession>.Denied();
        }

        var max = await MaxUploadAsync(db, ct);
        if (size < 0 || (max > 0 && size > max))
        {
            return Outcome<UploadSession>.Fail(Text.T("Files.TooLarge"));
        }

        if (!await HasRoomAsync(db, target.Value.DriveId, size, ct))
        {
            return Outcome<UploadSession>.Fail(Text.T("Files.QuotaFull"));
        }

        var session = new UploadSession { UserId = actor.UserId.Value, DriveId = target.Value.DriveId, ParentId = parentId, RelativePath = relativePath, Size = size, ExpiresDate = DateTime.UtcNow.AddHours(24) };
        db.UploadSessions.Add(session);
        await db.SaveChangesAsync(ct);
        return Outcome<UploadSession>.Success(session);
    }

    /// <summary>Appends a piece at <paramref name="offset"/> (pieces come in order; a piece that was sent twice is accepted).</summary>
    public static async Task<Outcome<long>> AppendUploadAsync(AppDbContext db, BlobStore store, long userId, Guid token, long offset, Stream piece, CancellationToken ct = default)
    {
        var session = await db.UploadSessions.FirstOrDefaultAsync(s => s.Token == token && s.UserId == userId && s.ExpiresDate > DateTime.UtcNow, ct);
        if (session is null)
        {
            return Outcome<long>.Denied();
        }

        var path = Path.Combine(store.TempFolder, session.Token.ToString("N") + ".upload");
        await using (var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            if (offset > file.Length)
            {
                return Outcome<long>.Fail(Text.T("Files.UploadGap"));
            }

            file.Position = offset;
            await piece.CopyToAsync(file, ct);
            file.SetLength(file.Position);
            if (file.Length > session.Size)
            {
                return Outcome<long>.Fail(Text.T("Files.TooLarge"));
            }

            session.Received = file.Length;
        }

        await db.SaveChangesAsync(ct);
        return Outcome<long>.Success(session.Received);
    }

    /// <summary>The last piece arrived: the file goes into its folder.</summary>
    public static async Task<Outcome<Node>> FinishUploadAsync(AppDbContext db, BlobStore store, long userId, Guid token, UploadMode mode, CancellationToken ct = default)
    {
        var session = await db.UploadSessions.FirstOrDefaultAsync(s => s.Token == token && s.UserId == userId, ct);
        if (session is null)
        {
            return Outcome<Node>.Denied();
        }

        var path = Path.Combine(store.TempFolder, session.Token.ToString("N") + ".upload");
        if (!File.Exists(path) && session.Size == 0)
        {
            await File.WriteAllBytesAsync(path, [], ct);
        }

        if (!File.Exists(path) || new FileInfo(path).Length != session.Size)
        {
            return Outcome<Node>.Fail(Text.T("Files.UploadIncomplete"));
        }

        db.UploadSessions.Remove(session);
        await db.SaveChangesAsync(ct);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        return await UploadAsync(db, store, Actor.Of(userId), session.DriveId, session.ParentId, session.RelativePath, stream, mode, "upload", ct);
    }

    // ---- zip and unzip ----------------------------------------------------------------------------------------------------------

    /// <summary>Several items (folders with everything in them) as one zip file in the temporary folder; answers its path and a name for the download.</summary>
    public static async Task<(string Path, string Name)?> ZipAsync(AppDbContext db, BlobStore store, Actor actor, IReadOnlyList<long> ids, CancellationToken ct = default)
    {
        var roots = new List<Node>();
        foreach (var id in ids.Distinct())
        {
            if (await DriveAccess.FindAsync(db, actor, id, ct: ct) is { } access)
            {
                roots.Add(access.Node);
            }
        }

        if (roots.Count == 0)
        {
            return null;
        }

        var zipPath = Path.Combine(store.TempFolder, Guid.NewGuid().ToString("N") + ".zip");
        await using (var file = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
        {
            foreach (var root in roots)
            {
                var below = await db.Nodes.AsNoTracking().Where(n => n.Path.StartsWith(root.Path) && !n.InTrash).ToListAsync(ct);
                var byId = below.ToDictionary(n => n.Id);
                foreach (var n in below.OrderBy(n => n.Path.Length))
                {
                    var rel = RelativeName(n, root, byId);
                    if (n.Kind == NodeKind.Folder)
                    {
                        zip.CreateEntry(rel + "/");
                        continue;
                    }

                    if (n.CurrentVersionId is not { } vid)
                    {
                        continue;
                    }

                    var version = await db.NodeVersions.AsNoTracking().FirstAsync(v => v.Id == vid, ct);
                    if (!store.Exists(version.BlobId))
                    {
                        continue;
                    }

                    var entry = zip.CreateEntry(rel, CompressionLevel.Fastest);
                    entry.LastWriteTime = n.ModifiedDate;
                    await using var target = await entry.OpenAsync(ct);
                    await using var source = store.OpenRead(version.BlobId);
                    await source.CopyToAsync(target, ct);
                }
            }
        }

        var name = roots.Count == 1 ? (roots[0].Kind == NodeKind.Folder ? roots[0].Name : Path.GetFileNameWithoutExtension(roots[0].Name)) + ".zip" : "MatDrive-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmm") + ".zip";
        await ActivityLog.WriteAsync(db, actor.UserId, "download", roots.Count == 1 ? roots[0] : null, "zip", driveId: roots[0].DriveId, target: name, ct: ct);
        return (zipPath, name);
    }

    private static string RelativeName(Node n, Node root, Dictionary<long, Node> byId)
    {
        var names = new List<string>();
        for (var cur = n; cur is not null; cur = cur.Id == root.Id ? null : cur.ParentId is { } p && byId.TryGetValue(p, out var parent) ? parent : null)
        {
            names.Add(cur.Name);
        }

        names.Reverse();
        return string.Join('/', names);
    }

    /// <summary>Unpacks a zip file into a new folder next to it (named like the zip file); answers the folder.</summary>
    public static async Task<Outcome<Node>> UnzipAsync(AppDbContext db, BlobStore store, Actor actor, long nodeId, CancellationToken ct = default)
    {
        var file = await OpenAsync(db, store, actor, nodeId, ct: ct);
        if (file is null || !file.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return Outcome<Node>.Denied();
        }

        var target = await NodeService.TargetAsync(db, actor, file.Node.DriveId, file.Node.ParentId, ct);
        if (target is null)
        {
            return Outcome<Node>.Denied();
        }

        try
        {
            await using var stream = File.OpenRead(file.PhysicalPath);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            var total = zip.Entries.Sum(e => e.Length);
            if (!await HasRoomAsync(db, target.Value.DriveId, total, ct))
            {
                return Outcome<Node>.Fail(Text.T("Files.QuotaFull"));
            }

            var folderName = await NodeService.FreeNameAsync(db, target.Value.DriveId, target.Value.Parent?.Id, Path.GetFileNameWithoutExtension(file.Name), null, ct);
            var folder = await NodeService.AddNodeAsync(db, actor.UserId, target.Value.DriveId, target.Value.Parent, NodeKind.Folder, folderName, ct);
            foreach (var entry in zip.Entries)
            {
                var path = entry.FullName.Replace('\\', '/');
                if (path.Split('/').Any(p => p == ".."))
                {
                    continue; // never outside the folder
                }

                if (path.EndsWith('/'))
                {
                    var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(NodeService.CleanName).Where(p => p is not null).ToList();
                    var cur = folder;
                    foreach (var part in parts)
                    {
                        cur = await EnsureFolderAsync(db, actor.UserId, folder.DriveId, cur, part!, ct);
                    }

                    continue;
                }

                await using var entryStream = await entry.OpenAsync(ct);
                await UploadAsync(db, store, actor, folder.DriveId, folder.Id, path, entryStream, UploadMode.KeepBoth, "unzip", ct);
            }

            await ActivityLog.WriteAsync(db, actor.UserId, "unzip", folder, file.Name, ct: ct);
            return Outcome<Node>.Success(folder);
        }
        catch (InvalidDataException)
        {
            return Outcome<Node>.Fail(Text.T("Files.ZipBroken"));
        }
    }

    // ---- text -------------------------------------------------------------------------------------------------------------------

    /// <summary>The text of a file for the editor (UTF-8; at most 5 MB).</summary>
    public static async Task<Outcome<(string Text, Node Node, string ETag)>> ReadTextAsync(AppDbContext db, BlobStore store, Actor actor, long nodeId, CancellationToken ct = default)
    {
        var file = await OpenAsync(db, store, actor, nodeId, ct: ct);
        if (file is null)
        {
            return Outcome<(string, Node, string)>.Denied();
        }

        if (file.Size > MaxTextEdit)
        {
            return Outcome<(string, Node, string)>.Fail(Text.T("Files.TooLargeForEditor"));
        }

        var text = await File.ReadAllTextAsync(file.PhysicalPath, Encoding.UTF8, ct);
        return Outcome<(string, Node, string)>.Success((text, file.Node, file.ETag));
    }

    /// <summary>Saves edited text as a new version (with the ETag the editor started from).</summary>
    public static async Task<(WriteStatus Status, string? ETag)> SaveTextAsync(AppDbContext db, BlobStore store, Actor actor, long nodeId, string text, string? etag, CancellationToken ct = default)
    {
        await using var stream = new MemoryStream(new UTF8Encoding(false).GetBytes(text));
        var (status, _, version) = await WriteAsync(db, store, actor, nodeId, stream, "editor", etag, TimeSpan.FromMinutes(10), ct);
        return (status, version?.Sha256);
    }

    private static async Task<string?> ReadTextStartAsync(BlobStore store, Guid blob, int max, CancellationToken ct)
    {
        try
        {
            await using var stream = store.OpenRead(blob);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[max];
            var read = await reader.ReadBlockAsync(buffer.AsMemory(), ct);
            var text = new string(buffer, 0, read).Replace("\0", string.Empty);
            return text.Length == 0 ? null : text;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string Trim(string etag) => etag.Trim().Trim('"').Replace("W/", string.Empty).Trim('"');
}
