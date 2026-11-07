using System.Security.Cryptography;
using System.Text;
using MatDrive.Data;
using MatDrive.Localization;
using Microsoft.EntityFrameworkCore;

namespace MatDrive.Services.Accounts;

public sealed record AppPasswordRow(long Id, string Name, string Prefix, DateTime CreateDate, DateTime? LastUsedDate);

/// <summary>
/// Passwords for devices and programs (WebDAV): made here, shown once, kept as a hash. A person signs in to WebDAV with their address and either an app password
/// or (a local account) their own password.
/// </summary>
public static class AppPasswords
{
    public static async Task<IReadOnlyList<AppPasswordRow>> ListAsync(AppDbContext db, long userId, CancellationToken ct = default)
        => await db.AppPasswords.AsNoTracking().Where(p => p.UserId == userId).OrderByDescending(p => p.CreateDate)
            .Select(p => new AppPasswordRow(p.Id, p.Name, p.Prefix, p.CreateDate, p.LastUsedDate)).ToListAsync(ct);

    /// <summary>Makes a new password; answers it (the only time it can be seen).</summary>
    public static async Task<Outcome<string>> CreateAsync(AppDbContext db, long userId, string name, CancellationToken ct = default)
    {
        var clean = (name ?? string.Empty).Trim();
        if (clean.Length == 0)
        {
            return Outcome<string>.Fail(Text.T("Common.NameRequired"));
        }

        if (await db.AppPasswords.CountAsync(p => p.UserId == userId, ct) >= 50)
        {
            return Outcome<string>.Fail(Text.T("AppPasswords.TooMany"));
        }

        // four groups of five, easy to type into a dialog of an operating system
        const string alphabet = "abcdefghjkmnpqrstuvwxyz23456789";
        var chars = RandomNumberGenerator.GetItems<char>(alphabet, 20);
        var password = string.Join('-', Enumerable.Range(0, 4).Select(i => new string(chars, i * 5, 5)));
        db.AppPasswords.Add(new AppPassword { UserId = userId, Name = clean.Length > 80 ? clean[..80] : clean, Hash = Hash(password), Prefix = password[..5] });
        await db.SaveChangesAsync(ct);
        return Outcome<string>.Success(password);
    }

    public static async Task<bool> DeleteAsync(AppDbContext db, long userId, long id, CancellationToken ct = default)
        => await db.AppPasswords.Where(p => p.Id == id && p.UserId == userId).ExecuteDeleteAsync(ct) > 0;

    /// <summary>The person of an address and an app password (or their own password, for a local account), or null.</summary>
    public static async Task<User?> SignInAsync(AppDbContext db, PasswordService passwords, string email, string password, CancellationToken ct = default)
    {
        var address = AccountService.Normalize(email);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == address && u.IsActive, ct);
        if (user is null || string.IsNullOrEmpty(password))
        {
            passwords.SpendTime(password ?? string.Empty);
            return null;
        }

        if (user.LockoutUntil is { } until && until > DateTime.UtcNow)
        {
            return null;
        }

        var hash = Hash(password.Trim());
        var app = await db.AppPasswords.FirstOrDefaultAsync(p => p.UserId == user.Id && p.Hash == hash, ct);
        if (app is not null)
        {
            if (app.LastUsedDate is null || app.LastUsedDate < DateTime.UtcNow.AddMinutes(-5))
            {
                app.LastUsedDate = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }

            return user;
        }

        return !string.IsNullOrEmpty(user.PasswordHash) && passwords.Verify(user.PasswordHash, password) ? user : null;
    }

    private static string Hash(string password) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(password)));
}
