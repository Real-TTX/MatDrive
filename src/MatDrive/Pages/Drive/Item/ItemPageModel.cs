using MatDrive.Data;
using MatDrive.Services.Files;
using MatDrive.Services.Office;

namespace MatDrive.Pages.Drive.Item;

/// <summary>What every tab of an item needs: the item with the role of the person, its path and the crumbs of the page.</summary>
public abstract class ItemPageModel(AppDbContext db) : AppPageModel
{
    protected AppDbContext Db => db;

    public long Id { get; protected set; }

    public NodeAccess Access { get; protected set; } = null!;

    public Node Node => Access.Node;

    public IReadOnlyList<Crumb> Path { get; protected set; } = [];

    public bool IsFolder => Node.Kind == NodeKind.Folder;

    public string Category => IsFolder ? FileTypes.Folder : FileTypes.CategoryOf(Node.Name, Node.MimeType);

    public bool OpensInOffice { get; protected set; }

    /// <summary>Where the item lies: its folder, else the top of its drive.</summary>
    public string FolderUrl => Node.ParentId is { } p && Path.Count > 1 ? $"/drive/folder/{p}"
        : Access.Drive.Kind == DriveKind.Shared && Access.Role >= AccessRole.Viewer ? $"/drive/shared-drive/{Access.Drive.Id}"
        : Access.Drive.OwnerId == Me.Id ? "/drive" : "/drive/shared";

    public string Zone => Me.TimeZone;

    /// <summary>Loads the item (also from the trash); false = 404.</summary>
    protected async Task<bool> LoadAsync(long id, AccessRole minimum = AccessRole.Viewer)
    {
        Id = id;
        var access = await DriveAccess.FindAsync(db, Actor.Of(UserId), id, minimum, trashed: true, ct: HttpContext.RequestAborted);
        if (access is null)
        {
            return false;
        }

        Access = access;
        Path = await NodeService.PathAsync(db, Actor.Of(UserId), access.Node, HttpContext.RequestAborted);
        OpensInOffice = access.Node.Kind == NodeKind.File && OfficeService.FormatOf(await OfficeService.ActiveKindsAsync(db), access.Node.Name) is not null;
        ViewData["Title"] = access.Node.Name;
        var crumbs = new List<(string, string?)>
        {
            access.Drive.Kind == DriveKind.Shared ? (access.Drive.Name, $"/drive/shared-drive/{access.Drive.Id}")
                : access.Drive.OwnerId == UserId ? (Localization.Text.T("Drive.My"), "/drive") : (Localization.Text.T("Drive.SharedWithMe"), "/drive/shared"),
        };
        crumbs.AddRange(Path.Select(c => (c.Name, (string?)(c.Id == id ? null : $"/drive/folder/{c.Id}"))));
        ViewData["Crumbs"] = crumbs;
        ViewData["NavKey"] = access.Drive.Kind == DriveKind.Shared ? "drive-" + access.Drive.Id : access.Drive.OwnerId == UserId ? "my" : "shared";
        return true;
    }
}
