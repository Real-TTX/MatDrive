using MatDrive.Data;
using MatDrive.Localization;
using Microsoft.EntityFrameworkCore;

namespace MatDrive.Services.Files;

public sealed record DriveRow(long Id, DriveKind Kind, string Name, string? Colour, string? Description, AccessRole Role, int Members, long UsedBytes, long? QuotaBytes, DateTime CreateDate);

public sealed record DriveMemberRow(long UserId, string Name, string Email, AccessRole Role);

/// <summary>What the edit page of a shared drive sends.</summary>
public sealed record DriveInput(string Name, string? Description, string? Colour);

/// <summary>The space a drive has and uses.</summary>
public sealed record DriveUsage(long Used, long? Quota)
{
    public long? Free => Quota is { } q && q > 0 ? Math.Max(0, q - Used) : null;

    public double Share => Quota is { } q && q > 0 ? Math.Min(1, (double)Used / q) : 0;
}

/// <summary>The drives: "My Drive" of every person (made on first use) and the shared drives of teams with their members.</summary>
public static class DriveService
{
    /// <summary>The personal drive of a person; made when it does not exist yet.</summary>
    public static async Task<Drive> MyDriveAsync(AppDbContext db, long userId, CancellationToken ct = default)
    {
        var drive = await db.Drives.FirstOrDefaultAsync(d => d.Kind == DriveKind.Personal && d.OwnerId == userId, ct);
        if (drive is not null)
        {
            return drive;
        }

        drive = new Drive { Kind = DriveKind.Personal, OwnerId = userId, Name = "My Drive", CreateUserId = userId };
        db.Drives.Add(drive);
        await db.SaveChangesAsync(ct);
        return drive;
    }

    /// <summary>The shared drives of a person (or every shared drive for an administrator who asks for <paramref name="all"/>).</summary>
    public static async Task<IReadOnlyList<DriveRow>> SharedDrivesAsync(AppDbContext db, long userId, CancellationToken ct = default)
    {
        var rows = await db.Drives.AsNoTracking()
            .Where(d => d.Kind == DriveKind.Shared && d.Members.Any(m => m.UserId == userId))
            .Select(d => new { d.Id, d.Kind, d.Name, d.Colour, d.Description, Role = d.Members.Where(m => m.UserId == userId).Select(m => m.Role).FirstOrDefault(), Members = d.Members.Count, d.UsedBytes, d.QuotaBytes, d.CreateDate })
            .ToListAsync(ct);
        return rows.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(r => new DriveRow(r.Id, r.Kind, r.Name, r.Colour, r.Description, r.Role, r.Members, r.UsedBytes, r.QuotaBytes, r.CreateDate)).ToList();
    }

    public static async Task<Outcome<Drive>> CreateSharedAsync(AppDbContext db, long userId, DriveInput input, CancellationToken ct = default)
    {
        var error = Check(input);
        if (error is not null)
        {
            return Outcome<Drive>.Fail(error);
        }

        var drive = new Drive { Kind = DriveKind.Shared, Name = input.Name.Trim(), Description = Clean(input.Description), Colour = Colour(input.Colour) };
        drive.Members.Add(new DriveMember { UserId = userId, Role = AccessRole.Manager });
        db.Drives.Add(drive);
        ActivityLog.Add(db, userId, "drive", detail: "created", target: drive.Name);
        await db.SaveChangesAsync(ct);
        return Outcome<Drive>.Success(drive);
    }

    public static async Task<Outcome<bool>> UpdateSharedAsync(AppDbContext db, long userId, long driveId, DriveInput input, CancellationToken ct = default)
    {
        if (await DriveAccess.FindDriveAsync(db, userId, driveId, AccessRole.Manager, ct) is not { Drive.Kind: DriveKind.Shared })
        {
            return Outcome<bool>.Denied();
        }

        var error = Check(input);
        if (error is not null)
        {
            return Outcome<bool>.Fail(error);
        }

        var drive = await db.Drives.FirstAsync(d => d.Id == driveId, ct);
        drive.Name = input.Name.Trim();
        drive.Description = Clean(input.Description);
        drive.Colour = Colour(input.Colour);
        await db.SaveChangesAsync(ct);
        return Outcome<bool>.Success(true);
    }

