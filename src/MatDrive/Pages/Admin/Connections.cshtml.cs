using MatDrive.Data;
using MatDrive.Localization;
using MatDrive.Services.Office;
using MatPlatform;
using Microsoft.AspNetCore.Mvc;

namespace MatDrive.Pages.Admin;

/// <summary>
/// The connections of MatDrive to other apps (the Mat app model): the Connections area of every app (<c>&lt;mat-connections /&gt;</c> of MatPlatform: what MatDrive uses,
/// how each was set, the form for the values by hand, what it adds to a home screen) and beside it what the bound office editor opens. Mattiverse makes the bindings by
/// itself; without it an administrator enters the values here (the form posts to MatPlatform, which comes back to this page). A binding that is new here gets its
/// formats at once; "Check now" fetches them again.
/// </summary>
public sealed class ConnectionsModel(AppDbContext db, MatBindings bindings, IHttpClientFactory clients) : AppPageModel
{
    public OfficeStatus Office { get; private set; } = new(false, [], null, null);

    public string Zone => Me.TimeZone;

    public async Task OnGetAsync()
    {
        var ct = HttpContext.RequestAborted;
        var office = await bindings.GetAsync(OfficeService.Capability, ct);
        Office = await OfficeService.StatusAsync(db, office, ct);
        if (Office.Bound && Office.CheckedDate is null)
        {
            await OfficeService.RefreshAsync(db, clients.CreateClient("office"), office, force: false, ct);
            Office = await OfficeService.StatusAsync(db, office, ct);
        }
    }

    public async Task<IActionResult> OnPostCheckAsync()
    {
        var office = await bindings.GetAsync(OfficeService.Capability, HttpContext.RequestAborted);
        var check = await OfficeService.CheckAsync(db, clients.CreateClient("office"), office, UserId, HttpContext.RequestAborted);
        if (check.NotAllowed)
        {
            return NotFound();
        }

        if (check.Ok)
        {
            Flash(Text.T("Office.Checked", check.Value!.Count));
        }
        else
        {
            FlashError(check.Error!);
        }

        return LocalRedirect("/Admin/Connections");
    }
}
