using MatDrive.Data;
using MatDrive.Localization;
using Microsoft.EntityFrameworkCore;

namespace MatDrive.Services.Accounts;

/// <summary>What a person can change about themselves.</summary>
public sealed record Profile(string Email, string DisplayName, string Language, string TimeZone, bool MailOnShare, string Scheme);

/// <summary>The form of the profile as it comes from the page (not yet checked).</summary>
public sealed record ProfileInput(string DisplayName, string Language, string TimeZone, bool MailOnShare, string Scheme);

/// <summary>
/// The own account of a person: profile, theme, password. Every function takes the id of the person and touches only their own record,
/// so nobody can reach the data of someone else through it (an administrator edits other accounts through <see cref="UserAdmin"/>).
/// </summary>
public static class Profiles
{
    public static async Task<Profile?> GetAsync(AppDbContext db, long userId, CancellationToken ct = default)
        => await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new Profile(u.Email, u.DisplayName, u.Language, u.TimeZone, u.MailOnShare, u.Scheme))
            .FirstOrDefaultAsync(ct);

    /// <summary>Saves the profile. The errors name the field of the form; no profile and no errors means the account is gone.</summary>
    public static async Task<(Profile? Saved, List<FieldError> Errors)> UpdateAsync(AppDbContext db, long userId, ProfileInput input, bool mailConfigured, CancellationToken ct = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            return (null, []);
        }

        var errors = new List<FieldError>();
        if (string.IsNullOrWhiteSpace(input.DisplayName))
        {
            errors.Add(new FieldError(nameof(ProfileInput.DisplayName), Text.T("Account.Name.Required")));
        }

        if (!Dates.IsZone(input.TimeZone))
        {
            errors.Add(new FieldError(nameof(ProfileInput.TimeZone), Text.T("Settings.TimeZone.Invalid")));
        }

        if (errors.Count > 0)
        {
            return (null, errors);
        }

        var name = input.DisplayName.Trim();
        user.DisplayName = name.Length > 120 ? name[..120] : name;
        user.Language = Languages.IsSupported(input.Language) ? Languages.Normalize(input.Language) : string.Empty;
        user.TimeZone = input.TimeZone;
        if (mailConfigured)
        {
            user.MailOnShare = input.MailOnShare;
        }

        user.Scheme = input.Scheme is "paper" or "contrast" or "oled" ? input.Scheme : "standard";
        await db.SaveChangesAsync(ct);
        return (new Profile(user.Email, user.DisplayName, user.Language, user.TimeZone, user.MailOnShare, user.Scheme), []);
    }

    /// <summary>The theme button of the menu (system, light, dark).</summary>
    public static async Task<bool> SetThemeAsync(AppDbContext db, long userId, string theme, CancellationToken ct = default)
    {
        if (theme is not ("system" or "light" or "dark"))
        {
            return false;
        }

        await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.Theme, theme), ct);
        return true;
    }

    /// <summary>Checks the password of the person (before something that cannot be undone). Returns the address of the account, or <c>null</c> when it is gone.</summary>
    public static async Task<(string Email, bool Correct)?> CheckPasswordAsync(AppDbContext db, PasswordService passwords, long userId, string password, CancellationToken ct = default)
    {
        var user = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => new { u.Email, u.PasswordHash }).FirstOrDefaultAsync(ct);
        return user is null ? null : (user.Email, passwords.Verify(user.PasswordHash, password));
    }

    /// <summary>A new password: the current one first. <c>null</c> = done; else the field and the message.</summary>
    public static async Task<FieldError?> ChangePasswordAsync(AppDbContext db, PasswordService passwords, long userId, string current, string next, string repeat, CancellationToken ct = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            return new FieldError("Current", Text.T("Password.Current.Wrong"));
        }

        if (!passwords.Verify(user.PasswordHash, current))
        {
            return new FieldError("Current", Text.T("Password.Current.Wrong"));
        }

        if (PasswordService.Check(next, user.Email) is { } problem)
        {
            return new FieldError("New", problem);
        }

        if (next != repeat)
        {
            return new FieldError("Repeat", Text.T("Password.Mismatch"));
        }

        user.PasswordHash = passwords.Hash(next);
        await db.SaveChangesAsync(ct);
        return null;
    }
}

