using MatDrive.Data;
using MatDrive.Localization;
using MatDrive.Services;
using MatDrive.Services.Files;

namespace MatDrive.Pages.Drives;

/// <summary>The shared drives of the person: the drives of teams, with their members and the space they use.</summary>
public sealed class IndexModel(AppDbContext db) : AppPageModel
{
    public IReadOnlyList<DriveRow> Items { get; private set; } = [];

    public ToolbarModel Toolbar { get; private set; } = null!;

    public async Task OnGetAsync()
    {
        var search = ListQuery.Text(Request, "q");
        var role = ListQuery.Text(Request, "role") is "manager" or "editor" or "viewer" ? ListQuery.Text(Request, "role") : null;
        var desc = ListQuery.Descending(Request);
        var sort = ListQuery.Text(Request, "sort") is "used" ? "used" : "name";
        var all = await DriveService.SharedDrivesAsync(db, UserId);
        IEnumerable<DriveRow> rows = all;
        if (search is not null)
        {
            rows = rows.Where(d => d.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase) || (d.Description ?? string.Empty).Contains(search, StringComparison.CurrentCultureIgnoreCase));
        }

        if (role is not null)
        {
            rows = rows.Where(d => d.Role.ToString().Equals(role, StringComparison.OrdinalIgnoreCase));
        }

        rows = sort == "used" ? (desc ? rows.OrderByDescending(d => d.UsedBytes) : rows.OrderBy(d => d.UsedBytes)) : (desc ? rows.OrderByDescending(d => d.Name) : rows.OrderBy(d => d.Name));
        Items = rows.ToList();
        ViewData["NavKey"] = "drives";
        Toolbar = new ToolbarModel
        {
            Id = "drives",
            Action = "/drives",
            Label = Text.T("Drive.SharedDrives"),
            Search = search,
            SearchField = true,
            SearchPlaceholder = Text.T("Drives.Search"),
            Filters = [new FilterField("role", Text.T("Drives.Col.Role"), [new("manager", Text.T("Roles.Manager")), new("editor", Text.T("Roles.Editor")), new("viewer", Text.T("Roles.Viewer"))], role)],
            SortOptions = [new("name", Text.T("Common.Name")), new("used", Text.T("System.Used"))],
            Sort = sort,
            Descending = desc,
            DefaultSort = "name",
            Total = all.Count,
            Matching = Items.Count,
            Noun = Text.T("Drives.Many"),
            NounOne = Text.T("Drives.One"),
        };
    }
}
