using MatDrive.Data;
using MatDrive.Localization;
using MatDrive.Services;
using MatDrive.Services.Files;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatDrive.Pages.Public;

/// <summary>
/// A shared link (<c>/l/&lt;secret&gt;</c>), for anybody who has it: a password first when the link has one; a file is shown (preview) and downloaded; a folder is browsed
/// (<c>?f=</c> a folder below it), downloaded as zip, and – with the right to add – takes uploads. A link that is wrong, off or expired answers 404.
/// </summary>
public sealed class LinkModel(AppDbContext db, PasswordService passwords, IDataProtectionProvider protection, CurrentUser me) : PageModel
{
    [BindProperty(SupportsGet = true)] public Guid Token { get; set; }

    [BindProperty(SupportsGet = true, Name = "f")] public long? FolderId { get; set; }

    [BindProperty] public string Password { get; set; } = string.Empty;

    public OpenedLink Link { get; private set; } = null!;

    public bool Locked { get; private set; }

    public Node Current { get; private set; } = null!;

    public IReadOnlyList<Crumb> Path { get; private set; } = [];

    public NodePage Listing { get; private set; } = new([], 0);

    public string Base => "/l/" + Token.ToString("N");

    public bool CanUpload => Link.Link.Role >= AccessRole.Editor && Link.Node.Kind == NodeKind.Folder;

    public string Zone => me.TimeZone;

    public async Task<IActionResult> OnGetAsync()
    {
        if (await ShareService.OpenLinkAsync(db, Token) is not { } link)
        {
            return NotFound();
        }

        Link = link;
        Response.Headers.CacheControl = "no-store";
        Response.Headers["X-Robots-Tag"] = "noindex";
        if (!LinkGate.IsOpen(HttpContext, protection, link.Link))
        {
            Locked = true;
            return Page();
        }

        return await ShowAsync() ? Page() : NotFound();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (await ShareService.OpenLinkAsync(db, Token) is not { } link)
        {
            return NotFound();
        }

        Link = link;
        if (link.Link.PasswordHash is null || passwords.Verify(link.Link.PasswordHash, Password))
        {
            LinkGate.Open(HttpContext, protection, link.Link);
            return LocalRedirect(Base);
        }

        passwords.SpendTime(Password);
        Locked = true;
        ModelState.AddModelError(nameof(Password), Text.T("Links.PasswordWrong"));
        return Page();
    }

    private async Task<bool> ShowAsync()
    {
        var actor = Link.ActorFor(null);
        var id = FolderId ?? Link.Node.Id;
        var access = await DriveAccess.FindAsync(db, actor, id);
        if (access is null)
        {
            return false;
        }

        Current = access.Node;
        Path = await NodeService.PathAsync(db, actor, access.Node);
        if (Current.Kind == NodeKind.Folder)
        {
            Listing = await NodeService.ListFolderAsync(db, actor, Current.DriveId, Current.Id, new NodeQuery(null, null, "name", false, 1, 1000)) ?? new([], 0);
        }

        if (FolderId is null)
        {
            await ShareService.CountAsync(db, Link.Link.Id, false);
        }

        return true;
    }
}
