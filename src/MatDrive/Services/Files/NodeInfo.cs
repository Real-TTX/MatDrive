using MatDrive.Data;
using Microsoft.EntityFrameworkCore;

namespace MatDrive.Services.Files;

/// <summary>Small questions the pages ask about items and people (no right is needed: the page has the item already through <see cref="DriveAccess"/>).</summary>
public static class NodeInfo
{
    public static async Task<string?> OwnerNameAsync(AppDbContext db, long userId, CancellationToken ct = default)
        => await db.Users.IgnoreQueryFilters().Where(u => u.Id == userId).Select(u => u.DisplayName).FirstOrDefaultAsync(ct);

    /// <summary>The largest files of a drive (the page "Storage").</summary>
    public static async Task<IReadOnlyList<NodeRow>?> LargestAsync(AppDbContext db, long userId, long driveId, int count, CancellationToken ct = default)
    {
        if (await DriveAccess.FindDriveAsync(db, userId, driveId, ct: ct) is null)
        {
            return null;
        }

        return await db.Nodes.AsNoTracking().Where(n => n.DriveId == driveId && n.Kind == NodeKind.File && !n.InTrash).OrderByDescending(n => n.Size).Take(count)
            .Select(n => new NodeRow(n.Id, n.DriveId, n.ParentId, n.Kind, n.Name, n.Size, n.MimeType, n.ModifiedDate, null, string.Empty, false, false, null, n.TrashedDate,
                n.Parent != null ? n.Parent.Name : null))
            .ToListAsync(ct);
    }

    /// <summary>The bytes in the trash and in older versions of a drive.</summary>
    public static async Task<(long Trash, long OldVersions)> SpareBytesAsync(AppDbContext db, long driveId, CancellationToken ct = default)
    {
        var trash = await db.Nodes.Where(n => n.DriveId == driveId && n.InTrash && n.Kind == NodeKind.File).SumAsync(n => n.Size, ct);
        var nodes = db.Nodes.IgnoreQueryFilters().Where(n => n.DriveId == driveId);
        var old = await db.NodeVersions.Where(v => nodes.Any(n => n.Id == v.NodeId && n.CurrentVersionId != v.Id)).SumAsync(v => v.Size, ct);
        return (trash, old);
    }

    /// <summary>Suggestions for the search of the top bar: names that match, folders first (only what the person can see).</summary>
    public static async Task<IReadOnlyList<NodeRow>> SuggestAsync(AppDbContext db, long userId, string text, int count, CancellationToken ct = default)
        => (await NodeService.SearchAsync(db, userId, new NodeQuery(text, null, "name", false, 1, count), ct)).Items;
}
