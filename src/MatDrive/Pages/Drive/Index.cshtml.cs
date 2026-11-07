using MatDrive.Data;
using MatDrive.Localization;
using MatDrive.Services;
using MatDrive.Services.Files;
using MatDrive.Services.Locations;
using MatDrive.Services.Office;
using Microsoft.AspNetCore.Mvc;

namespace MatDrive.Pages.Drive;

/// <summary>
/// The explorer: My Drive (<c>/drive</c>), a folder (<c>/drive/folder/12</c>), a shared drive (<c>/drive/shared-drive/3</c>), shared with me, recent, starred, the
/// trash and the search. One list (table or tiles) with a toolbar above (search in the folder, type, sort, view), selection with actions for many items, and the
/// actions of the folder below. Uploads, previews, menus and dialogs are drive.js / explorer.js; every change goes through <c>/live/drive</c>.
/// </summary>
public sealed class IndexModel(AppDbContext db) : AppPageModel
{
    private const int PageSize = 100;
    private static readonly string[] Sorts = ["name", "modified", "size"];

    [BindProperty(SupportsGet = true)] public string? View { get; set; }

    [BindProperty(SupportsGet = true)] public long? Id { get; set; }

    /// <summary>my, folder, shared-drive, shared, recent, starred, trash, search.</summary>
    public string Mode { get; private set; } = "my";

    public Data.Drive? CurrentDrive { get; private set; }

    public Node? Folder { get; private set; }

    public AccessRole Role { get; private set; }

    public bool CanAdd => Role >= AccessRole.Editor && Mode is "my" or "folder" or "shared-drive";

    public IReadOnlyList<Crumb> Path { get; private set; } = [];

    public NodePage Listing { get; private set; } = new([], 0);

    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public string Title { get; private set; } = string.Empty;

    public IReadOnlyList<OfficeKind> OfficeKinds { get; private set; } = [];

    /// <summary>Whether the person may change a storage location: then items can be copied to one (not from the trash).</summary>
    public bool CanCopyToLocation { get; private set; }

    public string Zone => Me.TimeZone;

