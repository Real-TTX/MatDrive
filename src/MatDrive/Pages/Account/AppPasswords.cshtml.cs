using MatDrive.Data;
using MatDrive.Localization;
using MatDrive.Services.Accounts;
using Microsoft.AspNetCore.Mvc;

namespace MatDrive.Pages.Account;

/// <summary>Passwords for devices and programs (WebDAV in the Explorer, Finder, a sync tool): made here, shown once, revoked here.</summary>
public sealed class AppPasswordsModel(AppDbContext db) : AppPageModel
{
    [BindProperty] public string Name { get; set; } = string.Empty;

    public IReadOnlyList<AppPasswordRow> Items { get; private set; } = [];

    public string? NewPassword { get; private set; }

    public string DavAddress => $"{Request.Scheme}://{Request.Host}/dav/";

    public string Email => Me.Snapshot?.Email ?? string.Empty;

    public string Zone => Me.TimeZone;

    public async Task OnGetAsync()
    {
        NewPassword = TempData["AppPassword"] as string;
        Items = await AppPasswords.ListAsync(db, UserId);
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var created = await AppPasswords.CreateAsync(db, UserId, Name);
        if (!created.Ok)
        {
            ModelState.AddModelError(nameof(Name), created.Error!);
            Items = await AppPasswords.ListAsync(db, UserId);
            return Page();
        }

        TempData["AppPassword"] = created.Value;
        Flash(Text.T("AppPasswords.Created"));
        return LocalRedirect("/Account/AppPasswords");
    }

    public async Task<IActionResult> OnPostDeleteAsync(long id)
    {
        if (!await AppPasswords.DeleteAsync(db, UserId, id))
        {
            return NotFound();
        }

        Flash(Text.T("AppPasswords.Deleted"));
        return LocalRedirect("/Account/AppPasswords");
    }
}
