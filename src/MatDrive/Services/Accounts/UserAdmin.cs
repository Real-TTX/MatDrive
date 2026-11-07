using MatDrive.Data;
using MatDrive.Localization;
using MatDrive.Services.Auth;
using Microsoft.EntityFrameworkCore;

namespace MatDrive.Services.Accounts;

public sealed record UserRow(long Id, string DisplayName, string Email, UserRole Role, bool IsActive, bool Locked, DateTime? LastLogin, DateTime Created, long UsedBytes);

/// <summary>What the list of accounts shows and in which order (as it comes from the toolbar).</summary>
public sealed record UserCriteria(string? Search, string? Role, string? State, string Sort, bool Descending, int Page, int PageSize);

public sealed record UserList(List<UserRow> Items, int Total, int Matching);

public sealed record UserDetail(long Id, string DisplayName, string Email, UserRole Role, bool IsActive, long? QuotaBytes);

/// <summary>The form of an account as it comes from the page (not yet checked).</summary>
/// <summary>What the edit page of a user sends; <see cref="QuotaBytes"/> null = the default of the installation, 0 = no limit.</summary>
public sealed record UserInput(string DisplayName, string Email, UserRole Role, bool IsActive, string Password, long? QuotaBytes = null);

/// <summary>The outcome of saving an account: the errors by field, or what the page has to do afterwards.</summary>
public sealed record UserSaved(List<FieldError> Errors, bool Found = true, bool Denied = false, bool EndSessions = false);

public enum UserDeleted
{
    Done,
    NotFound,
    Denied,
    CannotDelete,
}

/// <summary>
/// The accounts of the installation, for administrators. Every function checks in the first line that the actor is an active administrator,
/// whatever the page did before. The last active administrator cannot lose the role, be locked or be deleted. An administrator sees how many
/// space a person uses, never what is in their files (<see cref="MatDrive.Services.Files.DriveAccess"/> gives administrators no access to the files of others).
/// </summary>
public static class UserAdmin
{
    public static async Task<bool> IsAdminAsync(AppDbContext db, long actorId, CancellationToken ct = default)
        => await db.Users.AnyAsync(u => u.Id == actorId && u.Role == UserRole.Admin && u.IsActive, ct);

    /// <summary><c>null</c> = the actor is no administrator.</summary>
    public static async Task<UserList?> ListAsync(AppDbContext db, long actorId, UserCriteria criteria, CancellationToken ct = default)
    {
        if (!await IsAdminAsync(db, actorId, ct))
        {
            return null;
        }

        var now = DateTime.UtcNow;
        var query = db.Users.AsNoTracking();
        if (criteria.Search is not null)
        {
            var s = criteria.Search.ToLower();
            query = query.Where(u => u.DisplayName.ToLower().Contains(s) || u.Email.Contains(s));
        }

        if (criteria.Role == "admin")
        {
            query = query.Where(u => u.Role == UserRole.Admin);
        }
        else if (criteria.Role == "user")
        {
            query = query.Where(u => u.Role == UserRole.User);
        }

        if (criteria.State == "active")
        {
            query = query.Where(u => u.IsActive);
        }
        else if (criteria.State == "disabled")
        {
            query = query.Where(u => !u.IsActive);
        }

        query = (criteria.Sort, criteria.Descending) switch
        {
            ("email", false) => query.OrderBy(u => u.Email),
            ("email", true) => query.OrderByDescending(u => u.Email),
            ("login", false) => query.OrderBy(u => u.LastLoginDate),
            ("login", true) => query.OrderByDescending(u => u.LastLoginDate),
            ("created", false) => query.OrderBy(u => u.CreateDate),
            ("created", true) => query.OrderByDescending(u => u.CreateDate),
            (_, true) => query.OrderByDescending(u => u.DisplayName),
            _ => query.OrderBy(u => u.DisplayName),
        };

        var total = await db.Users.CountAsync(ct);
        var matching = await query.CountAsync(ct);
        var rows = await query.Skip((criteria.Page - 1) * criteria.PageSize).Take(criteria.PageSize)
            .Select(u => new
            {
                u.Id, u.DisplayName, u.Email, u.Role, u.IsActive, u.LockoutUntil, u.LastLoginDate, u.CreateDate,
                UsedBytes = db.Drives.Where(d => d.Kind == DriveKind.Personal && d.OwnerId == u.Id).Sum(d => (long?)d.UsedBytes) ?? 0,
            })
            .ToListAsync(ct);
        var items = rows.Select(u => new UserRow(u.Id, u.DisplayName, u.Email, u.Role, u.IsActive, u.LockoutUntil > now, u.LastLoginDate, u.CreateDate, u.UsedBytes)).ToList();
        return new UserList(items, total, matching);
    }

