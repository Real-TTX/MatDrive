using MatDrive.Localization;
using MatDrive.Services;
using MatDrive.Services.Mail;
using Microsoft.AspNetCore.Mvc;

namespace MatDrive.Pages.Admin;

/// <summary>The settings of the installation (registration, public address, space, uploads, trash, versions) and what is in it; the same values as the platform API.</summary>
public sealed class SystemModel(InstanceService instance, MailService mail) : AppPageModel
{
    private const long Gb = 1024L * 1024 * 1024;
    private const long Mb = 1024L * 1024;

    [BindProperty] public bool RegistrationOpen { get; set; }

    [BindProperty] public string PublicUrl { get; set; } = string.Empty;

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
        DefaultQuotaGb = s.DefaultQuotaBytes / Gb;
        MaxUploadMb = s.MaxUploadBytes / Mb;
        TrashDays = s.TrashDays;
        VersionDays = s.VersionDays;
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (await instance.SaveSettingsAsync(RegistrationOpen, PublicUrl, DefaultQuotaGb * Gb, MaxUploadMb * Mb, TrashDays, VersionDays) is { } problem)
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
    }
}
