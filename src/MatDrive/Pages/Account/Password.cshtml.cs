using MatDrive.Data;
using MatDrive.Localization;
using MatDrive.Services;
using MatDrive.Services.Accounts;
using Microsoft.AspNetCore.Mvc;

namespace MatDrive.Pages.Account;

/// <summary>A new password: the current one first. Every other session of the person ends.</summary>
public sealed class PasswordModel(AppDbContext db, PasswordService passwords, SessionService sessions) : AppPageModel
{
    [BindProperty] public string Current { get; set; } = string.Empty;

    [BindProperty] public string New { get; set; } = string.Empty;

    [BindProperty] public string Repeat { get; set; } = string.Empty;

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (await Profiles.ChangePasswordAsync(db, passwords, UserId, Current, New, Repeat, HttpContext.RequestAborted) is { } error)
        {
            ModelState.AddModelError(error.Field, error.Message);
            return Page();
        }

        await sessions.RevokeAllAsync(UserId, SessionService.CurrentSessionId(HttpContext));
        Flash(Text.T("Password.Changed"));
        return LocalRedirect("/Account/Password");
    }
}
