using MatDrive.Data;
using MatDrive.Localization;
using MatDrive.Services;
using MatDrive.Services.Files;

namespace MatDrive.Pages.Admin;

/// <summary>The activity of the whole installation for the administrators: who did what, when (names of items, never their content).</summary>
public sealed class ActivityModel(AppDbContext db) : AppPageModel
{
    private const int PageSize = 50;

    public static readonly string[] Actions = ["upload", "version", "create", "rename", "move", "copy", "trash", "restore", "delete", "share", "unshare", "link", "download", "office", "unzip", "drive", "member", "location"];

    public IReadOnlyList<ActivityRow> Items { get; private set; } = [];

    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public string Zone => Me.TimeZone;

    public async Task OnGetAsync()
    {
        var search = ListQuery.Text(Request, "q");
        var action = ListQuery.Text(Request, "action") is { } a && Actions.Contains(a) ? a : null;
        var page = ListQuery.Page(Request);
        var result = await ActivityLog.AllAsync(db, UserId, search, action, page, PageSize) ?? ([], 0);
        Items = result.Rows;
        Toolbar = new ToolbarModel
        {
            Id = "activity",
            Action = "/Admin/Activity",
            Label = Text.T("Activity.Title"),
            Search = search,
            SearchField = true,
            SearchPlaceholder = Text.T("Activity.Search"),
            Filters = [new FilterField("action", Text.T("Activity.Col.What"), Actions.Select(x => new FilterOption(x, Text.T("Activity.Action." + x))).ToList(), action)],
            SortOptions = [new("date", Text.T("Activity.Col.Date"))],
            Sort = "date",
            Descending = true,
            DefaultSort = "date",
            DefaultDescending = true,
            Total = result.Total,
            Matching = result.Total,
            Noun = Text.T("Activity.Many"),
            NounOne = Text.T("Activity.One"),
        };
        Pager = new PagerModel { Page = page, PageSize = PageSize, Total = result.Total, Url = Toolbar.UrlWithPage };
    }
}