    /// <summary>Deletes a shared drive with everything in it (managers; the drive must be empty unless <paramref name="withContent"/>).</summary>
    public static async Task<Outcome<IReadOnlyList<Guid>>> DeleteSharedAsync(AppDbContext db, long userId, long driveId, CancellationToken ct = default)
    {
        if (await DriveAccess.FindDriveAsync(db, userId, driveId, AccessRole.Manager, ct) is not { Drive.Kind: DriveKind.Shared } found)
        {
            return Outcome<IReadOnlyList<Guid>>.Denied();
        }

        var nodeIds = db.Nodes.IgnoreQueryFilters().Where(n => n.DriveId == driveId).Select(n => n.Id);
        var blobs = await db.NodeVersions.Where(v => nodeIds.Contains(v.NodeId)).Select(v => v.BlobId).ToListAsync(ct);
        await db.Nodes.IgnoreQueryFilters().Where(n => n.DriveId == driveId).ExecuteDeleteAsync(ct);
        await db.Drives.Where(d => d.Id == driveId).ExecuteDeleteAsync(ct);
        await ActivityLog.WriteAsync(db, userId, "drive", detail: "deleted", target: found.Drive.Name, ct: ct);
        return Outcome<IReadOnlyList<Guid>>.Success(blobs);
    }

