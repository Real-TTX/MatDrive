using MatDrive.Data;
using MatDrive.Pages.Drive.Item;
using MatDrive.Services.Files;
using Microsoft.AspNetCore.Mvc;

namespace MatDrive.Pages.Drive;

/// <summary>The text editor of MatDrive (like MatFile): text and code files up to 5 MB; every save is a version (saves within 10 minutes replace the last one).</summary>
public sealed class EditModel(AppDbContext db) : ItemPageModel(db)
{
    public string Language { get; private set; } = "plain";

    public async Task<IActionResult> OnGetAsync(long id)
    {
        if (!await LoadAsync(id) || IsFolder || Node.InTrash || !FileTypes.IsEditableText(Node.Name, Node.Size))
        {
            return NotFound();
        }

        Language = FileTypes.LanguageOf(Node.Name);
        ViewData["Width"] = "wide";
        return Page();
    }
}
