using MatDrive.Data;
using MatDrive.Localization;
using Microsoft.EntityFrameworkCore;

namespace MatDrive.Services.Files;

/// <summary>One row of a list of files and folders.</summary>
public sealed record NodeRow(
    long Id,
    long DriveId,
    long? ParentId,
    NodeKind Kind,
    string Name,
    long Size,
    string? MimeType,
    DateTime ModifiedDate,
    string? ModifiedBy,
    string OwnerName,
    bool IsStarred,
    bool IsShared,
    string? Colour,
    DateTime? TrashedDate,
    string? Location)
{
    public bool IsFolder => Kind == NodeKind.Folder;

    public string Category => IsFolder ? FileTypes.Folder : FileTypes.CategoryOf(Name, MimeType);
}

/// <summary>What a list asks for.</summary>
public sealed record NodeQuery(string? Search, string? Type, string Sort, bool Descending, int Page, int PageSize);

public sealed record NodePage(IReadOnlyList<NodeRow> Items, int Total);

/// <summary>One step of the path above a folder.</summary>
public sealed record Crumb(long? Id, string Name);

/// <summary>
/// Folders and files as a structure: lists (a folder, recent, starred, shared with me, trash, search), new folders, rename, move, copy, trash, restore, delete for
/// good, stars, visits. Names are unique in a folder (case does not count), like on a file system, so WebDAV and downloads never meet two files of the same name.
/// Every function checks the right of the person itself (<see cref="DriveAccess"/>).
/// </summary>
public static class NodeService
{
    public const int NameMax = 255;

    // ---- lists ------------------------------------------------------------------------------------------------------------------

    /// <summary>The children of a folder (or of the top of a drive when <paramref name="folderId"/> is null), folders first.</summary>
    public static async Task<NodePage?> ListFolderAsync(AppDbContext db, Actor actor, long driveId, long? folderId, NodeQuery q, CancellationToken ct = default)
    {
        if (folderId is { } fid)
        {
            var access = await DriveAccess.FindAsync(db, actor, fid, ct: ct);
            if (access is null || access.Node.Kind != NodeKind.Folder)
            {
                return null;
            }

            driveId = access.Node.DriveId;
        }
        else if (actor.UserId is not { } userId || await DriveAccess.FindDriveAsync(db, userId, driveId, ct: ct) is null)
        {
            return null;
        }

        var nodes = db.Nodes.Where(n => n.DriveId == driveId && n.ParentId == folderId && !n.InTrash);
        return await PageAsync(db, actor.UserId, Filter(nodes, q), q, foldersFirst: true, ct);
    }

    /// <summary>The files the person opened last.</summary>
    public static async Task<NodePage> RecentAsync(AppDbContext db, long userId, NodeQuery q, CancellationToken ct = default)
    {
        var visible = Visible(db, userId);
        var visits = db.NodeVisits.Where(v => v.UserId == userId);
        var nodes = visible.Where(n => n.Kind == NodeKind.File && visits.Any(v => v.NodeId == n.Id));
        var rows = Filter(nodes, q).OrderByDescending(n => visits.Where(v => v.NodeId == n.Id).Max(v => v.VisitDate));
        var total = await rows.CountAsync(ct);
        var items = await Project(db, userId, rows.Skip((q.Page - 1) * q.PageSize).Take(q.PageSize)).ToListAsync(ct);
        return new NodePage(items, total);
    }

    public static Task<NodePage> StarredAsync(AppDbContext db, long userId, NodeQuery q, CancellationToken ct = default)
    {
        var stars = db.NodeStars.Where(s => s.UserId == userId);
        return PageAsync(db, userId, Filter(Visible(db, userId).Where(n => stars.Any(s => s.NodeId == n.Id)), q), q, foldersFirst: true, ct);
    }

    /// <summary>The items other people shared with the person (the top items; what is inside a shared folder is opened from there).</summary>
    public static Task<NodePage> SharedWithMeAsync(AppDbContext db, long userId, NodeQuery q, CancellationToken ct = default)
    {
        var shared = db.NodeShares.Where(s => s.UserId == userId).Select(s => s.NodeId);
        var nodes = db.Nodes.Where(n => shared.Contains(n.Id) && !n.InTrash && n.OwnerId != userId);
        return PageAsync(db, userId, Filter(nodes, q), q with { Sort = q.Sort == "name" ? "name" : "modified" }, foldersFirst: false, ct);
    }