    public static async Task<IReadOnlyList<DriveMemberRow>?> MembersAsync(AppDbContext db, long userId, long driveId, CancellationToken ct = default)
    {
        if (await DriveAccess.FindDriveAsync(db, userId, driveId, ct: ct) is not { Drive.Kind: DriveKind.Shared })
        {
            return null;
        }

        var rows = await db.DriveMembers.AsNoTracking().Where(m => m.DriveId == driveId).Select(m => new { m.UserId, m.User!.DisplayName, m.User.Email, m.Role }).ToListAsync(ct);
        return rows.OrderByDescending(r => r.Role).ThenBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase).Select(r => new DriveMemberRow(r.UserId, r.DisplayName, r.Email, r.Role)).ToList();
    }

    public static async Task<Outcome<DriveMemberRow>> AddMemberAsync(AppDbContext db, long userId, long driveId, string email, AccessRole role, CancellationToken ct = default)
    {
        if (await DriveAccess.FindDriveAsync(db, userId, driveId, AccessRole.Manager, ct) is not { Drive.Kind: DriveKind.Shared })
        {
            return Outcome<DriveMemberRow>.Denied();
        }

        if (role is not (AccessRole.Viewer or AccessRole.Editor or AccessRole.Manager))
        {
            return Outcome<DriveMemberRow>.Fail(Text.T("Members.RoleInvalid"));
        }

        var address = AccountService.Normalize(email);
        var person = await db.Users.AsNoTracking().Where(u => u.Email == address && u.IsActive).Select(u => new { u.Id, u.DisplayName, u.Email }).FirstOrDefaultAsync(ct);
        if (person is null)
        {
            return Outcome<DriveMemberRow>.Fail(Text.T("Members.NoAccount"));
        }

        if (await db.DriveMembers.AnyAsync(m => m.DriveId == driveId && m.UserId == person.Id, ct))
        {
            return Outcome<DriveMemberRow>.Fail(Text.T("Members.AlreadyMember"));
        }

        db.DriveMembers.Add(new DriveMember { DriveId = driveId, UserId = person.Id, Role = role });
        ActivityLog.Add(db, userId, "member", detail: person.DisplayName + " · " + role, driveId: driveId);
        await db.SaveChangesAsync(ct);
        return Outcome<DriveMemberRow>.Success(new DriveMemberRow(person.Id, person.DisplayName, person.Email, role));
    }

    /// <summary>Changes the role of a member; the last manager stays a manager.</summary>
    public static async Task<Outcome<bool>> SetMemberRoleAsync(AppDbContext db, long userId, long driveId, long memberId, AccessRole role, CancellationToken ct = default)
    {
        if (await DriveAccess.FindDriveAsync(db, userId, driveId, AccessRole.Manager, ct) is not { Drive.Kind: DriveKind.Shared } || role is not (AccessRole.Viewer or AccessRole.Editor or AccessRole.Manager))
        {
            return Outcome<bool>.Denied();
        }

        var member = await db.DriveMembers.FirstOrDefaultAsync(m => m.DriveId == driveId && m.UserId == memberId, ct);
        if (member is null)
        {
            return Outcome<bool>.Denied();
        }

        if (member.Role == AccessRole.Manager && role != AccessRole.Manager && !await db.DriveMembers.AnyAsync(m => m.DriveId == driveId && m.UserId != memberId && m.Role == AccessRole.Manager, ct))
        {
            return Outcome<bool>.Fail(Text.T("Drives.LastManager"));
        }

        member.Role = role;
        await db.SaveChangesAsync(ct);
        return Outcome<bool>.Success(true);
    }

    /// <summary>Takes a member out (a manager, or the member leaves); the last manager cannot go.</summary>
    public static async Task<Outcome<bool>> RemoveMemberAsync(AppDbContext db, long userId, long driveId, long memberId, CancellationToken ct = default)
    {
        var minimum = memberId == userId ? AccessRole.Viewer : AccessRole.Manager;
        if (await DriveAccess.FindDriveAsync(db, userId, driveId, minimum, ct) is not { Drive.Kind: DriveKind.Shared })
        {
            return Outcome<bool>.Denied();
        }

        var member = await db.DriveMembers.FirstOrDefaultAsync(m => m.DriveId == driveId && m.UserId == memberId, ct);
        if (member is null)
        {
            return Outcome<bool>.Denied();
        }

        if (member.Role == AccessRole.Manager && !await db.DriveMembers.AnyAsync(m => m.DriveId == driveId && m.UserId != memberId && m.Role == AccessRole.Manager, ct))
        {
            return Outcome<bool>.Fail(Text.T("Drives.LastManager"));
        }

        db.DriveMembers.Remove(member);
        await db.SaveChangesAsync(ct);
        return Outcome<bool>.Success(true);
    }

    /// <summary>The space a drive uses and may use (the quota of a personal drive is its person's, else the default of the installation).</summary>
    public static async Task<DriveUsage> UsageAsync(AppDbContext db, Drive drive, CancellationToken ct = default)
    {
        long? quota = drive.QuotaBytes;
        if (drive.Kind == DriveKind.Personal && drive.OwnerId is { } owner)
        {
            var own = await db.Users.AsNoTracking().Where(u => u.Id == owner).Select(u => u.QuotaBytes).FirstOrDefaultAsync(ct);
            quota = own ?? (await db.SystemSettings.AsNoTracking().Where(s => s.Id == 1).Select(s => (long?)s.DefaultQuotaBytes).FirstOrDefaultAsync(ct) ?? new SystemSetting().DefaultQuotaBytes);
        }

        return new DriveUsage(drive.UsedBytes, quota is > 0 ? quota : null);
    }

    /// <summary>Changes the used space of a drive by <paramref name="delta"/> bytes (in the database, so two uploads never lose a number).</summary>
    public static Task AddUsedAsync(AppDbContext db, long driveId, long delta, CancellationToken ct = default)
        => delta == 0 ? Task.CompletedTask : db.Drives.Where(d => d.Id == driveId).ExecuteUpdateAsync(s => s.SetProperty(d => d.UsedBytes, d => d.UsedBytes + delta), ct);

    /// <summary>Counts the used space of every drive again (the housekeeping).</summary>
    public static async Task RecountAsync(AppDbContext db, CancellationToken ct = default)
    {
        var sums = await db.NodeVersions.Join(db.Nodes.IgnoreQueryFilters(), v => v.NodeId, n => n.Id, (v, n) => new { n.DriveId, v.Size })
            .GroupBy(x => x.DriveId).Select(g => new { DriveId = g.Key, Used = g.Sum(x => x.Size) }).ToListAsync(ct);
        var all = await db.Drives.IgnoreQueryFilters().Select(d => d.Id).ToListAsync(ct);
        foreach (var id in all)
        {
            var used = sums.FirstOrDefault(s => s.DriveId == id)?.Used ?? 0;
            await db.Drives.IgnoreQueryFilters().Where(d => d.Id == id && d.UsedBytes != used).ExecuteUpdateAsync(s => s.SetProperty(d => d.UsedBytes, used), ct);
        }
    }

    private static string? Check(DriveInput input)
    {
        var name = input.Name?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            return Text.T("Common.NameRequired");
        }

        return name.Length > 120 ? Text.T("Drives.NameTooLong") : null;
    }

    private static string? Clean(string? text)
    {
        var t = text?.Trim();
        return string.IsNullOrEmpty(t) ? null : t.Length > 1000 ? t[..1000] : t;
    }

    private static string? Colour(string? value) => Ui.IsColour(value) ? value : null;
}
