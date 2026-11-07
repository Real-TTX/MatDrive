using MatDrive.Data;
using MatDrive.Services.Locations;
using Microsoft.AspNetCore.Mvc;

namespace MatDrive.Pages.Locations;

/// <summary>
/// The text editor of a file of a location (<c>/locations/edit/3?path=/notes/todo.md</c>): the text is loaded here, saved by locations.js (Ctrl+S or the button) with
/// the ETag it started from, so a change made meanwhile by somebody else is not overwritten. Viewers and read-only locations see the text without saving.
/// </summary>
public sealed class EditModel(AppDbContext db, LocationConnections connections) : AppPageModel
{
    [BindProperty(SupportsGet = true)] public long Id { get; set; }

    [BindProperty(SupportsGet = true, Name = "path")] public string? FilePath { get; set; }

    public string Text { get; private set; } = string.Empty;

    public string ETag { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public bool CanEdit { get; private set; }

    public string? Error { get; private set; }

    public string Display => PathGuard.Display(FilePath);

    public string FolderUrl => IndexModel.BrowseUrl(Id, PathGuard.Parent(PathGuard.Normalize(FilePath)) ?? string.Empty);

    public IReadOnlyList<(string Text, string? Url)> Crumbs { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        var read = await LocationService.ReadTextAsync(db, connections, UserId, Id, FilePath, HttpContext.RequestAborted);
        if (read.NotAllowed)
        {
            return NotFound();
        }

        var location = await LocationService.OpenAsync(db, connections, UserId, Id, ct: HttpContext.RequestAborted);
        var crumbs = new List<(string, string?)> { (location?.Location.Name ?? string.Empty, IndexModel.BrowseUrl(Id, string.Empty)) };
        var walked = string.Empty;
        var segments = PathGuard.Normalize(FilePath).Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments.Take(segments.Length - 1))
        {
            walked = PathGuard.Combine(walked, segment);
            crumbs.Add((segment, IndexModel.BrowseUrl(Id, walked)));
        }

        Name = PathGuard.NameOf(FilePath);
        crumbs.Add((Name, null));
        Crumbs = crumbs;
        ViewData["NavKey"] = "location-" + Id;
        if (!read.Ok)
        {
            Error = read.Error;
            return Page();
        }

        (Text, ETag, _, CanEdit) = read.Value;
        Name = read.Value.Entry.Name;
        return Page();
    }
}