    public async Task<IActionResult> OnGetAsync()
    {
        var actor = Actor.Of(UserId);
        var search = ListQuery.Text(Request, "q");
        var type = ListQuery.Text(Request, "type") is { } t && FileTypes.Categories.Contains(t) ? t : null;
        var sort = ListQuery.Text(Request, "sort") is { } s && Sorts.Contains(s) ? s : (View is "recent" or "shared" ? "modified" : "name");
        var desc = ListQuery.Descending(Request, byDefault: sort != "name");
        var page = ListQuery.Page(Request);
        OfficeKinds = await OfficeService.ActiveKindsAsync(db);
        var query = new NodeQuery(search, type, sort, desc, page, PageSize);

        switch (View)
        {
            case null or "" or "my":
                Mode = "my";
                CurrentDrive = await DriveService.MyDriveAsync(db, UserId);
                Role = AccessRole.Owner;
                Title = Text.T("Drive.My");
                Listing = await NodeService.ListFolderAsync(db, actor, CurrentDrive.Id, null, query) ?? new([], 0);
                ViewData["NavKey"] = "my";
                break;
            case "shared-drive" when Id is { } driveId:
                if (await DriveAccess.FindDriveAsync(db, UserId, driveId) is not { } found || found.Drive.Kind != DriveKind.Shared)
                {
                    return NotFound();
                }

                Mode = "shared-drive";
                (CurrentDrive, Role) = found;
                Title = found.Drive.Name;
                Listing = await NodeService.ListFolderAsync(db, actor, driveId, null, query) ?? new([], 0);
                ViewData["NavKey"] = "drive-" + driveId;
                break;
            case "folder" when Id is { } folderId:
                if (await DriveAccess.FindAsync(db, actor, folderId) is not { } access || access.Node.Kind != NodeKind.Folder)
                {
                    return NotFound();
                }

                Mode = "folder";
                Folder = access.Node;
                CurrentDrive = access.Drive;
                Role = access.Role;
                Title = access.Node.Name;
                Path = await NodeService.PathAsync(db, actor, access.Node);
                Listing = await NodeService.ListFolderAsync(db, actor, access.Drive.Id, folderId, query) ?? new([], 0);
                ViewData["NavKey"] = access.Drive.Kind == DriveKind.Shared ? "drive-" + access.Drive.Id
                    : access.Drive.OwnerId == UserId ? "my" : "shared";
                break;
            case "shared":
                Mode = "shared";
                Title = Text.T("Drive.SharedWithMe");
                Listing = await NodeService.SharedWithMeAsync(db, UserId, query);
                ViewData["NavKey"] = "shared";
                break;
            case "recent":
                Mode = "recent";
                Title = Text.T("Drive.Recent");
                Listing = await NodeService.RecentAsync(db, UserId, query);
                ViewData["NavKey"] = "recent";
                break;
            case "starred":
                Mode = "starred";
                Title = Text.T("Drive.Starred");
                Listing = await NodeService.StarredAsync(db, UserId, query);
                ViewData["NavKey"] = "starred";
                break;
            case "trash":
                Mode = "trash";
                Title = Text.T("Drive.Trash");
                Listing = await NodeService.TrashAsync(db, UserId, query);
                ViewData["NavKey"] = "trash";
                break;
            case "search":
                Mode = "search";
                Title = Text.T("Drive.SearchResults");
                Listing = await NodeService.SearchAsync(db, UserId, query);
                ViewData["SearchText"] = search;
                break;
            default:
                return NotFound();
        }

        // the drive of the folder for "New" and uploads (a list without a folder: My Drive)
        CurrentDrive ??= await DriveService.MyDriveAsync(db, UserId);
        if (Mode is not ("my" or "folder" or "shared-drive"))
        {
            Role = AccessRole.Owner;
        }

        CanCopyToLocation = Mode != "trash" && await LocationAccess.CanWriteAnyAsync(db, UserId);

        Toolbar = new ToolbarModel
        {
            Id = "files",
            Action = Request.Path,
            Label = Title,
            Search = search,
            SearchField = true,
            SearchPlaceholder = Text.T(Mode == "search" ? "Drive.Search" : "Drive.SearchHere"),
            Filters = [new FilterField("type", Text.T("Drive.Col.Type"), FileTypes.Categories.Select(c => new FilterOption(c, Text.T("Types." + c))).ToList(), type)],
            SortOptions = [new("name", Text.T("Common.Name")), new("modified", Text.T("Drive.Col.Modified")), new("size", Text.T("Drive.Col.Size"))],
            Sort = sort,
            Descending = desc,
            DefaultSort = View is "recent" or "shared" ? "modified" : "name",
            DefaultDescending = View is "recent" or "shared",
            Views = [new ViewOption("list", Text.T("Lists.View.Table"), "list"), new ViewOption("gallery", Text.T("Lists.View.Gallery"), "grid")],
            Total = Listing.Total,
            Matching = Listing.Total,
            Noun = Text.T("Drive.Items"),
            NounOne = Text.T("Drive.Item"),
        };
        Pager = new PagerModel { Page = page, PageSize = PageSize, Total = Listing.Total, Url = Toolbar.UrlWithPage };
        return Page();
    }

    /// <summary>The address of a row: a folder opens, a file opens its preview (explorer.js) and falls back to its page.</summary>
    public static string HrefOf(NodeRow n) => n.IsFolder ? $"/drive/folder/{n.Id}" : $"/drive/item/{n.Id}";

    /// <summary>Whether MatOffice opens a file (the button "Open with MatOffice").</summary>
    public bool OpensInOffice(NodeRow n) => !n.IsFolder && OfficeService.FormatOf(OfficeKinds, n.Name) is not null;

    /// <summary>Whether a click opens the file in MatOffice (office formats) instead of the preview (text, CSV … that MatOffice can also open).</summary>
    public bool OpensInOfficeByDefault(NodeRow n) => !n.IsFolder && OfficeService.OpensByDefault(OfficeKinds, n.Name);
}
