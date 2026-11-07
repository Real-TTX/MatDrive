using MatDrive.Data;
using MatDrive.Localization;
using MatDrive.Services.Files;
using Microsoft.AspNetCore.Mvc;

namespace MatDrive.Pages.Drive.Item;

/// <summary>The tab "General" of a file or folder: name, description, colour (folders) and what it is; open, download, move to the trash (or restore it).</summary>
public sealed class IndexModel(AppDbContext db, BlobStore store) : ItemPageModel(db)
{
    [BindProperty] public string Name { get; set; } = string.Empty;

    [BindProperty] public string Description { get; set; } = string.Empty;

    [BindProperty] public string Colour { get; set; } = string.Empty;

    public int Shares { get; private set; }

    public int Links { get; private set; }

    public int Versions { get; private set; }

    public long? ItemsInside { get; private set; }

    public string? OwnerName { get; private set; }

    public async Task<IActionResult> OnGetAsync(long id)
    {
        if (!await LoadAsync(id))
        {
            return NotFound();
        }

        Name = Node.Name;
        Description = Node.Description ?? string.Empty;
        Colour = Node.Colour ?? string.Empty;
        await CountAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(long id)
    {
        if (!await LoadAsync(id, AccessRole.Editor))
        {
            return NotFound();
        }

        var actor = Services.Files.Actor.Of(UserId);
        if (Name != Node.Name)
        {
            var renamed = await NodeService.RenameAsync(Db, actor, id, Name);
            if (!renamed.Ok)
            {
                ModelState.AddModelError(nameof(Name), renamed.Error ?? Text.T("Drive.NotAllowed"));
                await CountAsync();
                return Page();
            }
        }

        await NodeService.UpdateDetailsAsync(Db, actor, id, Description, Colour);
        Flash(Text.T("Common.Saved"));
        return LocalRedirect($"/drive/item/{id}");
    }

    public async Task<IActionResult> OnPostDeleteAsync(long id)
    {
        if (!await LoadAsync(id, AccessRole.Editor))
        {
            return NotFound();
        }

        await NodeService.TrashAsync(Db, Services.Files.Actor.Of(UserId), [id]);
        Flash(Text.T("Drive.Trashed", 1));
        return LocalRedirect(FolderUrl);
    }

    public async Task<IActionResult> OnPostRestoreAsync(long id)
    {
        if (!await LoadAsync(id, AccessRole.Editor))
        {
            return NotFound();
        }

        await NodeService.RestoreAsync(Db, Services.Files.Actor.Of(UserId), [id]);
        Flash(Text.T("Drive.Restored", 1));
        return LocalRedirect($"/drive/item/{id}");
    }

    public async Task<IActionResult> OnPostDeleteForeverAsync(long id)
    {
        if (!await LoadAsync(id, AccessRole.Editor))
        {
            return NotFound();
        }

        foreach (var blob in await NodeService.DeleteForeverAsync(Db, Services.Files.Actor.Of(UserId), [id]))
        {
            store.Delete(blob);
        }

        Flash(Text.T("Drive.Deleted"));
        return LocalRedirect("/drive/trash");
    }

    private async Task CountAsync()
    {
        var actor = Services.Files.Actor.Of(UserId);
        Shares = (await ShareService.SharesAsync(Db, actor, Id))?.Count ?? 0;
        Links = Access.CanShare ? (await ShareService.LinksAsync(Db, actor, Id))?.Count ?? 0 : 0;
        Versions = IsFolder ? 0 : (await FileService.VersionsAsync(Db, actor, Id))?.Count ?? 0;
        if (IsFolder)
        {
            ItemsInside = (await NodeService.ListFolderAsync(Db, actor, Node.DriveId, Id, new NodeQuery(null, null, "name", false, 1, 1)))?.Total;
        }

        OwnerName = await Services.Files.NodeInfo.OwnerNameAsync(Db, Node.OwnerId);
    }
}
