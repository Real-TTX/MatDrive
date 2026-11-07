using MatDrive.Data;
using MatDrive.Localization;
using MatDrive.Services.Office;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;

namespace MatDrive.Pages.Admin;

/// <summary>
/// The integrations: MatOffice (the editor of the office files, its own container). The administrator enters the address of MatOffice, the API key MatOffice made for
/// MatDrive and, inside Docker, the addresses under which the two reach each other; "Check" asks MatOffice which files it opens.
/// </summary>
public sealed class IntegrationsModel(AppDbContext db, IDataProtectionProvider protection, IHttpClientFactory clients) : AppPageModel
{
    [BindProperty] public bool Enabled { get; set; }

    [BindProperty] public string OfficeUrl { get; set; } = string.Empty;

    [BindProperty] public string InternalUrl { get; set; } = string.Empty;

    [BindProperty] public string Key { get; set; } = string.Empty;

    [BindProperty] public string DriveUrl { get; set; } = string.Empty;

    public bool HasKey { get; private set; }

    public IReadOnlyList<OfficeKind> Kinds { get; private set; } = [];

    public DateTime? CheckedDate { get; private set; }

    public string Zone => Me.TimeZone;

    public string Origin => $"{Request.Scheme}://{Request.Host}";

    public async Task<IActionResult> OnGetAsync()
    {
        if (await OfficeService.SettingsAsync(db, UserId) is not { } s)
        {
            return NotFound();
        }

        Enabled = s.Enabled;
        OfficeUrl = s.Url ?? string.Empty;
        InternalUrl = s.InternalUrl ?? string.Empty;
        DriveUrl = s.DriveUrlForOffice ?? string.Empty;
        Show(s);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var saved = await OfficeService.SaveSettingsAsync(db, protection, UserId, new OfficeInput(Enabled, OfficeUrl, InternalUrl, Key, DriveUrl));
        if (saved.NotAllowed)
        {
            return NotFound();
        }

        if (!saved.Ok)
        {
            ModelState.AddModelError(nameof(OfficeUrl), saved.Error!);
            if (await OfficeService.SettingsAsync(db, UserId) is { } s)
            {
                Show(s);
            }

            return Page();
        }

        // a check right away, so the menus know what MatOffice opens
        if (Enabled)
        {
            var check = await OfficeService.CheckAsync(db, protection, clients.CreateClient("office"), UserId);
            if (check.Ok)
            {
                Flash(Text.T("Office.Checked", check.Value!.Count));
            }
            else
            {
                FlashError(Text.T("Office.SavedNotChecked", check.Error ?? string.Empty));
            }
        }
        else
        {
            Flash(Text.T("Common.Saved"));
        }

        return LocalRedirect("/Admin/Integrations");
    }

    public async Task<IActionResult> OnPostCheckAsync()
    {
        var check = await OfficeService.CheckAsync(db, protection, clients.CreateClient("office"), UserId);
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

        return LocalRedirect("/Admin/Integrations");
    }

    private void Show(OfficeSettings s)
    {
        HasKey = s.HasKey;
        Kinds = s.Kinds;
        CheckedDate = s.CheckedDate;
    }
}
