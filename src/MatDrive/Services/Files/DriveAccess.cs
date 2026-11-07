using MatDrive.Data;
using Microsoft.EntityFrameworkCore;

namespace MatDrive.Services.Files;

/// <summary>Who asks: a signed-in person, somebody who opened a shared link (the node the link points to), or both.</summary>
public readonly record struct Actor(long? UserId, long? LinkNodeId = null, AccessRole LinkRole = AccessRole.None)
{
    public static Actor Of(long userId) => new(userId);

    public static Actor ViaLink(long? userId, long nodeId, AccessRole role) => new(userId, nodeId, role);
}

/// <summary>A node together with what the asking person may do with it.</summary>
public sealed record NodeAccess(Node Node, AccessRole Role, Drive Drive)
{
    public bool CanEdit => Role >= AccessRole.Editor;

    public bool CanShare => Role >= AccessRole.Manager;
}

/// <summary>
/// The one place that decides who may see and change what. A personal drive belongs to its person (role Owner on everything in it); a shared drive gives its
/// members their role on everything in it; a share gives a person a role on an item and everything below it; a link does the same for anybody who has it.
/// The best of these counts. Something a person may not see answers <c>null</c>, exactly like something that does not exist. The administrator of the
/// installation has no access to the files of others.
/// </summary>
public static class DriveAccess
{
    /// <summary>The ids of a path (<c>/12/40/41/</c> → 12, 40, 41).</summary>
    public static long[] IdsOf(string path)
        => path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(long.Parse).ToArray();

    /// <summary>The role of a person on a drive (Owner of their personal drive, the member role of a shared drive, else None).</summary>
    public static async Task<AccessRole> DriveRoleAsync(AppDbContext db, long? userId, Drive drive, CancellationToken ct = default)
    {
        if (userId is not { } id)
        {
            return AccessRole.None;
        }

        if (drive.Kind == DriveKind.Personal)
        {
            return drive.OwnerId == id ? AccessRole.Owner : AccessRole.None;
        }

        return await db.DriveMembers.AsNoTracking().Where(m => m.DriveId == drive.Id && m.UserId == id).Select(m => (AccessRole?)m.Role).FirstOrDefaultAsync(ct) ?? AccessRole.None;
    }

    /// <summary>The role of the actor on a node: the drive, the shares on the node or a folder above it, the link – whichever is best.</summary>
    public static async Task<AccessRole> RoleAsync(AppDbContext db, Actor actor, Node node, Drive drive, CancellationToken ct = default)
    {
        var role = await DriveRoleAsync(db, actor.UserId, drive, ct);
        if (role >= AccessRole.Owner)
        {
            return role;
        }

        var chain = IdsOf(node.Path);
        if (actor.UserId is { } userId)
        {
            var shared = await db.NodeShares.AsNoTracking().Where(s => s.UserId == userId && chain.Contains(s.NodeId)).Select(s => (AccessRole?)s.Role).MaxAsync(ct);
            if (shared is { } s && s > role)
            {
                role = s;
            }
        }

        if (actor.LinkNodeId is { } linkNode && chain.Contains(linkNode) && actor.LinkRole > role)
        {
            role = actor.LinkRole;
        }

        return role;
    }

    /// <summary>
    /// The node with the role of the actor, or <c>null</c> when it does not exist, lies in the trash (unless <paramref name="trashed"/>) or the role is below
    /// <paramref name="minimum"/>. Tracked when <paramref name="track"/> is set.
    /// </summary>
    public static async Task<NodeAccess?> FindAsync(AppDbContext db, Actor actor, long nodeId, AccessRole minimum = AccessRole.Viewer, bool track = false, bool trashed = false, CancellationToken ct = default)
    {
        var query = track ? db.Nodes : db.Nodes.AsNoTracking();
        var node = await query.FirstOrDefaultAsync(n => n.Id == nodeId, ct);
        if (node is null || (node.InTrash && !trashed))
        {
            return null;
        }

        var drive = await db.Drives.AsNoTracking().FirstOrDefaultAsync(d => d.Id == node.DriveId, ct);
        if (drive is null)
        {
            return null;
        }

        var role = await RoleAsync(db, actor, node, drive, ct);
        return role >= minimum && role > AccessRole.None ? new NodeAccess(node, role, drive) : null;
    }

    /// <summary>A drive with the role of the person on it, or <c>null</c>.</summary>
    public static async Task<(Drive Drive, AccessRole Role)?> FindDriveAsync(AppDbContext db, long userId, long driveId, AccessRole minimum = AccessRole.Viewer, CancellationToken ct = default)
    {
        var drive = await db.Drives.AsNoTracking().FirstOrDefaultAsync(d => d.Id == driveId, ct);
        if (drive is null)
        {
            return null;
        }

        var role = await DriveRoleAsync(db, userId, drive, ct);
        return role >= minimum && role > AccessRole.None ? (drive, role) : null;
    }

    /// <summary>The drives a person can open: their own and the shared drives they are a member of.</summary>
    public static IQueryable<Drive> DrivesOf(AppDbContext db, long userId)
        => db.Drives.Where(d => (d.Kind == DriveKind.Personal && d.OwnerId == userId) || (d.Kind == DriveKind.Shared && d.Members.Any(m => m.UserId == userId)));
}
