using System.Globalization;
using MatDrive.Controls;
using MatDrive.Data;
using MatDrive.Localization;
using MatDrive.Services;
using MatDrive.Services.Files;
using Microsoft.AspNetCore.Mvc;

namespace MatDrive.Pages.Drive.Item;

/// <summary>A link of an item: who may do what (look; for a folder also add files), a password, a date until which it works, whether downloads are allowed. Managers only.</summary>
public sealed class LinkModel(AppDbContext db, PasswordService passwords, InstanceService instance) : ItemPageModel(db)
{
    [BindProperty(SupportsGet = true, Name = "link")] public long? LinkId { get; set; }

    [BindProperty] public AccessRole Role { get; set; } = AccessRole.Viewer;

    [BindProperty] public string Password { get; set; } = string.Empty;

    [BindProperty] public bool ClearPassword { get; set; }

    [BindProperty] public string Expires { get; set; } = string.Empty;

    [BindProperty] public bool AllowDownload { get; set; } = true;

    [BindProperty] public string Note { get; set; } = string.Empty;

    public bool IsNew => LinkId is null;

    public bool HasPassword { get; private set; }

    public string? Address { get; private set; }

    public List<Choice> Roles { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(long id)
    {
        if (!await ReadyAsync(id))
        {
            return NotFound();
        }

        if (LinkId is { } linkId)
        {
            if (await ShareService.FindLinkAsync(Db, Actor.Of(UserId), id, linkId) is not { } link)
            {
                return NotFound();
            }

            Role = link.Role;
            HasPassword = link.HasPassword;
            Expires = link.ExpiresDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;
            AllowDownload = link.AllowDownload;
            Note = link.Note ?? string.Empty;
            var (_, publicUrl) = await instance.SettingsAsync();
            Address = $"{publicUrl ?? $"{Request.Scheme}://{Request.Host}"}/l/{link.Token:N}";
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(long id)
    {
        if (!await ReadyAsync(id))
        {
            return NotFound();
        }

        DateTime? expires = null;
        if (Expires.Length > 0)
        {
            if (!DateOnly.TryParseExact(Expires, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            {
                ModelState.AddModelError(nameof(Expires), Text.T("Links.ExpiresInvalid"));
                return Page();
            }

            expires = Dates.EndOfDayUtc(day, Me.TimeZone);
        }

        var saved = await ShareService.SaveLinkAsync(Db, passwords, Actor.Of(UserId), id, LinkId, new LinkInput(Role, Password, ClearPassword, expires, AllowDownload, Note));
        if (saved.NotAllowed)
        {
            return NotFound();
        }

        if (!saved.Ok)
        {
            ModelState.AddModelError(nameof(Role), saved.Error!);
            return Page();
        }

        Flash(Text.T(IsNew ? "Links.Created" : "Common.Saved"));
        return LocalRedirect($"/drive/item/{id}/link?link={saved.Value!.Id}");
    }

    public async Task<IActionResult> OnPostDeleteAsync(long id)
    {
        if (LinkId is not { } linkId || !await ShareService.DeleteLinkAsync(Db, Actor.Of(UserId), id, linkId))
        {
            return NotFound();
        }

        Flash(Text.T("Links.Deleted"));
        return LocalRedirect($"/drive/item/{id}/sharing");
    }

    private async Task<bool> ReadyAsync(long id)
    {
        if (!await LoadAsync(id, AccessRole.Manager))
        {
            return false;
        }

        ViewData["Tab"] = "sharing";
        Roles = IsFolder
            ? [new(nameof(AccessRole.Viewer), Text.T("Links.Role.View")), new(nameof(AccessRole.Editor), Text.T("Links.Role.Upload"))]
            : [new(nameof(AccessRole.Viewer), Text.T("Links.Role.View"))];
        return true;
    }
}
