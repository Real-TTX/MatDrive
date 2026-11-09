using MatDrive.Data;
using MatDrive.Localization;
using MatDrive.Services.Files;
using MatDrive.Services.Office;
using MatPlatform;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;

namespace MatDrive.Pages.Office;

/// <summary>Opens a file in the office editor: starts the session there and goes to the editor; when that does not work, says why (not reachable, file type not supported …).</summary>
public sealed class OpenModel(AppDbContext db, IDataProtectionProvider protection, IHttpClientFactory clients, MatBindings bindings, MatPlatformSettings platform) : AppPageModel
{
    public string Problem { get; private set; } = string.Empty;

    public long Id { get; private set; }

    public async Task<IActionResult> OnGetAsync(long id)
    {
        Id = id;
        var office = await bindings.GetAsync(OfficeService.Capability, HttpContext.RequestAborted);
        var opened = await OfficeService.OpenAsync(db, protection, clients.CreateClient("office"), office, platform, Actor.Of(UserId), id, $"{Request.Scheme}://{Request.Host}",
            Languages.Current, HttpContext.RequestAborted);
        if (opened.NotAllowed)
        {
            return NotFound();
        }

        if (opened.Ok)
        {
            return Redirect(opened.Value!);
        }

        Problem = opened.Error ?? Text.T("Office.Unreachable");
        return Page();
    }
}
