using MatDrive.Data;
using MatDrive.Localization;
using MatDrive.Services.Accounts;
using MatDrive.Services.Auth;
using MatDrive.Services.Files;
using Microsoft.EntityFrameworkCore;

namespace MatDrive.Services.Locations;

/// <summary>One row of the list of locations in the administration.</summary>
public sealed record LocationRow(long Id, string Name, LocationKind Kind, string Where, bool IsActive, bool IsReadOnly, bool ForEveryone, int People, LocationStatus? Status);

/// <summary>What the list asks for (as it comes from the toolbar).</summary>
public sealed record LocationCriteria(string? Search, string? Kind, string? State, string Sort, bool Descending, int Page, int PageSize);

public sealed record LocationList(List<LocationRow> Items, int Total, int Matching);

/// <summary>The form of a location as it comes from the page (not yet checked). An empty password or key keeps the stored one.</summary>
public sealed record LocationInput(
    string? Name,
    LocationKind Kind,
    string? RootPath,
    string? Host,
    int? Port,
    string? Share,
    string? Username,
    string? Password,
    bool ClearPassword,
    string? Domain,
    string? Url,
    bool UseTls,
    bool AcceptAnyCertificate,
    bool UseKey,
    string? PrivateKey,
    bool IsReadOnly,
    bool IsActive,
    string? Note);

/// <summary>A location for the edit page: everything but the secrets (only whether there are some).</summary>
public sealed record LocationDetail(
    long Id,
    string Name,
    LocationKind Kind,
    string RootPath,
    string? Host,
    int? Port,
    string? Share,
    string? Username,
    string? Domain,
    string? Url,
    bool UseTls,
    bool AcceptAnyCertificate,
    bool HasPassword,
    bool HasPrivateKey,
    bool IsReadOnly,
    bool IsActive,
    string? Note,
    LocationStatus? Status);

public sealed record LocationPerson(long UserId, string Name, string Email, AccessRole Role);

/// <summary>Who may use a location: everybody with a role (or nobody), and the people of its list.</summary>
public sealed record LocationAccessInfo(long Id, string Name, bool IsActive, bool IsReadOnly, bool ForEveryone, AccessRole EveryoneRole, List<LocationPerson> People);

/// <summary>
/// The storage locations for administrators: list, create, change, test, delete, and who may use them. Every function checks in its first line that the actor is
/// an active administrator (null / denied otherwise), whatever the page did before. The secrets are encrypted (DataProtection) and never leave this class in
/// clear text; an empty field keeps the stored one. Deleting a location never deletes its files: only the entry of MatDrive goes.
/// </summary>
public static class LocationAdmin
{
    public const int NameMax = 120;

    public static readonly AccessRole[] Roles = [AccessRole.Viewer, AccessRole.Editor];

    // ---- list ---------------------------------------------------------------------------------------------------------------------

