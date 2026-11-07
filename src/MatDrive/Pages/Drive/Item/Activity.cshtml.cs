using MatDrive.Data;
using MatDrive.Services.Files;
using Microsoft.AspNetCore.Mvc;

namespace MatDrive.Pages.Drive.Item;

/// <summary>The tab "Activity": what happened with the item (and, for a folder, with everything in it), the last 200 entries.</summary>
public sealed class ActivityModel(AppDbContext db) : ItemPageModel(db)
{
    public IReadOnlyList<ActivityRow> Items { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(long id)
    {
        if (!await LoadAsync(id))
        {
            return NotFound();
        }

        Items = await ActivityLog.OfNodeAsync(Db, Actor.Of(UserId), id, 200) ?? [];
        return Page();
    }
}
