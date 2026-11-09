using MatDrive.Localization;
using MatDrive.Services;
using MatDrive.Services.Mail;
using Microsoft.AspNetCore.Mvc;

namespace MatDrive.Pages.Admin;

/// <summary>
/// The settings of the installation (registration, public address, space, uploads, trash, versions, the address for other apps) and what is in it; the same values as
/// the platform API. An address a variable sets (<c>MAT_PUBLIC_URL</c>, <c>MAT_INTERNAL_URL</c>) is shown read-only.
/// </summary>
public sealed class SystemModel(InstanceService instance, MailService mail) : AppPageModel
{
    private const long Gb = 1024L * 1024 * 1024;
    private const long Mb = 1024L * 1024;

    [BindProperty] public bool RegistrationOpen { get; set; }

    [BindProperty] public string PublicUrl { get; set; } = string.Empty;

    /// <summary>Where other apps (the office editor) reach this MatDrive, e.g. inside Docker; empty = the public address.</summary>
    [BindProperty] public string AppsUrl { get; set; } = string.Empty;

    /// <summary><c>MAT_PUBLIC_URL</c> sets the public address.</summary>
    public bool PublicUrlFixed { get; private set; }

    /// <summary><c>MAT_INTERNAL_URL</c> sets the address for other apps.</summary>
    public bool AppsUrlFixed { get; private set; }

    [BindProperty] public long DefaultQuotaGb { get; set; }

    [BindProperty] public long MaxUploadMb { get; set; }

    [BindProperty] public int TrashDays { get; set; } = 30;

    [BindProperty] public int VersionDays { get; set; } = 90;

    public InstanceSnapshot Counts { get; private set; } = null!;

    public string Database { get; private set; } = string.Empty;

    /// <summary>Where the mail goes (MatSMTP), or null when the installation sends none.</summary>
    public string? MailText => mail.Describe(Languages.Current);

    public async Task OnGetAsync()
    {
        var s = await instance.StorageSettingsAsync();
        RegistrationOpen = s.RegistrationOpen;
        PublicUrl = s.PublicUrl ?? string.Empty;
        AppsUrl = s.AppsUrl ?? string.Empty;
        DefaultQuotaGb = s.DefaultQuotaBytes / Gb;
        MaxUploadMb = s.MaxUploadBytes / Mb;
        TrashDays = s.TrashDays;
        VersionDays = s.VersionDays;
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        // a fixed address is not posted (the field is read-only) and stays as it is
        if (await instance.SaveSettingsAsync(RegistrationOpen, PublicUrl, DefaultQuotaGb * Gb, MaxUploadMb * Mb, TrashDays, VersionDays, AppsUrl) is { } problem)
        {
            ModelState.AddModelError(problem.Field, problem.Message);
            await LoadAsync();
            return Page();
        }

        Flash(Text.T("Common.Saved"));
        return LocalRedirect("/Admin/System");
    }

    public async Task<IActionResult> OnPostCleanUpAsync()
    {
        var result = await instance.CleanUpAsync(HttpContext.RequestAborted);
        Flash(Text.T("System.CleanedUp", result.Items, result.Versions, result.Uploads));
        return LocalRedirect("/Admin/System");
    }

    private async Task LoadAsync()
    {
        Counts = await instance.SnapshotAsync();
        Database = instance.DatabaseName();
        var s = await instance.StorageSettingsAsync();
        PublicUrlFixed = s.PublicUrlFixed;
        AppsUrlFixed = s.AppsUrlFixed;
        if (PublicUrlFixed)
        {
            PublicUrl = s.PublicUrl ?? string.Empty;
        }

        if (AppsUrlFixed)
        {
            AppsUrl = s.AppsUrl ?? string.Empty;
        }
    }
}
