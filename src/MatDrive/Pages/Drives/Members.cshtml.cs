using MatDrive.Controls;
using MatDrive.Data;
using MatDrive.Localization;
using MatDrive.Services.Files;
using Microsoft.AspNetCore.Mvc;

namespace MatDrive.Pages.Drives;

/// <summary>The members of a shared drive with their roles (Viewer, Editor, Manager); managers add, change and remove them, a member may leave.</summary>
public sealed class MembersModel(AppDbContext db) : AppPageModel
{
    [BindProperty(SupportsGet = true)] public long Id { get; set; }

    [BindProperty] public string Email { get; set; } = string.Empty;

    [BindProperty] public AccessRole NewRole { get; set; } = AccessRole.Editor;

    public string DriveName { get; private set; } = string.Empty;

    public bool CanManage { get; private set; }

    public IReadOnlyList<DriveMemberRow> Members { get; private set; } = [];

    public List<Choice> Roles { get; } =
    [
        new(nameof(AccessRole.Viewer), Text.T("Roles.Viewer")), new(nameof(AccessRole.Editor), Text.T("Roles.Editor")), new(nameof(AccessRole.Manager), Text.T("Roles.Manager")),
    ];

    public async Task<IActionResult> OnGetAsync() => await LoadAsync() ? Page() : NotFound();

    public async Task<IActionResult> OnPostAddAsync()
    {
        var added = await DriveService.AddMemberAsync(db, UserId, Id, Email, NewRole);
        if (added.NotAllowed)
        {
            return NotFound();
        }

        if (!added.Ok)
        {
            ModelState.AddModelError(nameof(Email), added.Error!);
            await LoadAsync();
            return Page();
        }

        Flash(Text.T("Drives.MemberAdded", added.Value!.Name));
        return LocalRedirect($"/drives/members?id={Id}");
    }

    public async Task<IActionResult> OnPostRoleAsync(long memberId, AccessRole role)
    {
        var changed = await DriveService.SetMemberRoleAsync(db, UserId, Id, memberId, role);
        if (changed.NotAllowed)
        {
            return NotFound();
        }

        if (!changed.Ok)
        {
            FlashError(changed.Error!);
        }
        else
        {
            Flash(Text.T("Common.Saved"));
        }

        return LocalRedirect($"/drives/members?id={Id}");
    }

    public async Task<IActionResult> OnPostRemoveAsync(long memberId)
    {
        var removed = await DriveService.RemoveMemberAsync(db, UserId, Id, memberId);
        if (removed.NotAllowed)
        {
            return NotFound();
        }

        if (!removed.Ok)
        {
            FlashError(removed.Error!);
            return LocalRedirect($"/drives/members?id={Id}");
        }

        Flash(Text.T(memberId == UserId ? "Drives.Left" : "Drives.MemberRemoved"));
        return LocalRedirect(memberId == UserId ? "/drives" : $"/drives/members?id={Id}");
    }

    private async Task<bool> LoadAsync()
    {
        if (await DriveAccess.FindDriveAsync(db, UserId, Id) is not { Drive.Kind: DriveKind.Shared } found || await DriveService.MembersAsync(db, UserId, Id) is not { } members)
        {
            return false;
        }

        DriveName = found.Drive.Name;
        CanManage = found.Role >= AccessRole.Manager;
        Members = members;
        ViewData["NavKey"] = "drive-" + Id;
        return true;
    }
}
