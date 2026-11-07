using MatDrive.Localization;
using MatDrive.Data;
using MatDrive.Services;
using MatDrive.Services.Accounts;

namespace MatDrive.Pages.Admin.Users;

/// <summary>The accounts of the installation. An administrator sees how many models a person owns, never what is on them.</summary>
public sealed class IndexModel(AppDbContext db) : AppPageModel
{
    private const int PageSize = 25;

    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public List<UserRow> Items { get; private set; } = [];

    public string Zone => Me.TimeZone;

    public async Task OnGetAsync()
    {
        var search = ListQuery.Text(Request, "q");
        var role = ListQuery.Text(Request, "role");
        var state = ListQuery.Text(Request, "state");
        var sort = ListQuery.Text(Request, "sort") ?? "name";
        var desc = ListQuery.Descending(Request);
        var page = ListQuery.Page(Request);
        var list = await UserAdmin.ListAsync(db, UserId, new UserCriteria(search, role, state, sort, desc, page, PageSize), HttpContext.RequestAborted)
                   ?? new UserList([], 0, 0);
        Items = list.Items;
        var total = list.Total;
        var matching = list.Matching;

        Toolbar = new ToolbarModel
        {
            Id = "users",
            Action = "/Admin/Users",
            Label = Text.T("Users.Title"),
            Search = search,
            SearchField = true,
            SearchPlaceholder = Text.T("Users.Search"),
            Filters =
            [
                new FilterField("role", Text.T("Users.Col.Role"), [new("admin", Text.T("Common.Role.Admin")), new("user", Text.T("Common.Role.User"))], role),
                new FilterField("state", Text.T("Common.Status"), [new("active", Text.T("Common.Active")), new("disabled", Text.T("Users.Disabled"))], state),
            ],
            SortOptions = [new("name", Text.T("Common.Name")), new("email", Text.T("Account.Email")), new("login", Text.T("Users.Col.LastLogin")), new("created", Text.T("Users.Registered"))],
            Sort = sort,
            Descending = desc,
            DefaultSort = "name",
            Total = total,
            Matching = matching,
            Noun = Text.T("Users.Many"),
            NounOne = Text.T("Users.One"),
        };
        Pager = new PagerModel { Page = page, PageSize = PageSize, Total = matching, Url = Toolbar.UrlWithPage };
    }
}