    /// <summary><c>null</c> = no such account, or the actor is no administrator.</summary>
    public static async Task<UserDetail?> GetAsync(AppDbContext db, long actorId, long id, CancellationToken ct = default)
    {
        if (!await IsAdminAsync(db, actorId, ct))
        {
            return null;
        }

        return await db.Users.AsNoTracking().Where(u => u.Id == id)
            .Select(u => new UserDetail(u.Id, u.DisplayName, u.Email, u.Role, u.IsActive, u.QuotaBytes))
            .FirstOrDefaultAsync(ct);
    }

    public static async Task<(User? Created, List<FieldError> Errors)> CreateAsync(AppDbContext db, AccountService accounts, long actorId, UserInput input, string? timeZone, string? language, CancellationToken ct = default)
    {
        if (!await IsAdminAsync(db, actorId, ct))
        {
            return (null, [new FieldError(string.Empty, Text.T("Common.NotAllowed"))]);
        }

        var (created, error) = await accounts.RegisterAsync(input.Email, input.DisplayName, input.Password, timeZone, input.Role, language);
        if (created is null)
        {
            return (null, [error!]);
        }

        created.IsActive = input.IsActive;
        created.QuotaBytes = input.QuotaBytes is >= 0 ? input.QuotaBytes : null;
        await db.SaveChangesAsync(ct);
        return (created, []);
    }

    public static async Task<UserSaved> UpdateAsync(AppDbContext db, PasswordService passwords, long actorId, long id, UserInput input, CancellationToken ct = default)
    {
        if (!await IsAdminAsync(db, actorId, ct))
        {
            return new UserSaved([], Denied: true);
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return new UserSaved([], Found: false);
        }

        var errors = new List<FieldError>();
        var email = AccountService.Normalize(input.Email);
        if (!AccountService.IsEmail(email))
        {
            errors.Add(new FieldError(nameof(UserInput.Email), Text.T("Account.Email.Invalid")));
        }
        else if (await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == email && u.Id != user.Id, ct))
        {
            errors.Add(new FieldError(nameof(UserInput.Email), Text.T("Account.Email.Taken")));
        }

        if (string.IsNullOrWhiteSpace(input.DisplayName))
        {
            errors.Add(new FieldError(nameof(UserInput.DisplayName), Text.T("Account.Name.Required")));
        }

        if ((input.Role != UserRole.Admin || !input.IsActive) && user.Role == UserRole.Admin && await IsLastAdminAsync(db, user.Id, ct))
        {
            errors.Add(new FieldError(nameof(UserInput.Role), Text.T("Users.LastAdmin")));
        }

        if (!string.IsNullOrEmpty(input.Password) && PasswordService.Check(input.Password, email) is { } problem)
        {
            errors.Add(new FieldError(nameof(UserInput.Password), problem));
        }

        if (errors.Count > 0)
        {
            return new UserSaved(errors);
        }

        user.DisplayName = input.DisplayName.Trim();
        user.Email = email;
        user.Role = Enum.IsDefined(input.Role) ? input.Role : UserRole.User;
        user.IsActive = input.IsActive;
        user.QuotaBytes = input.QuotaBytes is >= 0 ? input.QuotaBytes : null;
        var newPassword = !string.IsNullOrEmpty(input.Password);
        if (newPassword)
        {
            user.PasswordHash = passwords.Hash(input.Password);
            user.LockoutUntil = null;
            user.FailedLogins = 0;
        }

        await db.SaveChangesAsync(ct);
        return new UserSaved([], EndSessions: !input.IsActive || newPassword);
    }

    /// <summary>Deletes an account for good (GDPR Art. 17, the same as when the person deletes it); the sessions end first. Team projects stay with their teams.</summary>
    public static async Task<UserDeleted> DeleteAsync(AppDbContext db, SessionService sessions, AccountDataService accountData, long actorId, long id, CancellationToken ct = default)
    {
        if (!await IsAdminAsync(db, actorId, ct))
        {
            return UserDeleted.Denied;
        }

        var user = await db.Users.AsNoTracking().Where(u => u.Id == id).Select(u => new { u.Id, u.Role }).FirstOrDefaultAsync(ct);
        if (user is null)
        {
            return UserDeleted.NotFound;
        }

        if (user.Id == actorId || (user.Role == UserRole.Admin && await IsLastAdminAsync(db, user.Id, ct)))
        {
            return UserDeleted.CannotDelete;
        }

        await sessions.RevokeAllAsync(user.Id);
        await accountData.EraseAsync(user.Id);
        return UserDeleted.Done;
    }

    private static async Task<bool> IsLastAdminAsync(AppDbContext db, long id, CancellationToken ct)
        => await db.Users.AnyAsync(u => u.Id == id && u.Role == UserRole.Admin && u.IsActive, ct)
           && !await db.Users.AnyAsync(u => u.Id != id && u.Role == UserRole.Admin && u.IsActive, ct);
}
