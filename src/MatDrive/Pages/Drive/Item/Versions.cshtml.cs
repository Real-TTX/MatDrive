using MatDrive.Data;
using MatDrive.Localization;
using MatDrive.Services.Files;
using Microsoft.AspNetCore.Mvc;

namespace MatDrive.Pages.Drive.Item;

/// <summary>The tab "Versions" of a file: every state with who made it and where it came from; download, restore, keep for good, delete (not the current one).</summary>
public sealed class VersionsModel(AppDbContext db, BlobStore store) : ItemPageModel(db)
{
    public IReadOnlyList<VersionRow> Items { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(long id)
    {
        if (!await LoadAsync(id) || IsFolder)
        {
            return NotFound();
        }

        Items = await FileService.VersionsAsync(Db, Actor.Of(UserId), id) ?? [];
        return Page();
    }

    public async Task<IActionResult> OnPostRestoreAsync(long id, long versionId)
    {
        if (!await FileService.RestoreVersionAsync(Db, store, Actor.Of(UserId), id, versionId))
        {
            FlashError(Text.T("Versions.RestoreFailed"));
            return LocalRedirect($"/drive/item/{id}/versions");
        }

        Flash(Text.T("Versions.Restored"));
        return LocalRedirect($"/drive/item/{id}/versions");
    }

    public async Task<IActionResult> OnPostKeepAsync(long id, long versionId, bool keep)
    {
        if (!await FileService.KeepVersionAsync(Db, Actor.Of(UserId), id, versionId, keep))
        {
            return NotFound();
        }

        Flash(Text.T(keep ? "Versions.Kept" : "Versions.NotKept"));
        return LocalRedirect($"/drive/item/{id}/versions");
    }

    public async Task<IActionResult> OnPostDeleteAsync(long id, long versionId)
    {
        if (await FileService.DeleteVersionAsync(Db, Actor.Of(UserId), id, versionId) is not { } blob)
        {
            return NotFound();
        }

        store.Delete(blob);
        Flash(Text.T("Versions.Deleted"));
        return LocalRedirect($"/drive/item/{id}/versions");
    }
}
