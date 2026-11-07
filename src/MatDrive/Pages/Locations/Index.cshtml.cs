using MatDrive.Data;
using MatDrive.Localization;
using MatDrive.Services;
using MatDrive.Services.Files;
using MatDrive.Services.Locations;
using Microsoft.AspNetCore.Mvc;

namespace MatDrive.Pages.Locations;

/// <summary>
/// A storage location like the explorer of the drives: <c>/locations/3</c> is the top, <c>/locations/3/photos/2026</c> a folder below it (each segment encoded).
/// The list (table or tiles) has the toolbar above (search in the folder, type, hidden files, sort, view), a selection with actions for many entries, a menu per
/// row, the preview, and the actions of the folder below. Uploads, menus, dialogs and the search below the folder are locations.js; every change goes through
/// <c>/live/locations</c> (business layer).
/// </summary>
public sealed class IndexModel(AppDbContext db, LocationConnections connections) : AppPageModel
{
    private const int PageSize = 200;
    private static readonly string[] Sorts = ["name", "modified", "size"];

    [BindProperty(SupportsGet = true)] public long Id { get; set; }

    [BindProperty(SupportsGet = true, Name = "path")] public string? FolderPath { get; set; }

    public LocationFolder Folder { get; private set; } = null!;

    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public LocationStatus? Status { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public IReadOnlyList<(string Text, string? Url)> Crumbs { get; private set; } = [];

    public string Zone => Me.TimeZone;

    public bool IsAdmin => Me.IsAdmin;

    public bool CanEdit => Folder.Context.CanEdit;

    public StorageLocation Location => Folder.Context.Location;

    /// <summary>The folder as people read it: <c>/photos/2026</c>.</summary>
    public string DisplayPath => PathGuard.Display(Folder.Path);

    public async Task<IActionResult> OnGetAsync()
    {
        var search = ListQuery.Text(Request, "q");
        var type = ListQuery.Text(Request, "type") is { } t && FileTypes.Categories.Contains(t) ? t : null;
        var hidden = ListQuery.Text(Request, "hidden") == "show";
        var sort = ListQuery.Text(Request, "sort") is { } s && Sorts.Contains(s) ? s : "name";
        var desc = ListQuery.Descending(Request, byDefault: sort != "name");
        var page = ListQuery.Page(Request);
        var folder = await LocationService.BrowseAsync(db, connections, UserId, Id, FolderPath, new LocationQuery(search, type, hidden, sort, desc, page, PageSize), HttpContext.RequestAborted);
        if (folder is null)
        {
            return NotFound();
        }

        Folder = folder;
        Status = connections.StatusOf(Id);
        Title = folder.Path.Length == 0 ? folder.Context.Location.Name : PathGuard.NameOf(folder.Path);
        var crumbs = new List<(string, string?)> { (folder.Context.Location.Name, BrowseUrl(Id, string.Empty)) };
        var walked = string.Empty;
        foreach (var segment in folder.Path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            walked = PathGuard.Combine(walked, segment);
            crumbs.Add((segment, BrowseUrl(Id, walked)));
        }

        Crumbs = crumbs;
        ViewData["NavKey"] = "location-" + Id;
        Toolbar = new ToolbarModel
        {
            Id = "location",
            Action = BrowseUrl(Id, folder.Path),
            Label = Title,
            Search = search,
            SearchField = true,
            SearchPlaceholder = Text.T("Locations.SearchHere"),
            Filters =
            [
                new FilterField("type", Text.T("Locations.Col.Type"), FileTypes.Categories.Select(c => new FilterOption(c, Text.T("Locations.Category." + c))).ToList(), type),
                new FilterField("hidden", Text.T("Locations.Hidden"), [new FilterOption("show", Text.T("Locations.Hidden.Show"))], hidden ? "show" : null),
            ],
            SortOptions = [new("name", Text.T("Common.Name")), new("modified", Text.T("Locations.Col.Modified")), new("size", Text.T("Locations.Col.Size"))],
            Sort = sort,
            Descending = desc,
            DefaultSort = "name",
            Views = [new ViewOption("list", Text.T("Lists.View.Table"), "list"), new ViewOption("gallery", Text.T("Lists.View.Gallery"), "grid")],
            Total = folder.Total,
            Matching = folder.Matching,
            Noun = Text.T("Locations.Items"),
            NounOne = Text.T("Locations.Item"),
        };
        Pager = new PagerModel { Page = page, PageSize = PageSize, Total = folder.Matching, Url = Toolbar.UrlWithPage };
        return Page();
    }

    /// <summary>The address of a folder of a location: every segment encoded (<c>/locations/3/My%20photos/2026</c>).</summary>
    public static string BrowseUrl(long id, string path)
    {
        var encoded = string.Join('/', PathGuard.Normalize(path).Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
        return encoded.Length == 0 ? $"/locations/{id}" : $"/locations/{id}/{encoded}";
    }

    /// <summary>The content of a file (inline for a preview, or as a download).</summary>
    public static string ContentUrl(long id, string path, bool download = false)
        => $"/location-files/{id}/content?path={Uri.EscapeDataString(PathGuard.Display(path))}" + (download ? "&download=true" : string.Empty);

    public string HrefOf(LocationEntry e) => e.IsFolder ? BrowseUrl(Id, e.Path) : ContentUrl(Id, e.Path);

    public static string CategoryOf(LocationEntry e) => e.IsFolder ? FileTypes.Folder : FileTypes.CategoryOf(e.Name);

    public static string PreviewOf(LocationEntry e) => e.IsFolder ? "none" : FileTypes.PreviewOf(e.Name, e.Size);

    public static bool IsText(LocationEntry e) => !e.IsFolder && FileTypes.IsEditableText(e.Name, e.Size);

    public string Stamp(DateTime date) => date > DateTime.MinValue.AddDays(1) ? Dates.Stamp(date, Zone) : "–";
}
