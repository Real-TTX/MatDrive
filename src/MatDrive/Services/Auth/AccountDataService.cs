using System.Text.Json;
using System.Text.Json.Serialization;
using MatDrive.Data;
using MatDrive.Services.Files;
using Microsoft.EntityFrameworkCore;

namespace MatDrive.Services.Auth;

/// <summary>
/// The data of a person under the GDPR: the export of everything that is about them (Art. 15 and 20, a JSON file), and the erasure of the account (Art. 17).
/// <para>
/// The export holds the account and its settings, the list of the files and folders of "My Drive" (paths, sizes, dates, versions – the files themselves are
/// downloaded as a zip from the drive), the shared drives and the items shared with the person, the shares and links the person made, the app passwords (names),
/// the activity of the person and the sessions. No secret: no password hash, no token, no hash of an app password.
/// </para>
/// <para>
/// The erasure deletes for good: "My Drive" with every file and version (the bytes too), the memberships, shares, stars, visits, app passwords and sessions.
/// What the person changed in a shared drive stays there, without their name.
/// </para>
/// </summary>
public sealed class AccountDataService(AppDbContext db, BlobStore store)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The export as JSON (UTF-8).</summary>
    public async Task<byte[]> ExportAsync(long userId, CancellationToken ct = default)
    {
        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, ct);
        var drive = await DriveService.MyDriveAsync(db, userId, ct);
        var nodes = await db.Nodes.AsNoTracking().Where(n => n.DriveId == drive.Id).OrderBy(n => n.Path).ToListAsync(ct);
        var ids = nodes.Select(n => n.Id).ToList();
        var versions = await db.NodeVersions.AsNoTracking().Where(v => ids.Contains(v.NodeId)).OrderBy(v => v.Number).ToListAsync(ct);
        var names = nodes.ToDictionary(n => n.Id, n => n.Name);
        string PathOf(Node n) => "/" + string.Join('/', DriveAccess.IdsOf(n.Path).Select(id => names.GetValueOrDefault(id, "?")));

        var export = new
        {
            Format = "MatDrive export 1",
            ExportedAt = DateTime.UtcNow,
            Account = new
            {
                user.Email,
                user.DisplayName,
                user.Role,
                user.TimeZone,
                user.Language,
                user.Theme,
                user.Scheme,
                user.QuotaBytes,
                user.MailOnShare,
                user.CreateDate,
                user.LastLoginDate,
                LinkedWithMatIdentity = user.ExternalSubject != null,
            },
            MyDrive = new
            {
                drive.UsedBytes,
                Items = nodes.Select(n => new
                {
                    Path = PathOf(n),
                    Kind = n.Kind,
                    n.Size,
                    n.MimeType,
                    n.CreateDate,
                    n.ModifiedDate,
                    InTrash = n.InTrash ? true : (bool?)null,
                    n.Description,
                    Versions = n.Kind == NodeKind.File ? versions.Where(v => v.NodeId == n.Id).Select(v => new { v.Number, v.Size, v.CreateDate, v.Source, v.KeepForever, v.Sha256 }).ToList() : null,
                }).ToList(),
            },
            SharedDrives = await db.DriveMembers.AsNoTracking().Where(m => m.UserId == userId).Select(m => new { m.Drive!.Name, m.Role, Since = m.CreateDate }).ToListAsync(ct),
            SharedWithYou = await db.NodeShares.AsNoTracking().Where(s => s.UserId == userId).Select(s => new { s.Node!.Name, s.Role, Since = s.CreateDate }).ToListAsync(ct),
            YourShares = await db.NodeShares.AsNoTracking().Where(s => s.CreateUserId == userId).Select(s => new { Item = s.Node!.Name, Person = s.User!.DisplayName, s.Role, s.CreateDate }).ToListAsync(ct),
            YourLinks = await db.ShareLinks.AsNoTracking().Where(l => l.CreateUserId == userId).Select(l => new { Item = l.Node!.Name, l.Role, l.ExpiresDate, l.Visits, l.Downloads, l.CreateDate }).ToListAsync(ct),
            AppPasswords = await db.AppPasswords.AsNoTracking().Where(p => p.UserId == userId).Select(p => new { p.Name, p.CreateDate, p.LastUsedDate }).ToListAsync(ct),
            Activity = await db.Activities.AsNoTracking().Where(a => a.UserId == userId).OrderByDescending(a => a.OccurredDate).Take(5000)
                .Select(a => new { a.OccurredDate, a.Action, a.Target, a.Detail }).ToListAsync(ct),
            Sessions = await db.Sessions.AsNoTracking().Where(s => s.UserId == userId)
                .Select(s => new { s.CreateDate, s.LastSeenDate, s.ExpiresDate, s.IsRevoked, s.UserAgent, s.IpAddress }).ToListAsync(ct),
        };

        return JsonSerializer.SerializeToUtf8Bytes(export, Json);
    }

    /// <summary>Erases the account for good (see the class). The caller checks the password and that it is not the last administrator.</summary>
    public async Task EraseAsync(long userId, CancellationToken ct = default)
    {
        var drives = await db.Drives.IgnoreQueryFilters().Where(d => d.Kind == DriveKind.Personal && d.OwnerId == userId).Select(d => d.Id).ToListAsync(ct);
        var nodeIds = db.Nodes.IgnoreQueryFilters().Where(n => drives.Contains(n.DriveId)).Select(n => n.Id);
        var blobs = await db.NodeVersions.Where(v => nodeIds.Contains(v.NodeId)).Select(v => v.BlobId).ToListAsync(ct);

        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            // what the person did in shared drives stays, without their name
            await db.Nodes.IgnoreQueryFilters().Where(n => n.ModifiedById == userId).ExecuteUpdateAsync(s => s.SetProperty(x => x.ModifiedById, (long?)null), ct);
            await db.NodeVersions.Where(v => v.CreateUserId == userId).ExecuteUpdateAsync(s => s.SetProperty(x => x.CreateUserId, (long?)null), ct);
            await db.Activities.Where(a => a.UserId == userId).ExecuteDeleteAsync(ct);
            await db.NodeStars.Where(s => s.UserId == userId).ExecuteDeleteAsync(ct);
            await db.NodeVisits.Where(v => v.UserId == userId).ExecuteDeleteAsync(ct);
            await db.UploadSessions.Where(u => u.UserId == userId).ExecuteDeleteAsync(ct);

            // the personal drive with everything in it, then the account (sessions, memberships, shares and app passwords go with it)
            await db.Nodes.IgnoreQueryFilters().Where(n => drives.Contains(n.DriveId)).ExecuteDeleteAsync(ct);
            await db.Drives.IgnoreQueryFilters().Where(d => drives.Contains(d.Id)).ExecuteDeleteAsync(ct);
            await db.Users.IgnoreQueryFilters().Where(u => u.Id == userId).ExecuteDeleteAsync(ct);
            await tx.CommitAsync(ct);
        }

        foreach (var blob in blobs)
        {
            store.Delete(blob);
        }
    }

    /// <summary>Is the person the last active administrator (who must not go, or nobody can run the installation)?</summary>
    public async Task<bool> IsLastAdminAsync(long userId)
        => await db.Users.AnyAsync(u => u.Id == userId && u.Role == UserRole.Admin && u.IsActive)
           && !await db.Users.AnyAsync(u => u.Id != userId && u.Role == UserRole.Admin && u.IsActive);
}
