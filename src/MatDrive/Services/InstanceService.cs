using MatDrive.Data;
using MatDrive.Localization;
using MatDrive.Services.Files;
using MatPlatform;
using Microsoft.EntityFrameworkCore;

namespace MatDrive.Services;

/// <summary>The numbers of the installation (the platform tiles, the admin page System). Nothing here says what is in a file: only how many and how large.</summary>
public sealed record InstanceSnapshot(
    int Users,
    int Admins,
    int SharedDrives,
    int Files,
    int Folders,
    long UsedBytes,
    long FreeBytes,
    int SharedItems,
    int Links,
    int Locations,
    int UploadsLast7Days,
    int InTrash,
    DateTime? LastActivityUtc);

/// <summary>
/// The settings of the system (admin page System and the platform contract). <see cref="PublicUrl"/> and <see cref="AppsUrl"/> are the addresses in force: the value of
/// <c>MAT_PUBLIC_URL</c> / <c>MAT_INTERNAL_URL</c> while it is set (then <see cref="PublicUrlFixed"/> / <see cref="AppsUrlFixed"/>), else the stored one.
/// </summary>
public sealed record StorageSettings(bool RegistrationOpen, string? PublicUrl, long DefaultQuotaBytes, long MaxUploadBytes, int TrashDays, int VersionDays,
    string? AppsUrl = null, bool PublicUrlFixed = false, bool AppsUrlFixed = false);

/// <summary>What the housekeeping removed.</summary>
public sealed record CleanUpResult(int Sessions, int Items, int Versions, int Uploads);

/// <summary>
/// The installation as a whole: its numbers, the settings of the system (registration, public address, space, trash, versions), the state of the database and
/// the housekeeping. The admin page System, the platform contract (<c>Platform/MatDrivePlatform</c>) and the background housekeeping go through it.
/// A broker such as Mattiverse may set the public address (<c>MAT_PUBLIC_URL</c>) and the address for the other apps (<c>MAT_INTERNAL_URL</c>): then those win
/// and the stored values stay as they are (they come back when the variable goes).
/// </summary>
public sealed class InstanceService(AppDbContext db, AccountService accounts, BlobStore store, MatPlatformSettings platform)
{
    /// <summary>The public address in force: <c>MAT_PUBLIC_URL</c>, else the stored one; null = the address of the request.</summary>
    public static string? PublicUrlOf(SystemSetting s, MatPlatformSettings platform) => platform.AppPublicUrl ?? NormalizePublicUrl(s.PublicUrl);

    /// <summary>Where the other apps reach MatDrive: <c>MAT_INTERNAL_URL</c>, else the stored address for other apps; null = the public address.</summary>
    public static string? AppsUrlOf(SystemSetting s, MatPlatformSettings platform) => platform.AppInternalUrl ?? NormalizePublicUrl(s.DriveUrlForOffice);

