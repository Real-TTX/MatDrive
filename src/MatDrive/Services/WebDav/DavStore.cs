using System.Text;
using MatDrive.Data;
using MatDrive.Services.Files;
using Microsoft.EntityFrameworkCore;

namespace MatDrive.Services.WebDav;

/// <summary>What a place of the namespace is: the root, one of the folders above the drives, the top of a drive, or a file or folder.</summary>
public enum DavPlace
{
    /// <summary><c>/dav/</c>: My Drive, Shared drives, Shared with me.</summary>
    Root,

    /// <summary><c>/dav/Shared drives/</c>: a folder per shared drive of the person.</summary>
    SharedDrives,

    /// <summary><c>/dav/Shared with me/</c>: the top items shared with the person.</summary>
    SharedWithMe,

    /// <summary>The top of a drive (<c>My Drive</c>, <c>Shared drives/Team</c>).</summary>
    DriveRoot,

    /// <summary>A file or a folder.</summary>
    Node,
}

/// <summary>An item of the namespace with what its properties show.</summary>
public sealed record DavItem(
    string Name,
    bool IsCollection,
    long Size,
    string? MimeType,
    string? Sha256,
    DateTime Created,
    DateTime Modified,
    long? NodeId = null,
    string? NodePath = null,
    bool ReadOnly = false)
{
    /// <summary>The ids from the top of the drive to the node (empty for the places above the files).</summary>
    public long[] Chain => NodePath is null ? [] : DriveAccess.IdsOf(NodePath);
}

/// <summary>A place that exists, with the role of the person on it (<see cref="DriveAccess"/> decided it).</summary>
public sealed record DavTarget(DavPlace Place, IReadOnlyList<string> Segments, DavItem Item, Drive? Drive, Node? Node, AccessRole Role, bool ViaShare)
{
    public bool IsCollection => Item.IsCollection;

    /// <summary>The top of a drive or a folder: something new can be put into it (with the role Editor).</summary>
    public bool IsContainer => Place == DavPlace.DriveRoot || (Place == DavPlace.Node && Node!.Kind == NodeKind.Folder);

    public bool CanAdd => IsContainer && Role >= AccessRole.Editor;

    /// <summary>A file or folder the person may change, move and delete.</summary>
    public bool CanEdit => Place == DavPlace.Node && Role >= AccessRole.Editor;

    /// <summary>The folder new items go into (null: the top of the drive).</summary>
    public long? FolderId => Place == DavPlace.Node ? Node!.Id : null;

    public long[] Chain => Node is null ? [] : DriveAccess.IdsOf(Node.Path);
}

/// <summary>
/// The answer of resolving a path: the place (<see cref="Target"/>), or – when only the last name is free – the place it would go into (<see cref="Parent"/>,
/// <see cref="Name"/>). Both null: the path does not lead anywhere.
/// </summary>
public sealed record DavLookup(DavTarget? Target, DavTarget? Parent, string? Name)
{
    public static readonly DavLookup Missing = new(null, null, null);
}

/// <summary>
/// The namespace of WebDAV on top of the business layer: <c>/dav/My Drive/…</c> (the personal drive), <c>/dav/Shared drives/&lt;drive&gt;/…</c> (one folder per
/// shared drive of the person) and <c>/dav/Shared with me/&lt;item&gt;/…</c> (the top items shared with the person, with the normal tree inside). The names of these
/// places are English and stable, so paths never change with the language. Below them the names are the names of the nodes (unique per folder without regard to
/// case). Every place goes through <see cref="DriveAccess"/>; the trash is not shown.
/// </summary>
public static class DavStore
{
    public const string MyDrive = "My Drive";
    public const string SharedDrives = "Shared drives";
    public const string SharedWithMe = "Shared with me";

