using System.Net.Mail;
using MatDrive.Data;
using MatDrive.Localization;
using Microsoft.EntityFrameworkCore;

namespace MatDrive.Services;

public sealed record SignInResult(User? User, string? Error);

/// <summary>A message for one field of a form.</summary>
public sealed record FieldError(string Field, string Message);

/// <summary>Accounts: registering (the first account is the administrator) and signing in with a lockout after failed attempts.</summary>
public sealed class AccountService(AppDbContext db, PasswordService passwords)
{
    private const int MaxFailures = 8;

    public static string Normalize(string? email) => (email ?? string.Empty).Trim().ToLowerInvariant();

    public static bool IsEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email) || email.Length > 254 || !email.Contains('@', StringComparison.Ordinal))
        {
            return false;
        }

        return MailAddress.TryCreate(email, out var parsed) && parsed.Address == email.Trim();
    }

    public async Task<bool> AnyUserAsync() => await db.Users.IgnoreQueryFilters().AnyAsync();

    public Task<SystemSetting> SettingsAsync() => SettingsStore.LoadAsync(db);

    /// <summary>Creates an account. The very first account is an administrator.</summary>
    public async Task<(User? User, FieldError? Error)> RegisterAsync(string email, string displayName, string password, string? timeZone = null, UserRole? role = null, string? language = null)
    {
        email = Normalize(email);
        if (!IsEmail(email))
        {
            return (null, new FieldError("Email", Text.T("Account.Email.Invalid")));
        }

        if (PasswordService.Check(password, email) is { } problem)
        {
            return (null, new FieldError("Password", problem));
        }

        if (await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == email))
        {
            return (null, new FieldError("Email", Text.T("Account.Email.Taken")));
        }

        var first = !await AnyUserAsync();
        var user = new User
        {
            Email = email,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? email.Split('@')[0] : displayName.Trim(),
            PasswordHash = passwords.Hash(password),
            Role = role ?? (first ? UserRole.Admin : UserRole.User),
            TimeZone = Dates.IsZone(timeZone) ? timeZone! : "Europe/Berlin",
            Language = Languages.Normalize(language),
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        await ProvisionAsync(user);
        return (user, null);
    }

    /// <summary>What a new account needs after it was saved: it is its own creator (the audit column could not name it before it had an id).</summary>
    public async Task ProvisionAsync(User user)
    {
        user.CreateUserId ??= user.Id;
        await db.SaveChangesAsync();
    }

    public async Task<SignInResult> SignInAsync(string email, string password)
    {
        email = Normalize(email);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (user is null)
        {
            passwords.SpendTime(password);
            return new SignInResult(null, Text.T("Account.Login.Wrong"));
        }

        var now = DateTime.UtcNow;
        if (user.LockoutUntil is { } until && until > now)
        {
            return new SignInResult(null, Text.T("Account.LockedOut"));
        }

        if (!passwords.Verify(user.PasswordHash, password))
        {
            user.FailedLogins++;
            if (user.FailedLogins >= MaxFailures)
            {
                user.LockoutUntil = now.AddMinutes(10);
                user.FailedLogins = 0;
            }

            await db.SaveChangesAsync();
            return new SignInResult(null, Text.T("Account.Login.Wrong"));
        }

        if (!user.IsActive)
        {
            return new SignInResult(null, Text.T("Account.Login.Inactive"));
        }

        user.FailedLogins = 0;
        user.LockoutUntil = null;
        user.LastLoginDate = now;
        await db.SaveChangesAsync();
        return new SignInResult(user, null);
    }
}
