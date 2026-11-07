using MatDrive.Data;
using MatDrive.Localization;
using Microsoft.EntityFrameworkCore;

namespace MatDrive.Services.Files;

public sealed record ShareRow(long UserId, string Name, string Email, AccessRole Role);

public sealed record LinkRow(long Id, Guid Token, AccessRole Role, bool HasPassword, DateTime? ExpiresDate, bool AllowDownload, int Visits, int Downloads, string? Note, DateTime CreateDate, string? CreatedBy);

/// <summary>What the edit form of a link sends.</summary>
public sealed record LinkInput(AccessRole Role, string? Password, bool ClearPassword, DateTime? ExpiresDate, bool AllowDownload, string? Note);

/// <summary>A link that was opened: the node it points to and what it allows.</summary>
public sealed record OpenedLink(ShareLink Link, Node Node, Drive Drive)
{
    public Actor ActorFor(long? userId) => Actor.ViaLink(userId, Node.Id, Link.Role);
}

/// <summary>Sharing: people get a role on an item (and everything below it), links let anybody who has them look (or, for a folder, upload). Managers and owners share.</summary>
public static class ShareService
{
    public static async Task<IReadOnlyList<ShareRow>?> SharesAsync(AppDbContext db, Actor actor, long nodeId, CancellationToken ct = default)
    {
        if (await DriveAccess.FindAsync(db, actor, nodeId, ct: ct) is null)
        {
            return null;
        }

        var rows = await db.NodeShares.AsNoTracking().Where(s => s.NodeId == nodeId).Select(s => new { s.UserId, s.User!.DisplayName, s.User.Email, s.Role }).ToListAsync(ct);
        return rows.OrderBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase).Select(r => new ShareRow(r.UserId, r.DisplayName, r.Email, r.Role)).ToList();
    }

    public static async Task<Outcome<ShareRow>> AddAsync(AppDbContext db, Actor actor, long nodeId, string email, AccessRole role, CancellationToken ct = default)
    {
        var access = await DriveAccess.FindAsync(db, actor, nodeId, AccessRole.Manager, ct: ct);
        if (access is null)
        {
            return Outcome<ShareRow>.Denied();
        }

        if (role is not (AccessRole.Viewer or AccessRole.Editor))
        {
            return Outcome<ShareRow>.Fail(Text.T("Members.RoleInvalid"));
        }

        var address = AccountService.Normalize(email);
        var person = await db.Users.AsNoTracking().Where(u => u.Email == address && u.IsActive).Select(u => new { u.Id, u.DisplayName, u.Email }).FirstOrDefaultAsync(ct);
        if (person is null)
        {
            return Outcome<ShareRow>.Fail(Text.T("Members.NoAccount"));
        }

        if (person.Id == actor.UserId || (access.Drive.Kind == DriveKind.Personal && access.Drive.OwnerId == person.Id))
        {
            return Outcome<ShareRow>.Fail(Text.T("Members.IsOwner"));
        }

        var existing = await db.NodeShares.FirstOrDefaultAsync(s => s.NodeId == nodeId && s.UserId == person.Id, ct);
        if (existing is not null)
        {
            return Outcome<ShareRow>.Fail(Text.T("Members.AlreadyMember"));
        }

        db.NodeShares.Add(new NodeShare { NodeId = nodeId, UserId = person.Id, Role = role });
        ActivityLog.Add(db, actor.UserId, "share", access.Node, person.DisplayName + " · " + role);
        await db.SaveChangesAsync(ct);
        return Outcome<ShareRow>.Success(new ShareRow(person.Id, person.DisplayName, person.Email, role));
    }

    public static async Task<bool> SetRoleAsync(AppDbContext db, Actor actor, long nodeId, long userId, AccessRole role, CancellationToken ct = default)
    {
        if (role is not (AccessRole.Viewer or AccessRole.Editor) || await DriveAccess.FindAsync(db, actor, nodeId, AccessRole.Manager, ct: ct) is null)
        {
            return false;
        }

        return await db.NodeShares.Where(s => s.NodeId == nodeId && s.UserId == userId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Role, role), ct) > 0;
    }

    /// <summary>Ends the sharing with a person (a manager), or a person removes an item shared with them from their list.</summary>
    public static async Task<bool> RemoveAsync(AppDbContext db, Actor actor, long nodeId, long userId, CancellationToken ct = default)
    {
        var self = actor.UserId == userId;
        var access = await DriveAccess.FindAsync(db, actor, nodeId, self ? AccessRole.Viewer : AccessRole.Manager, ct: ct);
        if (access is null)
        {
            return false;
        }

        var removed = await db.NodeShares.Where(s => s.NodeId == nodeId && s.UserId == userId).ExecuteDeleteAsync(ct) > 0;
        if (removed)
        {
            await ActivityLog.WriteAsync(db, actor.UserId, "unshare", access.Node, ct: ct);
        }

        return removed;
    }

    // ---- links ------------------------------------------------------------------------------------------------------------------

    public static async Task<IReadOnlyList<LinkRow>?> LinksAsync(AppDbContext db, Actor actor, long nodeId, CancellationToken ct = default)
    {
        if (await DriveAccess.FindAsync(db, actor, nodeId, AccessRole.Manager, ct: ct) is null)
        {
            return null;
        }

        return await db.ShareLinks.AsNoTracking().Where(l => l.NodeId == nodeId).OrderByDescending(l => l.CreateDate)
            .Select(l => new LinkRow(l.Id, l.Token, l.Role, l.PasswordHash != null, l.ExpiresDate, l.AllowDownload, l.Visits, l.Downloads, l.Note, l.CreateDate,
                db.Users.IgnoreQueryFilters().Where(u => u.Id == l.CreateUserId).Select(u => u.DisplayName).FirstOrDefault()))
            .ToListAsync(ct);
    }

    public static async Task<LinkRow?> FindLinkAsync(AppDbContext db, Actor actor, long nodeId, long linkId, CancellationToken ct = default)
        => (await LinksAsync(db, actor, nodeId, ct))?.FirstOrDefault(l => l.Id == linkId);

    /// <summary>Creates or changes a link of an item (managers).</summary>
    public static async Task<Outcome<ShareLink>> SaveLinkAsync(AppDbContext db, PasswordService passwords, Actor actor, long nodeId, long? linkId, LinkInput input, CancellationToken ct = default)
    {
        var access = await DriveAccess.FindAsync(db, actor, nodeId, AccessRole.Manager, ct: ct);
        if (access is null)
        {
            return Outcome<ShareLink>.Denied();
        }

        if (input.Role is not (AccessRole.Viewer or AccessRole.Editor) || (input.Role == AccessRole.Editor && access.Node.Kind != NodeKind.Folder))
        {
            return Outcome<ShareLink>.Fail(Text.T("Links.RoleInvalid"));
        }

        if (input.ExpiresDate is { } expires && expires < DateTime.UtcNow)
        {
            return Outcome<ShareLink>.Fail(Text.T("Links.ExpiresPast"));
        }

        if (!string.IsNullOrEmpty(input.Password) && input.Password.Length < 6)
        {
            return Outcome<ShareLink>.Fail(Text.T("Links.PasswordShort"));
        }

        ShareLink? link;
        if (linkId is { } id)
        {
            link = await db.ShareLinks.FirstOrDefaultAsync(l => l.Id == id && l.NodeId == nodeId, ct);
            if (link is null)
            {
                return Outcome<ShareLink>.Denied();
            }
        }
        else
        {
            link = new ShareLink { NodeId = nodeId };
            db.ShareLinks.Add(link);
        }

        link.Role = input.Role;
        link.ExpiresDate = input.ExpiresDate;
        link.AllowDownload = input.AllowDownload;
        link.Note = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim().Length > 200 ? input.Note.Trim()[..200] : input.Note.Trim();
        if (input.ClearPassword)
        {
            link.PasswordHash = null;
        }

        if (!string.IsNullOrEmpty(input.Password))
        {
            link.PasswordHash = passwords.Hash(input.Password);
        }

        ActivityLog.Add(db, actor.UserId, "link", access.Node, linkId is null ? "created" : "changed");
        await db.SaveChangesAsync(ct);
        return Outcome<ShareLink>.Success(link);
    }

    public static async Task<bool> DeleteLinkAsync(AppDbContext db, Actor actor, long nodeId, long linkId, CancellationToken ct = default)
    {
        var access = await DriveAccess.FindAsync(db, actor, nodeId, AccessRole.Manager, ct: ct);
        if (access is null)
        {
            return false;
        }

        var deleted = await db.ShareLinks.Where(l => l.Id == linkId && l.NodeId == nodeId).ExecuteDeleteAsync(ct) > 0;
        if (deleted)
        {
            await ActivityLog.WriteAsync(db, actor.UserId, "link", access.Node, "deleted", ct: ct);
        }

        return deleted;
    }

    /// <summary>The link of a secret while it works (not expired, its item not in the trash); null otherwise.</summary>
    public static async Task<OpenedLink?> OpenLinkAsync(AppDbContext db, Guid token, CancellationToken ct = default)
    {
        var link = await db.ShareLinks.FirstOrDefaultAsync(l => l.Token == token, ct);
        if (link is null || (link.ExpiresDate is { } e && e < DateTime.UtcNow))
        {
            return null;
        }

        var node = await db.Nodes.AsNoTracking().FirstOrDefaultAsync(n => n.Id == link.NodeId && !n.InTrash, ct);
        var drive = node is null ? null : await db.Drives.AsNoTracking().FirstOrDefaultAsync(d => d.Id == node.DriveId, ct);
        return node is null || drive is null ? null : new OpenedLink(link, node, drive);
    }

    public static Task CountAsync(AppDbContext db, long linkId, bool download, CancellationToken ct = default)
        => download
            ? db.ShareLinks.Where(l => l.Id == linkId).ExecuteUpdateAsync(s => s.SetProperty(l => l.Downloads, l => l.Downloads + 1), ct)
            : db.ShareLinks.Where(l => l.Id == linkId).ExecuteUpdateAsync(s => s.SetProperty(l => l.Visits, l => l.Visits + 1), ct);

    /// <summary>Whether a person wants a mail when something is shared with them, and in which language.</summary>
    public static async Task<(bool Wants, string Language)> MailPreferenceAsync(AppDbContext db, long userId, CancellationToken ct = default)
    {
        var u = await db.Users.AsNoTracking().Where(x => x.Id == userId).Select(x => new { x.MailOnShare, x.Language }).FirstOrDefaultAsync(ct);
        return (u?.MailOnShare ?? false, u?.Language ?? string.Empty);
    }
}
