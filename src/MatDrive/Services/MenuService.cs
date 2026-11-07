using MatDrive.Data;
using MatDrive.Services.Files;
using MatDrive.Services.Locations;
using MatDrive.Services.Office;
using Microsoft.EntityFrameworkCore;

namespace MatDrive.Services;

/// <summary>What the menu shows: the drive of the person, the shared drives, the storage locations, the space used, what "New" can make.</summary>
public sealed class MenuModel
{
    public long MyDriveId { get; init; }

    public IReadOnlyList<(long Id, string Name, string? Colour)> SharedDrives { get; init; } = [];

    public IReadOnlyList<(long Id, string Name, LocationKind Kind)> Locations { get; init; } = [];

    public DriveUsage Usage { get; init; } = new(0, null);

    public IReadOnlyList<OfficeKind> OfficeKinds { get; init; } = [];

    public int Trash { get; init; }
}

/// <summary>One menu of the switcher at the foot of the menu.</summary>
public sealed record MenuArea(string Id, string Title, string Icon);

public sealed class MenuService(AppDbContext db, CurrentUser me)
{
    private MenuModel? _model;

    /// <summary>The menus of the switcher: the drive, the settings of the person and, for an administrator, the administration.</summary>
    public static IReadOnlyList<MenuArea> Areas(bool admin)
    {
        var list = new List<MenuArea>
        {
            new("drive", Localization.Text.T("Shell.Area.Drive"), "hard-drive"),
            new("settings", Localization.Text.T("Settings.Title"), "settings"),
        };
        if (admin)
        {
            list.Add(new MenuArea("admin", Localization.Text.T("Admin.Title"), "shield"));
        }

        return list;
    }

    /// <summary>The menu a page belongs to, by its address.</summary>
    public static string AreaOf(string? path)
    {
        path ??= "/";
        bool Under(string prefix) => path.Equals(prefix, StringComparison.OrdinalIgnoreCase) || path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);
        if (Under("/Admin"))
        {
            return "admin";
        }

        return Under("/Account") ? "settings" : "drive";
    }

    public async Task<MenuModel> GetAsync()
    {
        if (_model is not null)
        {
            return _model;
        }

        var userId = me.RequiredId;
        var mine = await DriveService.MyDriveAsync(db, userId);
        var shared = await DriveService.SharedDrivesAsync(db, userId);
        var trash = await db.Nodes.CountAsync(n => n.DriveId == mine.Id && n.TrashedDate != null);
        _model = new MenuModel
        {
            MyDriveId = mine.Id,
            SharedDrives = shared.Select(d => (d.Id, d.Name, d.Colour)).ToList(),
            Locations = await LocationAccess.ListForMenuAsync(db, userId),
            Usage = await DriveService.UsageAsync(db, mine),
            OfficeKinds = await OfficeService.ActiveKindsAsync(db),
            Trash = trash,
        };
        return _model;
    }
}
