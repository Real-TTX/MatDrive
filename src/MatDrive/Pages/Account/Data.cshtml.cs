using MatDrive.Data;
using MatDrive.Localization;
using MatDrive.Services;
using MatDrive.Services.Accounts;
using MatDrive.Services.Auth;
using Microsoft.AspNetCore.Mvc;

namespace MatDrive.Pages.Account;

/// <summary>The data of the person (GDPR): download everything as JSON (Art. 15/20), and delete the account for good (Art. 17) after the password.</summary>
public sealed class DataModel(AppDbContext db, AccountDataService data, PasswordService passwords, SessionService sessions) : AppPageModel
{
    [BindProperty] public string Password { get; set; } = string.Empty;

    public string Email { get; private set; } = string.Empty;

    public async Task OnGetAsync() => Email = (await Profiles.GetAsync(db, UserId, HttpContext.RequestAborted))?.Email ?? string.Empty;

    /// <summary>The export: a file to keep or to take to another app.</summary>
    public async Task<IActionResult> OnGetExportAsync()
    {
        var json = await data.ExportAsync(UserId, HttpContext.RequestAborted);
        return File(json, "application/json", $"matdrive-{Dates.Iso(Today)}.json");
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        if (await Profiles.CheckPasswordAsync(db, passwords, UserId, Password, HttpContext.RequestAborted) is not { } check)
        {
            return NotFound();
        }

        Email = check.Email;
        if (!check.Correct)
        {
            ModelState.AddModelError(nameof(Password), Text.T("Data.Delete.WrongPassword"));
            return Page();
        }

        if (await data.IsLastAdminAsync(UserId))
        {
            ModelState.AddModelError(string.Empty, Text.T("Data.Delete.LastAdmin"));
            return Page();
        }

        var id = UserId;
        await sessions.SignOutAsync(HttpContext);
        await data.EraseAsync(id);
        TempData["Flash"] = Text.T("Data.Delete.Done");
        return LocalRedirect("/Account/Login");
    }
}