    /// <summary>The trash: the items deleted in the drives where the person may delete (their own drive; shared drives where they are editor or more).</summary>
    public static Task<NodePage> TrashAsync(AppDbContext db, long userId, NodeQuery q, CancellationToken ct = default)
    {
        var drives = db.Drives.Where(d => (d.Kind == DriveKind.Personal && d.OwnerId == userId)
            || (d.Kind == DriveKind.Shared && d.Members.Any(m => m.UserId == userId && m.Role >= AccessRole.Editor))).Select(d => d.Id);
        var nodes = db.Nodes.Where(n => drives.Contains(n.DriveId) && n.TrashedDate != null);
        return PageAsync(db, userId, Filter(nodes, q), q with { Sort = "trashed" }, foldersFirst: false, ct);
    }

    /// <summary>Searches names (and the text of text files) in everything the person can see.</summary>
    public static async Task<NodePage> SearchAsync(AppDbContext db, long userId, NodeQuery q, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(q.Search) && string.IsNullOrWhiteSpace(q.Type))
        {
            return new NodePage([], 0);
        }

        var pattern = Pattern(q.Search ?? string.Empty);
        var nodes = Visible(db, userId);
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            nodes = nodes.Where(n => EF.Functions.ILike(n.Name, pattern) || (n.TextContent != null && EF.Functions.ILike(n.TextContent, pattern)));
        }

        return await PageAsync(db, userId, Filter(nodes, q with { Search = null }), q, foldersFirst: true, ct);
    }

    /// <summary>
    /// Everything a person can see (not in the trash): their drives, and below the items shared with them. The shared subtrees are found by their paths (a person
    /// rarely has more than a few hundred shares).
    /// </summary>
    public static IQueryable<Node> Visible(AppDbContext db, long userId)
    {
        var drives = DriveAccess.DrivesOf(db, userId).Select(d => d.Id);
        var sharedPaths = db.NodeShares.Where(s => s.UserId == userId).Select(s => s.Node!.Path);
        return db.Nodes.Where(n => !n.InTrash && (drives.Contains(n.DriveId) || sharedPaths.Any(p => n.Path.StartsWith(p))));
    }

    private static IQueryable<Node> Filter(IQueryable<Node> nodes, NodeQuery q)
    {
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var pattern = Pattern(q.Search);
            nodes = nodes.Where(n => EF.Functions.ILike(n.Name, pattern));
        }

        switch (q.Type)
        {
            case null or "":
                break;
            case "folder":
                nodes = nodes.Where(n => n.Kind == NodeKind.Folder);
                break;
            case "image":
                nodes = nodes.Where(n => n.Kind == NodeKind.File && n.MimeType != null && n.MimeType.StartsWith("image/"));
                break;
            case "video":
                nodes = nodes.Where(n => n.Kind == NodeKind.File && n.MimeType != null && n.MimeType.StartsWith("video/"));
                break;
            case "audio":
                nodes = nodes.Where(n => n.Kind == NodeKind.File && n.MimeType != null && n.MimeType.StartsWith("audio/"));
                break;
            default:
                var endings = FileTypes.ExtensionsOf(q.Type).Select(e => "%" + e).ToArray();
                nodes = nodes.Where(n => n.Kind == NodeKind.File && endings.Any(e => EF.Functions.ILike(n.Name, e)));
                break;
        }

        return nodes;
    }

    private static async Task<NodePage> PageAsync(AppDbContext db, long? userId, IQueryable<Node> nodes, NodeQuery q, bool foldersFirst, CancellationToken ct)
    {
        var total = await nodes.CountAsync(ct);
        IOrderedQueryable<Node> ordered = foldersFirst ? nodes.OrderBy(n => n.Kind) : nodes.OrderBy(n => 0);
        ordered = (q.Sort, q.Descending) switch
        {
            ("trashed", _) => ordered.ThenByDescending(n => n.TrashedDate),
            ("name", false) => ordered.ThenBy(n => n.Name.ToLower()),
            ("name", true) => ordered.ThenByDescending(n => n.Name.ToLower()),
            ("size", false) => ordered.ThenBy(n => n.Size),
            ("size", true) => ordered.ThenByDescending(n => n.Size),
            ("modified", false) => ordered.ThenBy(n => n.ModifiedDate),
            ("modified", true) => ordered.ThenByDescending(n => n.ModifiedDate),
            _ => ordered.ThenBy(n => n.Name.ToLower()),
        };
        var items = await Project(db, userId, ordered.ThenBy(n => n.Id).Skip(Math.Max(0, q.Page - 1) * q.PageSize).Take(q.PageSize)).ToListAsync(ct);
        return new NodePage(items, total);
    }

    private static IQueryable<NodeRow> Project(AppDbContext db, long? userId, IQueryable<Node> nodes)
        => nodes.Select(n => new NodeRow(
            n.Id, n.DriveId, n.ParentId, n.Kind, n.Name, n.Size, n.MimeType, n.ModifiedDate,
            db.Users.IgnoreQueryFilters().Where(u => u.Id == n.ModifiedById).Select(u => u.DisplayName).FirstOrDefault(),
            db.Users.IgnoreQueryFilters().Where(u => u.Id == n.OwnerId).Select(u => u.DisplayName).FirstOrDefault() ?? string.Empty,
            userId != null && db.NodeStars.Any(s => s.UserId == userId && s.NodeId == n.Id),
            n.Shares.Any() || db.ShareLinks.Any(l => l.NodeId == n.Id),
            n.Colour,
            n.TrashedDate,
            n.Parent != null ? n.Parent.Name : n.Drive!.Kind == DriveKind.Shared ? n.Drive.Name : null));

    private static string Pattern(string text) => "%" + text.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

    // ---- the path ---------------------------------------------------------------------------------------------------------------

    /// <summary>The folders above a node that the person may open (a shared folder starts the path where the access starts).</summary>
    public static async Task<IReadOnlyList<Crumb>> PathAsync(AppDbContext db, Actor actor, Node node, CancellationToken ct = default)
    {
        var ids = DriveAccess.IdsOf(node.Path);
        var nodes = await db.Nodes.AsNoTracking().Where(n => ids.Contains(n.Id)).Select(n => new { n.Id, n.Name, n.Path, n.DriveId }).ToListAsync(ct);
        var drive = await db.Drives.AsNoTracking().FirstAsync(d => d.Id == node.DriveId, ct);
        var driveRole = await DriveAccess.DriveRoleAsync(db, actor.UserId, drive, ct);
        var crumbs = new List<Crumb>();
        var reachable = driveRole > AccessRole.None;
        foreach (var id in ids)
        {
            var n = nodes.FirstOrDefault(x => x.Id == id);
            if (n is null)
            {
                break;
            }

            if (!reachable)
            {
                // without the drive, the path starts at the first item shared with the person (or the link)
                reachable = actor.LinkNodeId == id || (actor.UserId is { } uid && await db.NodeShares.AnyAsync(s => s.NodeId == id && s.UserId == uid, ct));
            }

            if (reachable)
            {
                crumbs.Add(new Crumb(n.Id, n.Name));
            }
        }

        return crumbs;
    }

    // ---- create, rename ---------------------------------------------------------------------------------------------------------

    /// <summary>A new folder in a folder (or at the top of a drive).</summary>
    public static async Task<Outcome<Node>> CreateFolderAsync(AppDbContext db, Actor actor, long driveId, long? parentId, string name, CancellationToken ct = default)
    {
        var target = await TargetAsync(db, actor, driveId, parentId, ct);
        if (target is null)
        {
            return Outcome<Node>.Denied();
        }

        var clean = CleanName(name);
        if (clean is null)
        {
            return Outcome<Node>.Fail(Text.T("Files.NameInvalid"));
        }

        using var nameLock = await LockNameAsync(target.Value.DriveId, parentId, clean, ct);
        if (await ExistsAsync(db, target.Value.DriveId, parentId, clean, null, ct))
        {
            return Outcome<Node>.Fail(Text.T("Files.NameTaken", clean));
        }

        var node = await AddNodeAsync(db, actor.UserId, target.Value.DriveId, target.Value.Parent, NodeKind.Folder, clean, ct);
        await ActivityLog.WriteAsync(db, actor.UserId, "create", node, ct: ct);
        return Outcome<Node>.Success(node);
    }

    /// <summary>Where something new goes: the drive and the parent folder, if the actor may add there (editor).</summary>
    public static async Task<(long DriveId, Node? Parent)?> TargetAsync(AppDbContext db, Actor actor, long driveId, long? parentId, CancellationToken ct = default)
    {
        if (parentId is { } pid)
        {
            var access = await DriveAccess.FindAsync(db, actor, pid, AccessRole.Editor, ct: ct);
            return access is null || access.Node.Kind != NodeKind.Folder ? null : (access.Node.DriveId, access.Node);
        }

        return actor.UserId is { } userId && await DriveAccess.FindDriveAsync(db, userId, driveId, AccessRole.Editor, ct) is not null ? (driveId, null) : null;
    }

    /// <summary>Adds a node and gives it its path (two writes: the id is known after the first).</summary>
    internal static async Task<Node> AddNodeAsync(AppDbContext db, long? userId, long driveId, Node? parent, NodeKind kind, string name, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var node = new Node
        {
            DriveId = driveId,
            ParentId = parent?.Id,
            Kind = kind,
            Name = name,
            OwnerId = userId ?? parent?.OwnerId ?? 0,
            ModifiedDate = now,
            ModifiedById = userId,
            Path = "/",
        };
        db.Nodes.Add(node);
        await db.SaveChangesAsync(ct);
        node.Path = (parent?.Path ?? "/") + node.Id + "/";
        await db.SaveChangesAsync(ct);
        if (parent is not null)
        {
            await TouchFolderAsync(db, parent.Id, userId, ct);
        }

        return node;
    }

    /// <summary>A folder changed (something was added, removed, renamed in it).</summary>
    internal static Task TouchFolderAsync(AppDbContext db, long folderId, long? userId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        return db.Nodes.Where(n => n.Id == folderId).ExecuteUpdateAsync(s => s.SetProperty(n => n.ModifiedDate, now).SetProperty(n => n.ModifiedById, userId), ct);
    }

    public static async Task<Outcome<Node>> RenameAsync(AppDbContext db, Actor actor, long nodeId, string name, CancellationToken ct = default)
    {
        var access = await DriveAccess.FindAsync(db, actor, nodeId, AccessRole.Editor, track: true, ct: ct);
        if (access is null)
        {
            return Outcome<Node>.Denied();
        }

        var clean = CleanName(name);
        if (clean is null)
        {
            return Outcome<Node>.Fail(Text.T("Files.NameInvalid"));
        }

        var node = access.Node;
        if (clean == node.Name)
        {
            return Outcome<Node>.Success(node);
        }

        if (await ExistsAsync(db, node.DriveId, node.ParentId, clean, node.Id, ct))
        {
            return Outcome<Node>.Fail(Text.T("Files.NameTaken", clean));
        }

        var before = node.Name;
        node.Name = clean;
        if (node.Kind == NodeKind.File)
        {
            node.MimeType = FileTypes.MimeOf(clean);
        }

        ActivityLog.Add(db, actor.UserId, "rename", node, before + " → " + clean);
        await db.SaveChangesAsync(ct);
        return Outcome<Node>.Success(node);
    }

    /// <summary>The description and the colour of a folder.</summary>
    public static async Task<Outcome<Node>> UpdateDetailsAsync(AppDbContext db, Actor actor, long nodeId, string? description, string? colour, CancellationToken ct = default)
    {
        var access = await DriveAccess.FindAsync(db, actor, nodeId, AccessRole.Editor, track: true, ct: ct);
        if (access is null)
        {
            return Outcome<Node>.Denied();
        }

        var d = description?.Trim();
        access.Node.Description = string.IsNullOrEmpty(d) ? null : d.Length > 2000 ? d[..2000] : d;
        access.Node.Colour = access.Node.Kind == NodeKind.Folder && Ui.IsColour(colour) ? colour : null;
        await db.SaveChangesAsync(ct);
        return Outcome<Node>.Success(access.Node);
    }

    // ---- move, copy -------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Moves items into a folder (or to the top of a drive); a name that is taken there gets a number. A folder never goes into itself. Moving into another drive
    /// moves the used space with it and needs the right to delete at the source.
    /// </summary>
    public static async Task<Outcome<int>> MoveAsync(AppDbContext db, Actor actor, IReadOnlyList<long> ids, long driveId, long? targetId, CancellationToken ct = default)
    {
        var target = await TargetAsync(db, actor, driveId, targetId, ct);
        if (target is null)
        {
            return Outcome<int>.Denied();
        }

        var (toDrive, toFolder) = target.Value;
        var moved = 0;
        foreach (var id in ids.Distinct())
        {
            var access = await DriveAccess.FindAsync(db, actor, id, AccessRole.Editor, track: true, ct: ct);
            if (access is null)
            {
                continue;
            }

            var node = access.Node;
            if (toFolder is not null && (toFolder.Id == node.Id || toFolder.Path.StartsWith(node.Path, StringComparison.Ordinal)))
            {
                return Outcome<int>.Fail(Text.T("Files.MoveIntoItself", node.Name));
            }

            if (node.ParentId == toFolder?.Id && node.DriveId == toDrive)
            {
                continue;
            }

            var oldPath = node.Path;
            var oldDrive = node.DriveId;
            var oldParent = node.ParentId;
            node.Name = await FreeNameAsync(db, toDrive, toFolder?.Id, node.Name, node.Id, ct);
            node.ParentId = toFolder?.Id;
            node.DriveId = toDrive;
            var newPath = (toFolder?.Path ?? "/") + node.Id + "/";
            node.Path = newPath;
            ActivityLog.Add(db, actor.UserId, "move", node, toFolder?.Name);
            await db.SaveChangesAsync(ct);

            // everything below goes with it
            await db.Nodes.IgnoreQueryFilters().Where(n => n.Id != node.Id && n.Path.StartsWith(oldPath))
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.Path, n => newPath + n.Path.Substring(oldPath.Length)).SetProperty(n => n.DriveId, toDrive), ct);
            if (oldDrive != toDrive)
            {
                var bytes = await SubtreeBytesAsync(db, newPath, ct);
                await DriveService.AddUsedAsync(db, oldDrive, -bytes, ct);
                await DriveService.AddUsedAsync(db, toDrive, bytes, ct);
            }

            if (oldParent is { } p)
            {
                await TouchFolderAsync(db, p, actor.UserId, ct);
            }

            if (toFolder is not null)
            {
                await TouchFolderAsync(db, toFolder.Id, actor.UserId, ct);
            }

            moved++;
        }

        return Outcome<int>.Success(moved);
    }

    /// <summary>Copies items (folders with everything in them, files with their current version) into a folder; the bytes are copied too.</summary>
    public static async Task<Outcome<int>> CopyAsync(AppDbContext db, BlobStore store, Actor actor, IReadOnlyList<long> ids, long driveId, long? targetId, CancellationToken ct = default)
    {
        var target = await TargetAsync(db, actor, driveId, targetId, ct);
        if (target is null)
        {
            return Outcome<int>.Denied();
        }

        var (toDrive, toFolder) = target.Value;
        var copied = 0;
        foreach (var id in ids.Distinct())
        {
            var access = await DriveAccess.FindAsync(db, actor, id, ct: ct);
            if (access is null)
            {
                continue;
            }

            if (toFolder is not null && toFolder.Path.StartsWith(access.Node.Path, StringComparison.Ordinal))
            {
                return Outcome<int>.Fail(Text.T("Files.CopyIntoItself", access.Node.Name));
            }

            var bytes = await SubtreeBytesAsync(db, access.Node.Path, ct, currentOnly: true);
            if (!await FileService.HasRoomAsync(db, toDrive, bytes, ct))
            {
                return Outcome<int>.Fail(Text.T("Files.QuotaFull"));
            }

            await CopyNodeAsync(db, store, actor.UserId, access.Node, toDrive, toFolder, firstLevel: true, ct);
            copied++;
        }

        return Outcome<int>.Success(copied);
    }

    /// <summary>
    /// Copies one item (a folder with everything in it) into a folder or to the top of a drive under a name the caller chose (WebDAV COPY); answers the copy.
    /// The name must be free there (it is not numbered).
    /// </summary>
    public static async Task<Outcome<Node>> CopyToAsync(AppDbContext db, BlobStore store, Actor actor, long id, long driveId, long? targetId, string name, CancellationToken ct = default)
    {
        var target = await TargetAsync(db, actor, driveId, targetId, ct);
        var access = target is null ? null : await DriveAccess.FindAsync(db, actor, id, ct: ct);
        if (target is null || access is null)
        {
            return Outcome<Node>.Denied();
        }

        var clean = CleanName(name);
        if (clean is null)
        {
            return Outcome<Node>.Fail(Text.T("Files.NameInvalid"));
        }

        var (toDrive, toFolder) = target.Value;
        if (toFolder is not null && toFolder.Path.StartsWith(access.Node.Path, StringComparison.Ordinal))
        {
            return Outcome<Node>.Fail(Text.T("Files.CopyIntoItself", access.Node.Name));
        }

        if (await ExistsAsync(db, toDrive, toFolder?.Id, clean, null, ct))
        {
            return Outcome<Node>.Fail(Text.T("Files.NameTaken", clean));
        }

        var bytes = await SubtreeBytesAsync(db, access.Node.Path, ct, currentOnly: true);
        if (!await FileService.HasRoomAsync(db, toDrive, bytes, ct))
        {
            return Outcome<Node>.Fail(Text.T("Files.QuotaFull"));
        }

        return Outcome<Node>.Success(await CopyNodeAsync(db, store, actor.UserId, access.Node, toDrive, toFolder, firstLevel: true, ct, clean));
    }

    private static async Task<Node> CopyNodeAsync(AppDbContext db, BlobStore store, long? userId, Node source, long toDrive, Node? toFolder, bool firstLevel, CancellationToken ct, string? name = null)
    {
        name ??= await FreeNameAsync(db, toDrive, toFolder?.Id, firstLevel && toFolder?.Id == source.ParentId && toDrive == source.DriveId ? CopyName(source.Name, source.Kind) : source.Name, null, ct);
        var copy = await AddNodeAsync(db, userId, toDrive, toFolder, source.Kind, name, ct);
        copy.Description = source.Description;
        copy.Colour = source.Colour;
        if (source.Kind == NodeKind.File && source.CurrentVersionId is { } versionId)
        {
            var version = await db.NodeVersions.AsNoTracking().FirstAsync(v => v.Id == versionId, ct);
            var blob = await store.CopyAsync(version.BlobId, ct);
            var v = new NodeVersion { NodeId = copy.Id, Number = 1, BlobId = blob.BlobId, Size = blob.Size, Sha256 = blob.Sha256, MimeType = version.MimeType, Source = "copy" };
            db.NodeVersions.Add(v);
            await db.SaveChangesAsync(ct);
            copy.CurrentVersionId = v.Id;
            copy.Size = blob.Size;
            copy.MimeType = source.MimeType;
            copy.TextContent = source.TextContent;
            await DriveService.AddUsedAsync(db, toDrive, blob.Size, ct);
        }

        if (firstLevel)
        {
            ActivityLog.Add(db, userId, "copy", copy, source.Name);
        }

        await db.SaveChangesAsync(ct);
        if (source.Kind == NodeKind.Folder)
        {
            var children = await db.Nodes.AsNoTracking().Where(n => n.ParentId == source.Id && !n.InTrash).ToListAsync(ct);
            foreach (var child in children)
            {
                await CopyNodeAsync(db, store, userId, child, toDrive, copy, firstLevel: false, ct);
            }
        }

        return copy;
    }

    // ---- trash ------------------------------------------------------------------------------------------------------------------

    /// <summary>Puts items into the trash (with everything below them); they stay there for the days of the system settings.</summary>
    public static async Task<int> TrashAsync(AppDbContext db, Actor actor, IReadOnlyList<long> ids, CancellationToken ct = default)
    {
        var count = 0;
        foreach (var id in ids.Distinct())
        {
            var access = await DriveAccess.FindAsync(db, actor, id, AccessRole.Editor, track: true, ct: ct);
            if (access is null)
            {
                continue;
            }

            var node = access.Node;
            node.TrashedDate = DateTime.UtcNow;
            node.TrashedById = actor.UserId;
            node.InTrash = true;
            ActivityLog.Add(db, actor.UserId, "trash", node);
            await db.SaveChangesAsync(ct);
            var prefix = node.Path;
            await db.Nodes.Where(n => n.Path.StartsWith(prefix)).ExecuteUpdateAsync(s => s.SetProperty(n => n.InTrash, true), ct);
            if (node.ParentId is { } p)
            {
                await TouchFolderAsync(db, p, actor.UserId, ct);
            }

            count++;
        }

        return count;
    }

    /// <summary>Takes items out of the trash; an item whose folder is gone (or still in the trash) comes back at the top of its drive.</summary>
    public static async Task<int> RestoreAsync(AppDbContext db, Actor actor, IReadOnlyList<long> ids, CancellationToken ct = default)
    {
        var count = 0;
        foreach (var id in ids.Distinct())
        {
            var access = await DriveAccess.FindAsync(db, actor, id, AccessRole.Editor, track: true, trashed: true, ct: ct);
            if (access?.Node.TrashedDate is null)
            {
                continue;
            }

            var node = access.Node;
            var parent = node.ParentId is { } pid ? await db.Nodes.AsNoTracking().FirstOrDefaultAsync(n => n.Id == pid, ct) : null;
            if (node.ParentId is not null && (parent is null || parent.InTrash))
            {
                // the folder is gone: back to the top of the drive
                var oldPath = node.Path;
                var newPath = "/" + node.Id + "/";
                node.ParentId = null;
                node.Path = newPath;
                await db.SaveChangesAsync(ct);
                await db.Nodes.Where(n => n.Id != node.Id && n.Path.StartsWith(oldPath)).ExecuteUpdateAsync(s => s.SetProperty(n => n.Path, n => newPath + n.Path.Substring(oldPath.Length)), ct);
            }

            node.Name = await FreeNameAsync(db, node.DriveId, node.ParentId, node.Name, node.Id, ct);
            node.TrashedDate = null;
            node.TrashedById = null;
            node.InTrash = false;
            ActivityLog.Add(db, actor.UserId, "restore", node);
            await db.SaveChangesAsync(ct);

            // everything below comes back, except what was put into the trash on its own
            var below = await db.Nodes.Where(n => n.Path.StartsWith(node.Path) && n.Id != node.Id).Select(n => new { n.Id, n.Path, n.TrashedDate }).ToListAsync(ct);
            var stillTrashed = below.Where(n => n.TrashedDate != null).Select(n => n.Path).ToList();
            var back = below.Where(n => n.TrashedDate == null && !stillTrashed.Any(p => n.Path.StartsWith(p, StringComparison.Ordinal))).Select(n => n.Id).ToList();
            if (back.Count > 0)
            {
                await db.Nodes.Where(n => back.Contains(n.Id)).ExecuteUpdateAsync(s => s.SetProperty(n => n.InTrash, false), ct);
            }

            count++;
        }

        return count;
    }

    /// <summary>Deletes items of the trash for good; answers the blobs to remove from the store.</summary>
    public static async Task<IReadOnlyList<Guid>> DeleteForeverAsync(AppDbContext db, Actor actor, IReadOnlyList<long> ids, CancellationToken ct = default)
    {
        var blobs = new List<Guid>();
        foreach (var id in ids.Distinct())
        {
            var access = await DriveAccess.FindAsync(db, actor, id, AccessRole.Editor, trashed: true, ct: ct);
            if (access?.Node.TrashedDate is null)
            {
                continue;
            }

            blobs.AddRange(await RemoveSubtreeAsync(db, access.Node, ct));
            await ActivityLog.WriteAsync(db, actor.UserId, "delete", null, driveId: access.Node.DriveId, target: access.Node.Name, ct: ct);
        }

        return blobs;
    }

    /// <summary>Empties the trash of the person (their drive and the shared drives where they may delete).</summary>
    public static async Task<IReadOnlyList<Guid>> EmptyTrashAsync(AppDbContext db, long userId, CancellationToken ct = default)
    {
        var page = await TrashAsync(db, userId, new NodeQuery(null, null, "trashed", true, 1, 100000), ct);
        return await DeleteForeverAsync(db, Actor.Of(userId), page.Items.Select(i => i.Id).ToList(), ct);
    }

    /// <summary>Removes a node and everything below it from the database (the housekeeping and "delete forever"); answers the blobs.</summary>
    internal static async Task<IReadOnlyList<Guid>> RemoveSubtreeAsync(AppDbContext db, Node node, CancellationToken ct)
    {
        var prefix = node.Path;
        var ids = db.Nodes.IgnoreQueryFilters().Where(n => n.Path.StartsWith(prefix)).Select(n => n.Id);
        var versions = await db.NodeVersions.Where(v => ids.Contains(v.NodeId)).Select(v => new { v.BlobId, v.Size }).ToListAsync(ct);
        await db.Activities.Where(a => a.NodeId != null && ids.Contains(a.NodeId.Value)).ExecuteUpdateAsync(s => s.SetProperty(a => a.NodeId, (long?)null), ct);
        await db.Nodes.IgnoreQueryFilters().Where(n => n.Path.StartsWith(prefix)).ExecuteDeleteAsync(ct);
        await DriveService.AddUsedAsync(db, node.DriveId, -versions.Sum(v => v.Size), ct);
        return versions.Select(v => v.BlobId).ToList();
    }

    // ---- stars and visits -------------------------------------------------------------------------------------------------------

    public static async Task<bool> StarAsync(AppDbContext db, long userId, long nodeId, bool starred, CancellationToken ct = default)
    {
        if (await DriveAccess.FindAsync(db, Actor.Of(userId), nodeId, ct: ct) is null)
        {
            return false;
        }

        if (starred)
        {
            if (!await db.NodeStars.AnyAsync(s => s.UserId == userId && s.NodeId == nodeId, ct))
            {
                db.NodeStars.Add(new NodeStar { UserId = userId, NodeId = nodeId });
                await db.SaveChangesAsync(ct);
            }
        }
        else
        {
            await db.NodeStars.Where(s => s.UserId == userId && s.NodeId == nodeId).ExecuteDeleteAsync(ct);
        }

        return true;
    }

    /// <summary>Notes that the person opened a file now (the list "Recent").</summary>
    public static async Task VisitAsync(AppDbContext db, long userId, long nodeId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        if (await db.NodeVisits.Where(v => v.UserId == userId && v.NodeId == nodeId).ExecuteUpdateAsync(s => s.SetProperty(v => v.VisitDate, now), ct) == 0)
        {
            db.NodeVisits.Add(new NodeVisit { UserId = userId, NodeId = nodeId, VisitDate = now });
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // two tabs at the same moment
            }
        }
    }

    // ---- names ------------------------------------------------------------------------------------------------------------------

    /// <summary>A name that is allowed on the file systems people use (no / \ : * ? " &lt; &gt; |, no control characters, not . or .., no space at the end); null when nothing is left.</summary>
    public static string? CleanName(string? name)
    {
        var text = new string((name ?? string.Empty).Where(c => !char.IsControl(c) && c is not ('/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|')).ToArray()).Trim().TrimEnd('.');
        if (text.Length == 0 || text is "." or "..")
        {
            return null;
        }

        return text.Length > NameMax ? text[..NameMax] : text;
    }

    public static Task<bool> ExistsAsync(AppDbContext db, long driveId, long? parentId, string name, long? except, CancellationToken ct = default)
    {
        var lower = name.ToLowerInvariant();
        return db.Nodes.AnyAsync(n => n.DriveId == driveId && n.ParentId == parentId && !n.InTrash && n.Name.ToLower() == lower && n.Id != except, ct);
    }

    // Two requests that make the same name in the same folder at the same moment (the parallel workers of a folder upload, two people) would both
    // find it free and both insert it: the look-up of a name and the insert of its node run one at a time. MatDrive runs as one instance;
    // a stripe of 64 locks needs no clean-up (two names may share a lock, which only makes one of them wait a moment).
    private static readonly SemaphoreSlim[] NameLocks = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    /// <summary>Holds a name in a folder until the result is disposed: wrap the look-up of the name AND the insert of the node in it.</summary>
    internal static async Task<NameLock> LockNameAsync(long driveId, long? parentId, string name, CancellationToken ct = default)
    {
        var gate = NameLocks[(int)((uint)HashCode.Combine(driveId, parentId ?? 0, name.ToLowerInvariant()) % (uint)NameLocks.Length)];
        await gate.WaitAsync(ct);
        return new NameLock(gate);
    }

    /// <summary>The node of a name in a folder (not in the trash), or null.</summary>
    public static Task<Node?> FindByNameAsync(AppDbContext db, long driveId, long? parentId, string name, bool track = false, CancellationToken ct = default)
    {
        var lower = name.ToLowerInvariant();
        var query = track ? db.Nodes : db.Nodes.AsNoTracking();
        return query.FirstOrDefaultAsync(n => n.DriveId == driveId && n.ParentId == parentId && !n.InTrash && n.Name.ToLower() == lower, ct);
    }

    /// <summary>The name itself when it is free in the folder, else "name (2).ext", "name (3).ext" …</summary>
    public static async Task<string> FreeNameAsync(AppDbContext db, long driveId, long? parentId, string name, long? except, CancellationToken ct = default)
    {
        if (!await ExistsAsync(db, driveId, parentId, name, except, ct))
        {
            return name;
        }

        var ext = Path.GetExtension(name);
        var stem = ext.Length > 0 && ext.Length < name.Length ? name[..^ext.Length] : name;
        if (ext == name)
        {
            ext = string.Empty;
        }

        for (var i = 2; i < 10000; i++)
        {
            var candidate = $"{stem} ({i}){ext}";
            if (!await ExistsAsync(db, driveId, parentId, candidate, except, ct))
            {
                return candidate;
            }
        }

        return $"{stem} ({Guid.NewGuid().ToString("N")[..6]}){ext}";
    }

    private static string CopyName(string name, NodeKind kind)
    {
        var ext = kind == NodeKind.File ? Path.GetExtension(name) : string.Empty;
        var stem = ext.Length > 0 ? name[..^ext.Length] : name;
        return Text.T("Files.CopyOf", stem) + ext;
    }

    /// <summary>The bytes below a path (every version, or only the current ones).</summary>
    internal static async Task<long> SubtreeBytesAsync(AppDbContext db, string path, CancellationToken ct, bool currentOnly = false)
    {
        var nodes = db.Nodes.IgnoreQueryFilters().Where(n => n.Path.StartsWith(path));
        if (currentOnly)
        {
            return await nodes.Where(n => !n.InTrash).SumAsync(n => n.Size, ct);
        }

        var ids = nodes.Select(n => n.Id);
        return await db.NodeVersions.Where(v => ids.Contains(v.NodeId)).SumAsync(v => v.Size, ct);
    }
}

/// <summary>A name held in a folder (see <see cref="NodeService.LockNameAsync"/>); disposing it lets the next request in.</summary>
internal readonly struct NameLock(SemaphoreSlim gate) : IDisposable
{
    public void Dispose() => gate.Release();
}
