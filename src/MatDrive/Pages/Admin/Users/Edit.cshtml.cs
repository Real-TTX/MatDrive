using MatDrive.Localization;
using MatDrive.Controls;
using MatDrive.Data;
using MatDrive.Services;
using Microsoft.AspNetCore.Mvc;
using MatDrive.Services.Accounts;

namespace MatDrive.Pages.Admin.Users;

/// <summary>An account: name, address, role, locked or not, a new password. The last active administrator cannot lose the role, be locked or be deleted.</summary>
public sealed class EditModel(AppDbContext db, AccountService accounts, PasswordService passwords, SessionService sessions, MatDrive.Services.Auth.AccountDataService accountData) : AppPageModel
{
    [BindProperty(SupportsGet = true)] public long? Id { get; set; }

    [BindProperty] public string DisplayName { get; set; } = string.Empty;

    [BindProperty] public string Email { get; set; } = string.Empty;

    [BindProperty] public UserRole Role { get; set; } = UserRole.User;

    [BindProperty] public bool IsActive { get; set; } = true;

    [BindProperty] public string Password { get; set; } = string.Empty;

    /// <summary>The space of My Drive in GB; empty = the default of the installation, 0 = no limit.</summary>
    [BindProperty] public string QuotaGb { get; set; } = string.Empty;

    /// <summary>MatIdentity sets the space of My Drive: the field shows its value and cannot be changed here.</summary>
    public bool QuotaFixed { get; private set; }

    /// <summary>The value MatIdentity set, in words ("50 GB", "no limit").</summary>
    public string FixedQuota { get; private set; } = string.Empty;

    public bool IsNew => Id is null;

    public bool IsSelf => Id == UserId;

    public List<Choice> Roles { get; } = [new("User", Text.T("Common.Role.User")), new("Admin", Text.T("Common.Role.Admin"))];

    public async Task<IActionResult> OnGetAsync()
    {
        if (Id is { } id)
        {
            if (await UserAdmin.GetAsync(db, UserId, id, HttpContext.RequestAborted) is not { } user)
            {
                return NotFound();
            }

            DisplayName = user.DisplayName;
            Email = user.Email;
            Role = user.Role;
            IsActive = user.IsActive;
            QuotaGb = user.QuotaBytes is { } q ? (q / (1024L * 1024 * 1024)).ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
            ShowFixed(user);
        }

        return Page();
    }

    private void ShowFixed(UserDetail user)
    {
        QuotaFixed = user.IdentityQuotaSet;
        FixedQuota = user.IdentityQuotaBytes is { } bytes ? Ui.Bytes(bytes) : Text.T("Users.Quota.Unlimited");
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var quota = long.TryParse(QuotaGb, out var gb) && gb >= 0 ? gb * 1024L * 1024 * 1024 : (long?)null;
        if (Id is { } existing && await UserAdmin.GetAsync(db, UserId, existing, HttpContext.RequestAborted) is { IdentityQuotaSet: true } fixedUser)
        {
            // The field is not sent while MatIdentity sets the space: the person's own quota stays as it was.
            quota = fixedUser.QuotaBytes;
            ShowFixed(fixedUser);
        }

        var input = new UserInput(DisplayName, Email, Role, IsActive, Password, quota);
        if (Id is not { } id)
        {
            var (created, errors) = await UserAdmin.CreateAsync(db, accounts, UserId, input, Me.TimeZone, Languages.Current, HttpContext.RequestAborted);
            if (created is null)
            {
                errors.ForEach(e => ModelState.AddModelError(e.Field, e.Message));
                return Page();
            }

            Flash(Text.T("Users.Created", created.Email));
            return LocalRedirect("/Admin/Users");
        }

        var saved = await UserAdmin.UpdateAsync(db, passwords, UserId, id, input, HttpContext.RequestAborted);
        if (!saved.Found || saved.Denied)
        {
            return NotFound();
        }

        if (saved.Errors.Count > 0)
        {
            saved.Errors.ForEach(e => ModelState.AddModelError(e.Field, e.Message));
            return Page();
        }

        if (saved.EndSessions)
        {
            await sessions.RevokeAllAsync(id, IsSelf ? SessionService.CurrentSessionId(HttpContext) : null);
        }

        SessionService.Invalidate();
        Flash(Text.T("Common.Saved"));
        return LocalRedirect("/Admin/Users");
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        switch (await UserAdmin.DeleteAsync(db, sessions, accountData, UserId, Id ?? 0, HttpContext.RequestAborted))
        {
            case UserDeleted.Done:
                Flash(Text.T("Users.Deleted", DisplayName));
                return LocalRedirect("/Admin/Users");
            case UserDeleted.CannotDelete:
                FlashError(Text.T("Users.CannotDelete"));
                return LocalRedirect($"/Admin/Users/Edit?id={Id}");
            default:
                return NotFound();
        }
    }
}
