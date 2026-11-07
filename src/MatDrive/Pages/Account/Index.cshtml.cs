using MatDrive.Controls;
using MatDrive.Data;
using MatDrive.Localization;
using MatDrive.Services;
using MatDrive.Services.Mail;
using Microsoft.AspNetCore.Mvc;
using MatDrive.Services.Accounts;

namespace MatDrive.Pages.Account;

/// <summary>The profile: name, language, time zone, the look, and whether a mail comes when something is shared. The theme button of the menu saves here too (handler Theme).</summary>
public sealed class IndexModel(AppDbContext db, MailService mail) : AppPageModel
{
    [BindProperty] public string DisplayName { get; set; } = string.Empty;

    [BindProperty] public string Language { get; set; } = string.Empty;

    [BindProperty] public string TimeZone { get; set; } = string.Empty;

    [BindProperty] public bool MailOnShare { get; set; } = true;

    [BindProperty] public string Scheme { get; set; } = "standard";

    public string Email { get; private set; } = string.Empty;

    public bool MailAvailable => mail.IsConfigured;

    public List<Choice> Zones { get; } = Dates.ZoneIds.Select(z => new Choice(z, z.Replace('_', ' '))).ToList();

    public List<Choice> LanguageChoices { get; } = [new(string.Empty, Text.T("Common.Language.Auto")), .. Languages.Supported.Select(l => new Choice(l.Code, l.Name))];

    public List<Choice> Schemes { get; } =
    [
        new("standard", Text.T("Settings.Scheme.Standard")), new("paper", Text.T("Settings.Scheme.Paper")),
        new("contrast", Text.T("Settings.Scheme.Contrast")), new("oled", Text.T("Settings.Scheme.Oled")),
    ];

    public async Task<IActionResult> OnGetAsync()
    {
        if (await Profiles.GetAsync(db, UserId, HttpContext.RequestAborted) is not { } user)
        {
            return NotFound();
        }

        DisplayName = user.DisplayName;
        Language = user.Language;
        TimeZone = user.TimeZone;
        MailOnShare = user.MailOnShare;
        Scheme = user.Scheme;
        Email = user.Email;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var input = new ProfileInput(DisplayName, Language, TimeZone, MailOnShare, Scheme);
        var (user, errors) = await Profiles.UpdateAsync(db, UserId, input, mail.IsConfigured, HttpContext.RequestAborted);
        if (user is null)
        {
            Email = (await Profiles.GetAsync(db, UserId))?.Email ?? string.Empty;
            errors.ForEach(e => ModelState.AddModelError(e.Field, e.Message));
            return Page();
        }

        if (user.Language.Length > 0)
        {
            Languages.SetCookie(Response, user.Language, Request.IsHttps);
        }
        else
        {
            Response.Cookies.Delete(Languages.CookieName);
        }

        SessionService.Invalidate();

        // the message is written in the language just chosen
        Flash(user.Language.Length > 0 ? Languages.In(user.Language, () => Text.T("Common.Saved")) : Text.T("Common.Saved"));
        return LocalRedirect("/Account");
    }

    /// <summary>The theme button of the menu (system, light, dark), saved per person.</summary>
    public async Task<IActionResult> OnPostThemeAsync(string theme)
    {
        if (await Profiles.SetThemeAsync(db, UserId, theme))
        {
            SessionService.Invalidate();
        }

        return new NoContentResult();
    }
}