    /// <summary>Can the database be reached (the health check)?</summary>
    public async Task<bool> CanConnectAsync(CancellationToken ct = default)
    {
        try
        {
            return await db.Database.CanConnectAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Can the files be written (the health check)?</summary>
    public bool CanWriteFiles() => store.CanWrite();

    public async Task<InstanceSnapshot> SnapshotAsync(CancellationToken ct = default)
    {
        var weekAgo = DateTime.UtcNow.AddDays(-7);
        return new InstanceSnapshot(
            Users: await db.Users.CountAsync(u => u.IsActive, ct),
            Admins: await db.Users.CountAsync(u => u.IsActive && u.Role == UserRole.Admin, ct),
            SharedDrives: await db.Drives.CountAsync(d => d.Kind == DriveKind.Shared, ct),
            Files: await db.Nodes.CountAsync(n => n.Kind == NodeKind.File && !n.InTrash, ct),
            Folders: await db.Nodes.CountAsync(n => n.Kind == NodeKind.Folder && !n.InTrash, ct),
            UsedBytes: await db.Drives.SumAsync(d => d.UsedBytes, ct),
            FreeBytes: store.FreeSpace(),
            SharedItems: await db.NodeShares.Select(s => s.NodeId).Distinct().CountAsync(ct),
            Links: await db.ShareLinks.CountAsync(l => l.ExpiresDate == null || l.ExpiresDate > DateTime.UtcNow, ct),
            Locations: await db.StorageLocations.CountAsync(l => l.IsActive, ct),
            UploadsLast7Days: await db.Activities.CountAsync(a => a.Action == "upload" && a.OccurredDate >= weekAgo, ct),
            InTrash: await db.Nodes.CountAsync(n => n.TrashedDate != null, ct),
            LastActivityUtc: await db.Activities.MaxAsync(a => (DateTime?)a.OccurredDate, ct));
    }

    public Task<bool> AnyUserAsync() => accounts.AnyUserAsync();

    /// <summary>What MatDrive knows of the bound office editor (the health check <c>office.edit</c>).</summary>
    public Task<Office.OfficeStatus> OfficeStatusAsync(MatBinding office, CancellationToken ct = default) => Office.OfficeService.StatusAsync(db, office, ct);

    /// <summary>Where the data is: <c>PostgreSQL host:port/database</c> (for the admin page; never in the platform API).</summary>
    public string DatabaseName()
    {
        var cs = new Npgsql.NpgsqlConnectionStringBuilder(db.Database.GetConnectionString());
        return $"PostgreSQL {cs.Host}:{cs.Port}/{cs.Database}";
    }

    /// <summary>Whether sign-up is open and the public address in force (null = the address of the request).</summary>
    public async Task<(bool RegistrationOpen, string? PublicUrl)> SettingsAsync()
    {
        var s = await accounts.SettingsAsync();
        return (s.RegistrationOpen, PublicUrlOf(s, platform));
    }

    public async Task<StorageSettings> StorageSettingsAsync()
    {
        var s = await accounts.SettingsAsync();
        return new StorageSettings(s.RegistrationOpen, PublicUrlOf(s, platform), s.DefaultQuotaBytes, s.MaxUploadBytes, s.TrashDays, s.VersionDays,
            AppsUrlOf(s, platform), platform.AppPublicUrl is not null, platform.AppInternalUrl is not null);
    }

    /// <summary>
    /// Saves the settings of the system; a <c>null</c> leaves a value as it is, an empty address clears it. An address that a variable sets (<c>MAT_PUBLIC_URL</c>,
    /// <c>MAT_INTERNAL_URL</c>) is left as it is. Answers the field and the message of the first wrong value (nothing is saved then), else <c>null</c>.
    /// </summary>
    public async Task<(string Field, string Message)?> SaveSettingsAsync(bool? registrationOpen, string? publicUrl, long? defaultQuotaBytes = null, long? maxUploadBytes = null, int? trashDays = null, int? versionDays = null,
        string? appsUrl = null)
    {
        publicUrl = platform.AppPublicUrl is null ? publicUrl : null;
        appsUrl = platform.AppInternalUrl is null ? appsUrl : null;
        string? url = null;
        if (publicUrl is not null)
        {
            url = NormalizePublicUrl(publicUrl);
            if (url is not null && !IsPublicUrl(url))
            {
                return ("PublicUrl", Text.T("System.PublicUrl.Invalid"));
            }
        }

        string? forApps = null;
        if (appsUrl is not null)
        {
            forApps = NormalizePublicUrl(appsUrl);
            if (forApps is not null && !IsPublicUrl(forApps))
            {
                return ("AppsUrl", Text.T("System.PublicUrl.Invalid"));
            }
        }

        if (defaultQuotaBytes < 0 || maxUploadBytes < 0)
        {
            return ("DefaultQuotaGb", Text.T("System.Number.Invalid"));
        }

        if (trashDays is < 1 or > 3650)
        {
            return ("TrashDays", Text.T("System.TrashDays.Invalid"));
        }

        if (versionDays is < 0 or > 36500)
        {
            return ("VersionDays", Text.T("System.VersionDays.Invalid"));
        }

        var s = await accounts.SettingsAsync();
        if (registrationOpen is { } open)
        {
            s.RegistrationOpen = open;
        }

        if (publicUrl is not null)
        {
            s.PublicUrl = url;
        }

        if (appsUrl is not null)
        {
            s.DriveUrlForOffice = forApps;
        }

        if (defaultQuotaBytes is { } q)
        {
            s.DefaultQuotaBytes = q;
        }

        if (maxUploadBytes is { } m)
        {
            s.MaxUploadBytes = m;
        }

        if (trashDays is { } t)
        {
            s.TrashDays = t;
        }

        if (versionDays is { } v)
        {
            s.VersionDays = v;
        }

        await db.SaveChangesAsync();
        return null;
    }

    /// <summary>The address without spaces and the last slash; <c>null</c> when empty.</summary>
    public static string? NormalizePublicUrl(string? url)
    {
        var trimmed = (url ?? string.Empty).Trim().TrimEnd('/');
        return trimmed.Length == 0 ? null : trimmed;
    }

    public static bool IsPublicUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    /// <summary>
    /// The housekeeping: ended sessions go; items that lie in the trash longer than its days are deleted for good (with their bytes); versions older than the days
    /// of the settings go (never the current one, never one marked to keep); broken uploads go; the used space of every drive is counted again.
    /// </summary>
    public async Task<CleanUpResult> CleanUpAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var settings = await accounts.SettingsAsync();
        var sessions = await db.Sessions.Where(s => s.IsRevoked || s.ExpiresDate < now.AddDays(-1)).ExecuteDeleteAsync(ct);

        var trashLimit = now.AddDays(-Math.Max(1, settings.TrashDays));
        var expired = await db.Nodes.Where(n => n.TrashedDate != null && n.TrashedDate < trashLimit).ToListAsync(ct);
        var items = 0;
        foreach (var node in expired.OrderBy(n => n.Path.Length))
        {
            if (!await db.Nodes.AnyAsync(n => n.Id == node.Id, ct))
            {
                continue; // gone with a folder above it
            }

            foreach (var blob in await NodeService.RemoveSubtreeAsync(db, node, ct))
            {
                store.Delete(blob);
            }

            items++;
        }

        var versions = 0;
        if (settings.VersionDays > 0)
        {
            var limit = now.AddDays(-settings.VersionDays);
            var old = await db.NodeVersions
                .Where(v => !v.KeepForever && v.CreateDate < limit && db.Nodes.IgnoreQueryFilters().Any(n => n.Id == v.NodeId && n.CurrentVersionId != v.Id))
                .Select(v => new { v.Id, v.BlobId }).ToListAsync(ct);
            foreach (var v in old)
            {
                await db.NodeVersions.Where(x => x.Id == v.Id).ExecuteDeleteAsync(ct);
                store.Delete(v.BlobId);
                versions++;
            }
        }

        var uploads = await db.UploadSessions.Where(u => u.ExpiresDate < now).ExecuteDeleteAsync(ct);
        uploads += store.CleanTemp();
        await DriveService.RecountAsync(db, ct);
        return new CleanUpResult(sessions, items, versions, uploads);
    }
}