    /// <summary>Finds the place of a path (the segments below <c>/dav</c>).</summary>
    public static async Task<DavLookup> ResolveAsync(AppDbContext db, DavUser user, IReadOnlyList<string> segments, CancellationToken ct = default)
    {
        if (segments.Count == 0)
        {
            return new DavLookup(Virtual(DavPlace.Root, segments, string.Empty, user), null, null);
        }

        var first = segments[0];
        Step step;
        int used;
        if (Same(first, MyDrive))
        {
            var mine = await DriveService.MyDriveAsync(db, user.Id, ct);
            if (await DriveAccess.FindDriveAsync(db, user.Id, mine.Id, ct: ct) is not { } found)
            {
                return DavLookup.Missing;
            }

            step = new Step(DavPlace.DriveRoot, MyDrive, found.Drive, null, found.Role, false);
            used = 1;
        }
        else if (Same(first, SharedDrives))
        {
            var folder = Virtual(DavPlace.SharedDrives, segments.Take(1).ToList(), SharedDrives, user);
            if (segments.Count == 1)
            {
                return new DavLookup(folder, null, null);
            }

            var match = (await SharedDrivesOfAsync(db, user.Id, ct)).FirstOrDefault(d => Same(d.Name, segments[1]));
            if (match is null)
            {
                return segments.Count == 2 ? new DavLookup(null, folder, segments[1]) : DavLookup.Missing;
            }

            if (await DriveAccess.FindDriveAsync(db, user.Id, match.Id, ct: ct) is not { } found)
            {
                return DavLookup.Missing;
            }

            step = new Step(DavPlace.DriveRoot, match.Name, found.Drive, null, found.Role, false);
            used = 2;
        }
        else if (Same(first, SharedWithMe))
        {
            var folder = Virtual(DavPlace.SharedWithMe, segments.Take(1).ToList(), SharedWithMe, user);
            if (segments.Count == 1)
            {
                return new DavLookup(folder, null, null);
            }

            var names = await SharedTopNamesAsync(db, user.Id, ct);
            var match = names.FirstOrDefault(n => Same(n.Value, segments[1]));
            var access = match.Key == 0 ? null : await DriveAccess.FindAsync(db, Actor.Of(user.Id), match.Key, ct: ct);
            if (access is null)
            {
                return segments.Count == 2 ? new DavLookup(null, folder, segments[1]) : DavLookup.Missing;
            }

            step = new Step(DavPlace.Node, match.Value, access.Drive, access.Node, access.Role, true);
            used = 2;
        }
        else
        {
            // nothing can be made next to the three folders of the root
            return segments.Count == 1 ? new DavLookup(null, Virtual(DavPlace.Root, [], string.Empty, user), segments[0]) : DavLookup.Missing;
        }

        for (; used < segments.Count; used++)
        {
            if (!step.IsContainer)
            {
                return DavLookup.Missing;
            }

            var node = await FindChildAsync(db, step.Drive!.Id, step.Node?.Id, segments[used], ct);
            if (node is null)
            {
                if (used < segments.Count - 1)
                {
                    return DavLookup.Missing;
                }

                var parent = await TargetAsync(db, user, step, segments.Take(used).ToList(), ct);
                return parent is null ? DavLookup.Missing : new DavLookup(null, parent, segments[used]);
            }

            step = new Step(DavPlace.Node, node.Name, step.Drive, node, null, step.ViaShare);
        }

        var target = await TargetAsync(db, user, step, segments, ct);
        return target is null ? DavLookup.Missing : new DavLookup(target, null, null);
    }

    /// <summary>The items in a place (one query for the children of a folder, however many there are).</summary>
    public static async Task<IReadOnlyList<DavItem>> ChildrenAsync(AppDbContext db, DavUser user, DavTarget target, CancellationToken ct = default)
    {
        switch (target.Place)
        {
            case DavPlace.Root:
            {
                var mine = await DriveService.MyDriveAsync(db, user.Id, ct);
                var modified = await DrivesModifiedAsync(db, [mine.Id], ct);
                return
                [
                    DriveItem(MyDrive, mine, modified, AccessRole.Owner),
                    Virtual(DavPlace.SharedDrives, [], SharedDrives, user).Item,
                    Virtual(DavPlace.SharedWithMe, [], SharedWithMe, user).Item,
                ];
            }

            case DavPlace.SharedDrives:
            {
                var drives = await SharedDrivesOfAsync(db, user.Id, ct);
                var modified = await DrivesModifiedAsync(db, drives.Select(d => d.Id).ToList(), ct);
                return drives.Select(d => new DavItem(d.Name, true, 0, null, null, d.Created, Later(d.Created, modified.GetValueOrDefault(d.Id)), ReadOnly: d.Role < AccessRole.Editor)).ToList();
            }

            case DavPlace.SharedWithMe:
                return await SharedTopsAsync(db, user.Id, ct);

            case DavPlace.DriveRoot:
            case DavPlace.Node when target.IsContainer:
                return await FolderChildrenAsync(db, user.Id, target.Drive!.Id, target.FolderId, target.Role, ct);

            default:
                return [];
        }
    }

