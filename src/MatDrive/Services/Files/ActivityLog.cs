using MatDrive.Data;
using Microsoft.EntityFrameworkCore;

namespace MatDrive.Services.Files;

public sealed record ActivityRow(long Id, DateTime Date, string Action, string? Target, string? Detail, string? UserName, long? NodeId, long? DriveId);

/// <summary>What happened in the drives. Entries are added together with the change they describe (the same SaveChanges), or on their own.</summary>
public static class ActivityLog
{
    private static readonly AsyncLocal<string?> Source = new();

    /// <summary>
    /// Marks every entry written from here on in the current flow of work with where the changes come from (a WebDAV request: <c>webdav</c>): it becomes the
    /// detail of the entry, or follows it (<c>v3 · webdav</c>). The changes are still logged once, by the business layer. Dispose the answer to end it.
    /// </summary>
    public static IDisposable From(string source)
    {
        var before = Source.Value;
        Source.Value = source;
        return new SourceScope(before);
    }

    /// <summary>Adds an entry to the context (saved with the next SaveChanges).</summary>
    public static void Add(AppDbContext db, long? userId, string action, Node? node = null, string? detail = null, long? driveId = null, string? target = null)
        => db.Activities.Add(new Activity
        {
            UserId = userId,
            Action = action,
            NodeId = node?.Id is > 0 ? node.Id : null,
            DriveId = node?.DriveId ?? driveId,
            Target = Cut(target ?? node?.Name, 255),
            Detail = Cut(Source.Value is { } source ? (string.IsNullOrEmpty(detail) ? source : detail + " · " + source) : detail, 1000),
            OccurredDate = DateTime.UtcNow,
        });

    /// <summary>Writes an entry at once.</summary>
    public static async Task WriteAsync(AppDbContext db, long? userId, string action, Node? node = null, string? detail = null, long? driveId = null, string? target = null, CancellationToken ct = default)
    {
        Add(db, userId, action, node, detail, driveId, target);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The activity of one item (and, for a folder, of everything in it) that the person may see.</summary>
    public static async Task<IReadOnlyList<ActivityRow>?> OfNodeAsync(AppDbContext db, Actor actor, long nodeId, int count, CancellationToken ct = default)
    {
        var access = await DriveAccess.FindAsync(db, actor, nodeId, trashed: true, ct: ct);
        if (access is null)
        {
            return null;
        }

        var prefix = access.Node.Path;
        var ids = db.Nodes.IgnoreQueryFilters().Where(n => n.Path.StartsWith(prefix)).Select(n => n.Id);
        return await Rows(db, db.Activities.Where(a => a.NodeId != null && ids.Contains(a.NodeId.Value)), count, ct);
    }

    /// <summary>The activity of a drive (members of a shared drive, the owner of a personal one).</summary>
    public static async Task<IReadOnlyList<ActivityRow>?> OfDriveAsync(AppDbContext db, long userId, long driveId, int count, CancellationToken ct = default)
        => await DriveAccess.FindDriveAsync(db, userId, driveId, ct: ct) is null ? null : await Rows(db, db.Activities.Where(a => a.DriveId == driveId), count, ct);

    /// <summary>The whole log (administrators): who did what, never the content.</summary>
    public static async Task<(IReadOnlyList<ActivityRow> Rows, int Total)?> AllAsync(AppDbContext db, long adminId, string? search, string? action, int page, int pageSize, CancellationToken ct = default)
    {
        if (!await db.Users.AnyAsync(u => u.Id == adminId && u.Role == UserRole.Admin && u.IsActive, ct))
        {
            return null;
        }

        var query = db.Activities.AsQueryable();
        if (!string.IsNullOrWhiteSpace(action))
        {
            query = query.Where(a => a.Action == action);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = "%" + search.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            query = query.Where(a => EF.Functions.ILike(a.Target ?? string.Empty, pattern) || EF.Functions.ILike(a.Detail ?? string.Empty, pattern)
                || db.Users.IgnoreQueryFilters().Any(u => u.Id == a.UserId && EF.Functions.ILike(u.DisplayName, pattern)));
        }

        var total = await query.CountAsync(ct);
        var rows = await Rows(db, query, pageSize, ct, (page - 1) * pageSize);
        return (rows, total);
    }

    private static async Task<IReadOnlyList<ActivityRow>> Rows(AppDbContext db, IQueryable<Activity> query, int count, CancellationToken ct, int skip = 0)
        => await query.OrderByDescending(a => a.OccurredDate).ThenByDescending(a => a.Id).Skip(skip).Take(count)
            .Select(a => new ActivityRow(a.Id, a.OccurredDate, a.Action, a.Target, a.Detail,
                db.Users.IgnoreQueryFilters().Where(u => u.Id == a.UserId).Select(u => u.DisplayName).FirstOrDefault(), a.NodeId, a.DriveId))
            .ToListAsync(ct);

    private static string? Cut(string? text, int max) => text is null ? null : text.Length > max ? text[..max] : text;

    private sealed class SourceScope(string? before) : IDisposable
    {
        public void Dispose() => Source.Value = before;
    }
}
