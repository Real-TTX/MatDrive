using MatDrive.Data;
using Microsoft.EntityFrameworkCore;

namespace MatDrive.Services.Locations;

/// <summary>
/// Who may see and change a storage location (a folder of the server or a share of another machine, browsed live like in MatFile): every person with the
/// role "for everyone", or the people in its access list. Administrators manage locations but only see them when they are on the list too.
/// </summary>
public static class LocationAccess
{
    /// <summary>The role of a person on a location (None when they may not see it, or when it is switched off).</summary>
    public static async Task<AccessRole> RoleAsync(AppDbContext db, long userId, StorageLocation location, CancellationToken ct = default)
    {
        if (!location.IsActive)
        {
            return AccessRole.None;
        }

        var role = location.ForEveryone ? location.EveryoneRole : AccessRole.None;
        var listed = await db.StorageLocationAccess.AsNoTracking().Where(a => a.LocationId == location.Id && a.UserId == userId).Select(a => (AccessRole?)a.Role).FirstOrDefaultAsync(ct);
        if (listed is { } r && r > role)
        {
            role = r;
        }

        if (location.IsReadOnly && role > AccessRole.Viewer)
        {
            role = AccessRole.Viewer;
        }

        return role;
    }

    /// <summary>The location with the role of the person, or null when they may not see it.</summary>
    public static async Task<(StorageLocation Location, AccessRole Role)?> FindAsync(AppDbContext db, long userId, long locationId, AccessRole minimum = AccessRole.Viewer, CancellationToken ct = default)
    {
        var location = await db.StorageLocations.AsNoTracking().FirstOrDefaultAsync(l => l.Id == locationId, ct);
        if (location is null)
        {
            return null;
        }

        var role = await RoleAsync(db, userId, location, ct);
        return role >= minimum && role > AccessRole.None ? (location, role) : null;
    }

    /// <summary>The locations a person can open with their role on each (the pickers of "Move to" and "Copy to").</summary>
    public static async Task<IReadOnlyList<(long Id, string Name, LocationKind Kind, AccessRole Role)>> ListForPersonAsync(AppDbContext db, long userId, CancellationToken ct = default)
    {
        var rows = await db.StorageLocations.AsNoTracking()
            .Where(l => l.IsActive && (l.ForEveryone || l.Access.Any(a => a.UserId == userId)))
            .Select(l => new { l.Id, l.Name, l.Kind, l.ForEveryone, l.EveryoneRole, l.IsReadOnly, Listed = l.Access.Where(a => a.UserId == userId).Select(a => (AccessRole?)a.Role).FirstOrDefault() })
            .ToListAsync(ct);
        return rows
            .Select(r =>
            {
                var role = r.ForEveryone ? r.EveryoneRole : AccessRole.None;
                if (r.Listed is { } listed && listed > role)
                {
                    role = listed;
                }

                return (r.Id, r.Name, r.Kind, r.IsReadOnly && role > AccessRole.Viewer ? AccessRole.Viewer : role);
            })
            .Where(r => r.Item4 > AccessRole.None)
            .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Whether a person may change at least one location (the explorer of the drives then offers "Copy to storage location").</summary>
    public static async Task<bool> CanWriteAnyAsync(AppDbContext db, long userId, CancellationToken ct = default)
        => (await ListForPersonAsync(db, userId, ct)).Any(l => l.Role >= AccessRole.Editor);

    /// <summary>The locations a person can open, for the menu.</summary>
    public static async Task<IReadOnlyList<(long Id, string Name, LocationKind Kind)>> ListForMenuAsync(AppDbContext db, long userId, CancellationToken ct = default)
    {
        var rows = await db.StorageLocations.AsNoTracking()
            .Where(l => l.IsActive && (l.ForEveryone || l.Access.Any(a => a.UserId == userId)))
            .OrderBy(l => l.Name)
            .Select(l => new { l.Id, l.Name, l.Kind })
            .ToListAsync(ct);
        return rows.Select(r => (r.Id, r.Name, r.Kind)).ToList();
    }
}