    /// <summary>The space of the drive of a place (the root: My Drive); null for what was shared by others (their space is theirs).</summary>
    public static async Task<DavQuota?> QuotaAsync(AppDbContext db, BlobStore store, DavUser user, DavTarget target, CancellationToken ct = default)
    {
        var drive = target.Place switch
        {
            DavPlace.Root => await DriveService.MyDriveAsync(db, user.Id, ct),
            DavPlace.DriveRoot or DavPlace.Node when !target.ViaShare => target.Drive,
            _ => null,
        };
        if (drive is null)
        {
            return null;
        }

        var usage = await DriveService.UsageAsync(db, drive, ct);
        var free = store.FreeSpace();
        long? available = usage.Quota is { } quota ? Math.Max(0, quota - usage.Used) : free >= 0 ? free : null;
        return new DavQuota(usage.Used, available);
    }

    /// <summary>The item of a node as the properties show it.</summary>
    public static async Task<DavItem> ItemAsync(AppDbContext db, Node node, string name, AccessRole role, CancellationToken ct = default)
    {
        var sha = node.CurrentVersionId is { } version
            ? await db.NodeVersions.AsNoTracking().Where(v => v.Id == version).Select(v => v.Sha256).FirstOrDefaultAsync(ct)
            : null;
        return new DavItem(name, node.Kind == NodeKind.Folder, node.Size, node.MimeType, sha, node.CreateDate, node.ModifiedDate, node.Id, node.Path, role < AccessRole.Editor);
    }

    /// <summary>Two names are the same in the namespace: in NFC, without regard to case.</summary>
    public static bool Same(string a, string b) => string.Equals(DavPath.Nfc(a), DavPath.Nfc(b), StringComparison.OrdinalIgnoreCase);

    // ---- the steps of a path ------------------------------------------------------------------------------------------------------

    /// <summary>One place on the way down a path; the role of a node is only looked up at the end.</summary>
    private sealed record Step(DavPlace Place, string Name, Drive? Drive, Node? Node, AccessRole? Role, bool ViaShare)
    {
        public bool IsContainer => Place == DavPlace.DriveRoot || (Place == DavPlace.Node && Node!.Kind == NodeKind.Folder);
    }

    private static async Task<DavTarget?> TargetAsync(AppDbContext db, DavUser user, Step step, IReadOnlyList<string> segments, CancellationToken ct)
    {
        if (step.Place == DavPlace.DriveRoot)
        {
            var modified = await DrivesModifiedAsync(db, [step.Drive!.Id], ct);
            var role = step.Role ?? AccessRole.None;
            return new DavTarget(DavPlace.DriveRoot, segments, DriveItem(step.Name, step.Drive, modified, role), step.Drive, null, role, step.ViaShare);
        }

        var node = step.Node!;
        var nodeRole = step.Role ?? await DriveAccess.RoleAsync(db, Actor.Of(user.Id), node, step.Drive!, ct);
        if (nodeRole <= AccessRole.None)
        {
            return null;
        }

        var item = await ItemAsync(db, node, step.Name, nodeRole, ct);
        return new DavTarget(DavPlace.Node, segments, item, step.Drive, node, nodeRole, step.ViaShare);
    }

    private static DavTarget Virtual(DavPlace place, IReadOnlyList<string> segments, string name, DavUser user)
        => new(place, segments, new DavItem(name, true, 0, null, null, user.Created, user.Created, ReadOnly: true), null, null, AccessRole.Viewer, false);