    public static async Task<LocationList?> ListAsync(AppDbContext db, LocationConnections connections, long adminId, LocationCriteria criteria, CancellationToken ct = default)
    {
        if (!await UserAdmin.IsAdminAsync(db, adminId, ct))
        {
            return null;
        }

        var all = await db.StorageLocations.AsNoTracking()
            .Select(l => new { Location = l, People = l.Access.Count })
            .ToListAsync(ct);
        var rows = all.Select(x => new LocationRow(x.Location.Id, x.Location.Name, x.Location.Kind, WhereOf(x.Location), x.Location.IsActive, x.Location.IsReadOnly,
            x.Location.ForEveryone, x.People, connections.StatusOf(x.Location.Id))).ToList();
        var total = rows.Count;

        IEnumerable<LocationRow> query = rows;
        if (!string.IsNullOrWhiteSpace(criteria.Search))
        {
            var s = criteria.Search.Trim();
            query = query.Where(r => r.Name.Contains(s, StringComparison.OrdinalIgnoreCase) || r.Where.Contains(s, StringComparison.OrdinalIgnoreCase));
        }

        if (Enum.TryParse<LocationKind>(criteria.Kind, ignoreCase: true, out var kind) && Enum.IsDefined(kind))
        {
            query = query.Where(r => r.Kind == kind);
        }

        query = criteria.State switch
        {
            "reachable" => query.Where(r => r.IsActive && r.Status is { Reachable: true }),
            "unreachable" => query.Where(r => r.IsActive && r.Status is { Reachable: false }),
            "inactive" => query.Where(r => !r.IsActive),
            "active" => query.Where(r => r.IsActive),
            _ => query,
        };

        query = (criteria.Sort, criteria.Descending) switch
        {
            ("kind", false) => query.OrderBy(r => r.Kind).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase),
            ("kind", true) => query.OrderByDescending(r => r.Kind).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase),
            ("free", false) => query.OrderBy(r => r.Status?.Space?.Free ?? long.MaxValue),
            ("free", true) => query.OrderByDescending(r => r.Status?.Space?.Free ?? -1),
            (_, true) => query.OrderByDescending(r => r.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => query.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase),
        };

        var matching = query.ToList();
        var page = matching.Skip(Math.Max(0, criteria.Page - 1) * criteria.PageSize).Take(criteria.PageSize).ToList();
        return new LocationList(page, total, matching.Count);
    }

    /// <summary>Where a location is, for people: the folder, the share, the server and port, the address (never a password).</summary>
    public static string WhereOf(StorageLocation l)
    {
        var port = l.Port is > 0 ? ":" + l.Port.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
        var folder = string.IsNullOrWhiteSpace(l.RootPath) ? string.Empty : PathGuard.RemoteBase(l.RootPath);
        return l.Kind switch
        {
            LocationKind.Local => l.RootPath,
            LocationKind.Smb => $@"\\{l.Host}{port}" + (string.IsNullOrWhiteSpace(l.Share) ? string.Empty : @"\" + l.Share + folder.Replace('/', '\\')),
            LocationKind.Sftp => $"sftp://{(string.IsNullOrEmpty(l.Username) ? string.Empty : l.Username + "@")}{l.Host}{port}{folder}",
            LocationKind.Ftp => $"{(l.UseTls ? "ftps" : "ftp")}://{(string.IsNullOrEmpty(l.Username) ? string.Empty : l.Username + "@")}{l.Host}{port}{folder}",
            LocationKind.WebDav => l.Url ?? string.Empty,
            _ => string.Empty,
        };
    }

    // ---- one location -------------------------------------------------------------------------------------------------------------

    public static async Task<LocationDetail?> GetAsync(AppDbContext db, LocationConnections connections, long adminId, long id, CancellationToken ct = default)
    {
        if (!await UserAdmin.IsAdminAsync(db, adminId, ct))
        {
            return null;
        }

        var l = await db.StorageLocations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return l is null ? null : new LocationDetail(l.Id, l.Name, l.Kind, l.RootPath, l.Host, l.Port, l.Share, l.Username, l.Domain, l.Url, l.UseTls, l.AcceptAnyCertificate,
            !string.IsNullOrEmpty(l.SecretProtected), !string.IsNullOrEmpty(l.PrivateKeyProtected), l.IsReadOnly, l.IsActive, l.Note, connections.StatusOf(l.Id));
    }

    /// <summary>Creates (<paramref name="id"/> null) or changes a location. Answers its id, or the errors by field (or Denied / not found as null id and no error).</summary>
    public static async Task<(long? Id, List<FieldError> Errors)> SaveAsync(AppDbContext db, LocationConnections connections, long adminId, long? id, LocationInput input, CancellationToken ct = default)
    {
        if (!await UserAdmin.IsAdminAsync(db, adminId, ct))
        {
            return (null, []);
        }

        StorageLocation? location = null;
        if (id is { } existingId)
        {
            location = await db.StorageLocations.FirstOrDefaultAsync(l => l.Id == existingId, ct);
            if (location is null)
            {
                return (null, []);
            }
        }

        var errors = Validate(connections, input, location);
        var name = (input.Name ?? string.Empty).Trim();
        if (name.Length > 0 && await db.StorageLocations.AnyAsync(l => l.Name.ToLower() == name.ToLower() && l.Id != (id ?? 0), ct))
        {
            errors.Add(new FieldError(nameof(LocationInput.Name), Text.T("Locations.Error.NameTaken")));
        }

        if (errors.Count > 0)
        {
            return (null, errors);
        }

        var created = location is null;
        location ??= db.StorageLocations.Add(new StorageLocation()).Entity;
        Apply(connections, location, input);
        if (!created)
        {
            location.UpdateState = SoftEntity.Updated;
        }

        ActivityLog.Add(db, adminId, "location", detail: created ? "create" : "change", target: location.Name);
        await db.SaveChangesAsync(ct);

        // the menu and the list show the new state soon; the page does not wait for a slow server
        connections.Forget(location.Id);
        if (location.IsActive)
        {
            var copy = Copy(location);
            _ = Task.Run(() => LocationMonitor.CheckAsync(connections, copy, CancellationToken.None), CancellationToken.None);
        }

        return (location.Id, []);
    }

    /// <summary>Tries the connection with the values of the form (a stored secret is used when the field is empty), without saving anything.</summary>
    public static async Task<Outcome<LocationStatus>> TestAsync(AppDbContext db, LocationConnections connections, long adminId, long? id, LocationInput input, CancellationToken ct = default)
    {
        if (!await UserAdmin.IsAdminAsync(db, adminId, ct))
        {
            return Outcome<LocationStatus>.Denied();
        }

        StorageLocation? stored = null;
        if (id is { } existingId)
        {
            stored = await db.StorageLocations.AsNoTracking().FirstOrDefaultAsync(l => l.Id == existingId, ct);
            if (stored is null)
            {
                return Outcome<LocationStatus>.Denied();
            }
        }

        var errors = Validate(connections, input with { Name = string.IsNullOrWhiteSpace(input.Name) ? "test" : input.Name }, stored);
        if (errors.Count > 0)
        {
            return Outcome<LocationStatus>.Fail(errors[0].Message);
        }

        var probe = new StorageLocation();
        if (stored is not null)
        {
            probe.SecretProtected = stored.SecretProtected;
            probe.PrivateKeyProtected = stored.PrivateKeyProtected;
        }

        Apply(connections, probe, input);
        var status = await LocationMonitor.ProbeAsync(connections, connections.SettingsOf(probe), ct);
        if (stored is not null && Same(stored, probe))
        {
            connections.Report(stored.Id, status);
        }

        return Outcome<LocationStatus>.Success(status);
    }

    /// <summary>Removes a location from MatDrive (soft delete). Its files stay where they are.</summary>
    public static async Task<bool> DeleteAsync(AppDbContext db, LocationConnections connections, long adminId, long id, CancellationToken ct = default)
    {
        if (!await UserAdmin.IsAdminAsync(db, adminId, ct))
        {
            return false;
        }

        var location = await db.StorageLocations.FirstOrDefaultAsync(l => l.Id == id, ct);
        if (location is null)
        {
            return false;
        }

        location.UpdateState = SoftEntity.Deleted;
        location.IsActive = false;
        location.SecretProtected = null;
        location.PrivateKeyProtected = null;
        db.StorageLocationAccess.RemoveRange(db.StorageLocationAccess.Where(a => a.LocationId == id));
        ActivityLog.Add(db, adminId, "location", detail: "delete", target: location.Name);
        await db.SaveChangesAsync(ct);
        connections.Forget(id);
        return true;
    }

    /// <summary>Checks every active location now (the button of the list, the action of the platform).</summary>
    public static async Task<LocationCheckSummary?> CheckAllAsync(AppDbContext db, LocationMonitor monitor, long adminId, CancellationToken ct = default)
        => await UserAdmin.IsAdminAsync(db, adminId, ct) ? await monitor.CheckAllAsync(ct) : null;

    // ---- access -------------------------------------------------------------------------------------------------------------------

    public static async Task<LocationAccessInfo?> AccessAsync(AppDbContext db, long adminId, long id, CancellationToken ct = default)
    {
        if (!await UserAdmin.IsAdminAsync(db, adminId, ct))
        {
            return null;
        }

        var l = await db.StorageLocations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (l is null)
        {
            return null;
        }

        var people = await db.StorageLocationAccess.AsNoTracking().Where(a => a.LocationId == id)
            .Join(db.Users, a => a.UserId, u => u.Id, (a, u) => new LocationPerson(u.Id, u.DisplayName, u.Email, a.Role))
            .ToListAsync(ct);
        people = people.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        return new LocationAccessInfo(l.Id, l.Name, l.IsActive, l.IsReadOnly, l.ForEveryone, l.EveryoneRole, people);
    }

    /// <summary>Everybody gets a role on the location (Viewer or Editor), or nobody (None): then only the people of the list.</summary>
    public static async Task<bool> SetEveryoneAsync(AppDbContext db, long adminId, long id, AccessRole role, CancellationToken ct = default)
    {
        if (!await UserAdmin.IsAdminAsync(db, adminId, ct) || (role != AccessRole.None && !Roles.Contains(role)))
        {
            return false;
        }

        var l = await db.StorageLocations.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (l is null)
        {
            return false;
        }

        l.ForEveryone = role != AccessRole.None;
        if (role != AccessRole.None)
        {
            l.EveryoneRole = role;
        }

        l.UpdateState = SoftEntity.Updated;
        ActivityLog.Add(db, adminId, "location", detail: "access everyone " + role.ToString().ToLowerInvariant(), target: l.Name);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Adds a person by the address of their account (or changes their role when they are on the list already).</summary>
    public static async Task<Outcome<LocationPerson>> AddPersonAsync(AppDbContext db, long adminId, long id, string? email, AccessRole role, CancellationToken ct = default)
    {
        if (!await UserAdmin.IsAdminAsync(db, adminId, ct) || !Roles.Contains(role))
        {
            return Outcome<LocationPerson>.Denied();
        }

        var l = await db.StorageLocations.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (l is null)
        {
            return Outcome<LocationPerson>.Denied();
        }

        var address = (email ?? string.Empty).Trim().ToLowerInvariant();
        if (address.Length == 0)
        {
            return Outcome<LocationPerson>.Fail(Text.T("Locations.Access.EmailRequired"));
        }

        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Email == address, ct);
        if (user is null)
        {
            return Outcome<LocationPerson>.Fail(Text.T("Locations.Access.NoAccount", address));
        }

        var entry = await db.StorageLocationAccess.FirstOrDefaultAsync(a => a.LocationId == id && a.UserId == user.Id, ct);
        if (entry is null)
        {
            db.StorageLocationAccess.Add(new StorageLocationAccess { LocationId = id, UserId = user.Id, Role = role });
        }
        else
        {
            entry.Role = role;
        }

        ActivityLog.Add(db, adminId, "location", detail: $"access {user.Email} {role.ToString().ToLowerInvariant()}", target: l.Name);
        await db.SaveChangesAsync(ct);
        return Outcome<LocationPerson>.Success(new LocationPerson(user.Id, user.DisplayName, user.Email, role));
    }

    public static async Task<bool> SetPersonAsync(AppDbContext db, long adminId, long id, long userId, AccessRole role, CancellationToken ct = default)
    {
        if (!await UserAdmin.IsAdminAsync(db, adminId, ct) || !Roles.Contains(role))
        {
            return false;
        }

        var entry = await db.StorageLocationAccess.Include(a => a.Location).Include(a => a.User).FirstOrDefaultAsync(a => a.LocationId == id && a.UserId == userId, ct);
        if (entry is null || entry.Location is null)
        {
            return false;
        }

        entry.Role = role;
        ActivityLog.Add(db, adminId, "location", detail: $"access {entry.User?.Email} {role.ToString().ToLowerInvariant()}", target: entry.Location.Name);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public static async Task<bool> RemovePersonAsync(AppDbContext db, long adminId, long id, long userId, CancellationToken ct = default)
    {
        if (!await UserAdmin.IsAdminAsync(db, adminId, ct))
        {
            return false;
        }

        var entry = await db.StorageLocationAccess.Include(a => a.Location).Include(a => a.User).FirstOrDefaultAsync(a => a.LocationId == id && a.UserId == userId, ct);
        if (entry is null || entry.Location is null)
        {
            return false;
        }

        db.StorageLocationAccess.Remove(entry);
        ActivityLog.Add(db, adminId, "location", detail: $"access {entry.User?.Email} removed", target: entry.Location.Name);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------------------

    private static List<FieldError> Validate(LocationConnections connections, LocationInput input, StorageLocation? stored)
    {
        var errors = new List<FieldError>();
        var name = (input.Name ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            errors.Add(new FieldError(nameof(LocationInput.Name), Text.T("Common.NameRequired")));
        }
        else if (name.Length > NameMax)
        {
            errors.Add(new FieldError(nameof(LocationInput.Name), Text.T("Locations.Error.NameTooLong", NameMax)));
        }

        if (!Enum.IsDefined(input.Kind))
        {
            errors.Add(new FieldError(nameof(LocationInput.Kind), Text.T("Locations.Error.KindRequired")));
            return errors;
        }

        var root = (input.RootPath ?? string.Empty).Trim();
        if (root.Length > 500)
        {
            errors.Add(new FieldError(nameof(LocationInput.RootPath), Text.T("Locations.Error.TooLong")));
        }

        switch (input.Kind)
        {
            case LocationKind.Local:
                if (root.Length == 0)
                {
                    errors.Add(new FieldError(nameof(LocationInput.RootPath), Text.T("Locations.Error.PathRequired")));
                }
                else if (!Path.IsPathFullyQualified(root))
                {
                    errors.Add(new FieldError(nameof(LocationInput.RootPath), Text.T("Locations.Error.PathNotAbsolute")));
                }
                else if (!connections.IsAllowedLocalFolder(Path.GetFullPath(root)))
                {
                    errors.Add(new FieldError(nameof(LocationInput.RootPath), Text.T("Locations.Error.PathOutside", connections.StorageRoot)));
                }
                else if (!Directory.Exists(root))
                {
                    errors.Add(new FieldError(nameof(LocationInput.RootPath), Text.T("Locations.Error.PathMissing")));
                }

                break;
            case LocationKind.Smb or LocationKind.Sftp or LocationKind.Ftp:
                var host = (input.Host ?? string.Empty).Trim();
                if (host.Length == 0)
                {
                    errors.Add(new FieldError(nameof(LocationInput.Host), Text.T("Locations.Error.HostRequired")));
                }
                else if (host.Length > 255 || host.Any(c => char.IsWhiteSpace(c) || c is '/' or '\\' or '@'))
                {
                    errors.Add(new FieldError(nameof(LocationInput.Host), Text.T("Locations.Error.HostInvalid")));
                }

                if (input.Port is { } port && port is < 1 or > 65535)
                {
                    errors.Add(new FieldError(nameof(LocationInput.Port), Text.T("Locations.Error.PortInvalid")));
                }

                if ((input.Share ?? string.Empty).Trim().Any(c => c is '/' or '\\'))
                {
                    errors.Add(new FieldError(nameof(LocationInput.Share), Text.T("Locations.Error.ShareInvalid")));
                }

                if (input.Kind == LocationKind.Sftp && input.UseKey && string.IsNullOrWhiteSpace(input.PrivateKey) && string.IsNullOrEmpty(stored?.PrivateKeyProtected))
                {
                    errors.Add(new FieldError(nameof(LocationInput.PrivateKey), Text.T("Locations.Error.KeyRequired")));
                }

                if (!string.IsNullOrWhiteSpace(input.PrivateKey) && (input.PrivateKey.Length > 12000 || !input.PrivateKey.Contains("PRIVATE KEY", StringComparison.Ordinal)))
                {
                    errors.Add(new FieldError(nameof(LocationInput.PrivateKey), Text.T("Locations.Error.KeyInvalid")));
                }

                break;
            case LocationKind.WebDav:
                var url = (input.Url ?? string.Empty).Trim();
                if (url.Length == 0)
                {
                    errors.Add(new FieldError(nameof(LocationInput.Url), Text.T("Locations.Error.UrlRequired")));
                }
                else if (url.Length > 500 || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
                {
                    errors.Add(new FieldError(nameof(LocationInput.Url), Text.T("Locations.Error.UrlInvalid")));
                }

                break;
        }

        if ((input.Username ?? string.Empty).Length > 255 || (input.Domain ?? string.Empty).Length > 255 || (input.Share ?? string.Empty).Length > 255 || (input.Password ?? string.Empty).Length > 1000)
        {
            errors.Add(new FieldError(nameof(LocationInput.Username), Text.T("Locations.Error.TooLong")));
        }

        if ((input.Note ?? string.Empty).Length > 1000)
        {
            errors.Add(new FieldError(nameof(LocationInput.Note), Text.T("Locations.Error.TooLong")));
        }

        return errors;
    }

    /// <summary>Takes the checked values into the entity: what the kind does not use is cleared, a secret is only replaced by a new one.</summary>
    private static void Apply(LocationConnections connections, StorageLocation l, LocationInput input)
    {
        static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        l.Name = (input.Name ?? string.Empty).Trim();
        l.Kind = input.Kind;
        l.IsReadOnly = input.IsReadOnly;
        l.IsActive = input.IsActive;
        l.Note = Clean(input.Note);
        var remote = input.Kind is LocationKind.Smb or LocationKind.Sftp or LocationKind.Ftp;
        l.RootPath = input.Kind switch
        {
            LocationKind.Local => Path.TrimEndingDirectorySeparator(Path.GetFullPath((input.RootPath ?? string.Empty).Trim())),
            LocationKind.WebDav => string.Empty,
            _ => (input.RootPath ?? string.Empty).Trim(),
        };
        l.Host = remote ? Clean(input.Host) : null;
        l.Port = remote ? input.Port : null;
        l.Share = input.Kind == LocationKind.Smb ? Clean(input.Share) : null;
        l.Domain = input.Kind == LocationKind.Smb ? Clean(input.Domain) : null;
        l.Url = input.Kind == LocationKind.WebDav ? Clean(input.Url) : null;
        l.UseTls = input.Kind == LocationKind.Ftp && input.UseTls;
        l.AcceptAnyCertificate = input.Kind is LocationKind.Ftp or LocationKind.WebDav && input.AcceptAnyCertificate;
        l.Username = input.Kind == LocationKind.Local ? null : Clean(input.Username);

        if (input.Kind == LocationKind.Local)
        {
            l.SecretProtected = null;
            l.PrivateKeyProtected = null;
            return;
        }

        if (input.ClearPassword)
        {
            l.SecretProtected = null;
        }
        else if (!string.IsNullOrEmpty(input.Password))
        {
            l.SecretProtected = connections.Protect(input.Password);
        }

        if (input.Kind != LocationKind.Sftp || !input.UseKey)
        {
            l.PrivateKeyProtected = null;
        }
        else if (!string.IsNullOrWhiteSpace(input.PrivateKey))
        {
            l.PrivateKeyProtected = connections.Protect(input.PrivateKey.Trim());
        }
    }

    private static bool Same(StorageLocation a, StorageLocation b)
        => a.Kind == b.Kind && a.RootPath == b.RootPath && a.Host == b.Host && a.Port == b.Port && a.Share == b.Share && a.Username == b.Username && a.Domain == b.Domain
           && a.Url == b.Url && a.UseTls == b.UseTls && a.SecretProtected == b.SecretProtected && a.PrivateKeyProtected == b.PrivateKeyProtected;

    private static StorageLocation Copy(StorageLocation l) => new()
    {
        Id = l.Id, Name = l.Name, Kind = l.Kind, RootPath = l.RootPath, Host = l.Host, Port = l.Port, Share = l.Share, Username = l.Username, SecretProtected = l.SecretProtected,
        Domain = l.Domain, Url = l.Url, UseTls = l.UseTls, AcceptAnyCertificate = l.AcceptAnyCertificate, PrivateKeyProtected = l.PrivateKeyProtected, IsActive = l.IsActive,
    };
}
