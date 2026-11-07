using MatDrive.Controls;
using MatDrive.Data;
using MatDrive.Localization;
using MatDrive.Services;
using MatDrive.Services.Files;
using MatDrive.Services.Mail;
using Microsoft.AspNetCore.Mvc;

namespace MatDrive.Pages.Drive.Item;

/// <summary>The tab "Sharing": the people the item is shared with (add by address, role, remove) and its links (list; a link is edited on a page of its own). Managers only.</summary>
public sealed class SharingModel(AppDbContext db, MailService mail, InstanceService instance) : ItemPageModel(db)
{
    [BindProperty] public string Email { get; set; } = string.Empty;

    [BindProperty] public AccessRole NewRole { get; set; } = AccessRole.Viewer;

    public IReadOnlyList<ShareRow> Shares { get; private set; } = [];

    public IReadOnlyList<LinkRow> Links { get; private set; } = [];

    public IReadOnlyList<DriveMemberRow> DriveMembers { get; private set; } = [];

    public string Origin { get; private set; } = string.Empty;

    public List<Choice> Roles { get; } = [new(nameof(AccessRole.Viewer), Text.T("Roles.Viewer")), new(nameof(AccessRole.Editor), Text.T("Roles.Editor"))];

    public async Task<IActionResult> OnGetAsync(long id) => await ReadyAsync(id) ? Page() : NotFound();

    public async Task<IActionResult> OnPostAddAsync(long id)
    {
        if (!await LoadAsync(id, AccessRole.Manager))
        {
            return NotFound();
        }

        var added = await ShareService.AddAsync(Db, Actor.Of(UserId), id, Email, NewRole);
        if (!added.Ok)
        {
            ModelState.AddModelError(nameof(Email), added.Error ?? Text.T("Drive.NotAllowed"));
            await ReadyAsync(id);
            return Page();
        }

        await TellAsync(added.Value!);
        Flash(Text.T("Item.SharedWith", added.Value!.Name));
        return LocalRedirect($"/drive/item/{id}/sharing");
    }

    public async Task<IActionResult> OnPostRoleAsync(long id, long userId, AccessRole role)
    {
        if (!await ShareService.SetRoleAsync(Db, Actor.Of(UserId), id, userId, role))
        {
            return NotFound();
        }

        Flash(Text.T("Common.Saved"));
        return LocalRedirect($"/drive/item/{id}/sharing");
    }

    public async Task<IActionResult> OnPostRemoveAsync(long id, long userId)
    {
        if (!await ShareService.RemoveAsync(Db, Actor.Of(UserId), id, userId))
        {
            return NotFound();
        }

        Flash(Text.T("Item.Unshared"));
        return LocalRedirect($"/drive/item/{id}/sharing");
    }

    private async Task<bool> ReadyAsync(long id)
    {
        if (!await LoadAsync(id, AccessRole.Manager))
        {
            return false;
        }

        var actor = Actor.Of(UserId);
        Shares = await ShareService.SharesAsync(Db, actor, id) ?? [];
        Links = await ShareService.LinksAsync(Db, actor, id) ?? [];
        if (Access.Drive.Kind == DriveKind.Shared)
        {
            DriveMembers = await DriveService.MembersAsync(Db, UserId, Access.Drive.Id) ?? [];
        }

        var (_, publicUrl) = await instance.SettingsAsync();
        Origin = publicUrl ?? $"{Request.Scheme}://{Request.Host}";
        return true;
    }

    /// <summary>A mail to the person, when the installation can send mail and the person wants it (in their language).</summary>
    private async Task TellAsync(ShareRow person)
    {
        if (!mail.IsConfigured)
        {
            return;
        }

        var (wants, language) = await ShareService.MailPreferenceAsync(Db, person.UserId);
        if (!wants)
        {
            return;
        }

        var (_, publicUrl) = await instance.SettingsAsync();
        var url = (publicUrl ?? $"{Request.Scheme}://{Request.Host}") + (IsFolder ? $"/drive/folder/{Id}" : $"/drive/item/{Id}");
        var sender = Me.DisplayName;
        var name = Node.Name;
        var subject = Languages.In(language, () => Text.T("Mail.Share.Subject", sender, name));
        var text = Languages.In(language, () => Text.T(person.Role == AccessRole.Editor ? "Mail.Share.Editor" : "Mail.Share.Viewer", sender, name, url));
        mail.Enqueue(new OutgoingMail(person.Email, person.Name, subject, text));
    }
}