    private static DavItem DriveItem(string name, Drive drive, Dictionary<long, DateTime> modified, AccessRole role)
        => new(name, true, 0, null, null, drive.CreateDate, Later(drive.CreateDate, modified.GetValueOrDefault(drive.Id)), ReadOnly: role < AccessRole.Editor);

    private static DateTime Later(DateTime a, DateTime b) => a > b ? a : b;

    /// <summary>A child of a folder by name: in NFC first (how names are stored), then as sent and decomposed (a name stored by a Mac).</summary>
    private static async Task<Node?> FindChildAsync(AppDbContext db, long driveId, long? parentId, string segment, CancellationToken ct)
    {
        var nfc = DavPath.Nfc(segment);
        var node = await NodeService.FindByNameAsync(db, driveId, parentId, nfc, ct: ct);
        if (node is null && !segment.All(char.IsAscii))
        {
            var nfd = segment.Normalize(NormalizationForm.FormD);
            if (nfd != nfc)
            {
                node = await NodeService.FindByNameAsync(db, driveId, parentId, nfd, ct: ct);
            }
        }

        return node;
    }

    // ---- lists --------------------------------------------------------------------------------------------------------------------

    private sealed record Row(long Id, long DriveId, NodeKind Kind, string Name, long Size, string? MimeType, DateTime Created, DateTime Modified, string Path, string? Sha256)
    {
        public DavItem ToItem(string name, bool readOnly) => new(name, Kind == NodeKind.Folder, Size, MimeType, Sha256, Created, Modified, Id, Path, readOnly);
    }

    private static IQueryable<Row> Rows(AppDbContext db, IQueryable<Node> nodes)
        => nodes.OrderBy(n => n.Kind).ThenBy(n => n.Name)
            .Select(n => new Row(n.Id, n.DriveId, n.Kind, n.Name, n.Size, n.MimeType, n.CreateDate, n.ModifiedDate, n.Path,
                db.NodeVersions.Where(v => v.Id == n.CurrentVersionId).Select(v => v.Sha256).FirstOrDefault()));

    /// <summary>
    /// The children of a folder (or the top of a drive) in one query, with the hash of their current version. Below a role under Editor, a child is still
    /// writable when it was shared with the person as an editor (one more query).
    /// </summary>
    private static async Task<IReadOnlyList<DavItem>> FolderChildrenAsync(AppDbContext db, long userId, long driveId, long? folderId, AccessRole role, CancellationToken ct)
    {
        var nodes = db.Nodes.AsNoTracking().Where(n => n.DriveId == driveId && !n.InTrash);
        nodes = folderId is { } parent ? nodes.Where(n => n.ParentId == parent) : nodes.Where(n => n.ParentId == null);
        var rows = await Rows(db, nodes).ToListAsync(ct);

        HashSet<long>? writable = null;
        if (role < AccessRole.Editor && rows.Count > 0)
        {
            var shares = db.NodeShares.AsNoTracking().Where(s => s.UserId == userId && s.Role >= AccessRole.Editor && s.Node!.DriveId == driveId);
            shares = folderId is { } p ? shares.Where(s => s.Node!.ParentId == p) : shares.Where(s => s.Node!.ParentId == null);
            writable = (await shares.Select(s => s.NodeId).ToListAsync(ct)).ToHashSet();
        }

        return rows.Select(r => r.ToItem(r.Name, writable is not null && !writable.Contains(r.Id))).ToList();
    }

    /// <summary>The items other people shared with the person (the top items, like the list "Shared with me" of the explorer).</summary>
    private static IQueryable<Node> SharedTops(AppDbContext db, long userId)
    {
        var shared = db.NodeShares.Where(s => s.UserId == userId).Select(s => s.NodeId);
        return db.Nodes.AsNoTracking().Where(n => shared.Contains(n.Id) && !n.InTrash && n.OwnerId != userId);
    }

    private static async Task<Dictionary<long, string>> SharedTopNamesAsync(AppDbContext db, long userId, CancellationToken ct)
    {
        var tops = await SharedTops(db, userId).Select(n => new { n.Id, n.Name, n.Kind }).ToListAsync(ct);
        return DavNames.Unique(tops.Select(t => (t.Id, DavPath.Nfc(t.Name), t.Kind == NodeKind.Folder)));
    }

