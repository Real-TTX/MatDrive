using MatDrive.Data;
using MatDrive.Localization;
using MatDrive.Services.Files;
using Microsoft.AspNetCore.Mvc;

namespace MatDrive.Pages.Drives;

/// <summary>A shared drive: create one (the person becomes its manager), change its name, description and colour, delete it with everything in it (managers).</summary>
public sealed class EditModel(AppDbContext db, BlobStore store) : AppPageModel
{
    [BindProperty(SupportsGet = true)] public long? Id { get; set; }

    [BindProperty] public string Name { get; set; } = string.Empty;

    [BindProperty] public string Description { get; set; } = string.Empty;

    [BindProperty] public string Colour { get; set; } = string.Empty;

    public bool IsNew => Id is null;

    public AccessRole Role { get; private set; } = AccessRole.Manager;

    public bool CanManage => Role >= AccessRole.Manager;

    public DriveUsage? Usage { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        ViewData["NavKey"] = Id is { } d ? "drive-" + d : null;
        if (Id is not { } id)
        {
            return Page();
        }

        if (await DriveAccess.FindDriveAsync(db, UserId, id) is not { Drive.Kind: DriveKind.Shared } found)
        {
            return NotFound();
        }

        Name = found.Drive.Name;
        Description = found.Drive.Description ?? string.Empty;
        Colour = found.Drive.Colour ?? string.Empty;
        Role = found.Role;
        Usage = await DriveService.UsageAsync(db, found.Drive);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var input = new DriveInput(Name, Description, Colour);
        if (Id is { } id)
        {
            var saved = await DriveService.UpdateSharedAsync(db, UserId, id, input);
            if (saved.NotAllowed)
            {
                return NotFound();
            }

            if (!saved.Ok)
            {
                ModelState.AddModelError(nameof(Name), saved.Error!);
                return Page();
            }

            Flash(Text.T("Common.Saved"));
            return LocalRedirect($"/drive/shared-drive/{id}");
        }

        var created = await DriveService.CreateSharedAsync(db, UserId, input);
        if (!created.Ok)
        {
            ModelState.AddModelError(nameof(Name), created.Error!);
            return Page();
        }

        Flash(Text.T("Drives.Created", created.Value!.Name));
        return LocalRedirect($"/drives/members?id={created.Value.Id}");
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        if (Id is not { } id)
        {
            return NotFound();
        }

        var deleted = await DriveService.DeleteSharedAsync(db, UserId, id);
        if (deleted.NotAllowed)
        {
            return NotFound();
        }

        foreach (var blob in deleted.Value!)
        {
            store.Delete(blob);
        }

        Flash(Text.T("Drives.Deleted", Name));
        return LocalRedirect("/drives");
    }
}