    private static async Task<IReadOnlyList<DavItem>> SharedTopsAsync(AppDbContext db, long userId, CancellationToken ct)
    {
        var rows = await Rows(db, SharedTops(db, userId)).ToListAsync(ct);
        if (rows.Count == 0)
        {
            return [];
        }

        var names = DavNames.Unique(rows.Select(r => (r.Id, DavPath.Nfc(r.Name), r.Kind == NodeKind.Folder)));
        var shares = await db.NodeShares.AsNoTracking().Where(s => s.UserId == userId).Select(s => new { s.NodeId, s.Role }).ToListAsync(ct);
        var members = await db.DriveMembers.AsNoTracking().Where(m => m.UserId == userId).Select(m => new { m.DriveId, m.Role }).ToListAsync(ct);
        return rows.Select(r =>
        {
            var role = shares.Where(s => s.NodeId == r.Id).Select(s => s.Role).DefaultIfEmpty(AccessRole.None).Max();
            var member = members.Where(m => m.DriveId == r.DriveId).Select(m => m.Role).DefaultIfEmpty(AccessRole.None).Max();
            return r.ToItem(names[r.Id], (role > member ? role : member) < AccessRole.Editor);
        }).ToList();
    }

    private sealed record SharedDrive(long Id, string Name, DateTime Created, AccessRole Role);

    /// <summary>The shared drives of the person with names that work as folders (unique, without the characters no file system takes).</summary>
    private static async Task<IReadOnlyList<SharedDrive>> SharedDrivesOfAsync(AppDbContext db, long userId, CancellationToken ct)
    {
        var rows = await DriveService.SharedDrivesAsync(db, userId, ct);
        var names = DavNames.Unique(rows.Select(r => (r.Id, DavNames.Clean(r.Name, "Drive " + r.Id), true)));
        return rows.Select(r => new SharedDrive(r.Id, names[r.Id], r.CreateDate, r.Role)).ToList();
    }

    /// <summary>When something at the top of each drive changed last (the date of the folder of a drive).</summary>
    private static async Task<Dictionary<long, DateTime>> DrivesModifiedAsync(AppDbContext db, IReadOnlyCollection<long> driveIds, CancellationToken ct)
    {
        if (driveIds.Count == 0)
        {
            return [];
        }

        var rows = await db.Nodes.AsNoTracking().Where(n => driveIds.Contains(n.DriveId) && n.ParentId == null && !n.InTrash)
            .GroupBy(n => n.DriveId).Select(g => new { g.Key, Modified = g.Max(n => n.ModifiedDate) }).ToListAsync(ct);
        return rows.ToDictionary(r => r.Key, r => r.Modified);
    }
}

/// <summary>Names of the places above the files: folders for shared drives and shared items, unique and allowed in every file system.</summary>
public static class DavNames
{
    /// <summary>A name that works as a path segment: the characters no file system takes become <c>_</c>; when nothing is left, <paramref name="fallback"/>.</summary>
    public static string Clean(string name, string fallback)
    {
        var chars = name.Select(c => char.IsControl(c) || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : c).ToArray();
        var text = DavPath.Nfc(new string(chars).Trim().TrimEnd('.').Trim());
        return text.Length == 0 ? fallback : text;
    }

    /// <summary>Unique names (without regard to case), given in the order of the ids: a second <c>Report.docx</c> becomes <c>Report (2).docx</c>.</summary>
    public static Dictionary<long, string> Unique(IEnumerable<(long Id, string Name, bool IsFolder)> items)
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<long, string>();
        foreach (var (id, name, folder) in items.OrderBy(i => i.Id))
        {
            var candidate = name;
            for (var n = 2; !taken.Add(candidate); n++)
            {
                candidate = Numbered(name, n, folder);
            }

            result[id] = candidate;
        }

        return result;
    }

    private static string Numbered(string name, int number, bool folder)
    {
        var ext = folder ? string.Empty : Path.GetExtension(name);
        if (ext.Length == name.Length)
        {
            ext = string.Empty;
        }

        return $"{name[..^ext.Length]} ({number}){ext}";
    }
}
