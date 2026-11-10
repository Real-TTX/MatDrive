using MatDrive.Data;
using MatDrive.Platform;
using MatDrive.Services;
using MatDrive.Services.Files;
using MatPlatform;
using Microsoft.Extensions.Logging.Abstractions;

namespace MatDrive.Tests;

/// <summary>
/// The quota "storage" of MatIdentity (MatDesign docs/PLATFORM.md "People, permissions and quotas"): MatDrive names it, keeps what MatIdentity sent at the sign-in on
/// the account, counts it before its own setting, and answers what a person uses for /api/quotas.
/// </summary>
public sealed class QuotaTests
{
    private const long Gb = 1024L * 1024 * 1024;

    [Fact]
    public void MatDrive_names_the_space_of_my_drive_as_a_quota_in_bytes()
    {
        var quota = Assert.Single(MatPlatformBuilder.Build(MatDrivePlatform.Describe, "en").Quotas!);
        Assert.Equal(ExternalIdentity.StorageQuota, quota.Key);
        Assert.Equal(MatQuotaUnits.Bytes, quota.Unit);
        Assert.Null(quota.Period);
    }

    [DbFact]
    public async Task The_value_of_matidentity_counts_before_the_own_setting_and_goes_when_it_is_not_sent()
    {
        await using var test = await TestDatabase.CreateAsync();
        var anna = await test.AddUserAsync("Anna");
        anna.ExternalSubject = "sub-anna";
        anna.QuotaBytes = 5 * Gb;
        await test.Db.SaveChangesAsync();
        var drive = await DriveService.MyDriveAsync(test.Db, anna.Id);
        await DriveService.AddUsedAsync(test.Db, drive.Id, Gb);

        var signIn = new ExternalSignInService(test.Db, null!, NullLogger<ExternalSignInService>.Instance);
        async Task<long?> QuotaAfterSignInAsync(string? role, Dictionary<string, long?>? quotas)
        {
            var result = await signIn.SignInExternalAsync(new ExternalIdentity("sub-anna", null, "Anna", null, role, quotas));
            Assert.NotNull(result.User);
            test.Db.ChangeTracker.Clear();
            var current = await test.Db.Drives.FindAsync(drive.Id);
            return (await DriveService.UsageAsync(test.Db, current!)).Quota;
        }

        // Without MatIdentity's value: the person's own quota.
        Assert.Equal(5 * Gb, (await DriveService.UsageAsync(test.Db, drive)).Quota);
        Assert.Equal((Gb, (long?)(5 * Gb)), await DriveService.PersonalUsageAsync(test.Db, "sub-anna"));

        Assert.Equal(2 * Gb, await QuotaAfterSignInAsync("user", new() { [ExternalIdentity.StorageQuota] = 2 * Gb }));
        // No limit from MatIdentity.
        Assert.Null(await QuotaAfterSignInAsync("user", new() { [ExternalIdentity.StorageQuota] = null }));
        // A link after the password knows no quotas: the account keeps what it has.
        Assert.Null(await QuotaAfterSignInAsync("user", null));
        // MatIdentity sends no value: the own setting again.
        Assert.Equal(5 * Gb, await QuotaAfterSignInAsync("user", new()));
        // MatIdentity does not decide the roles here (no role): the own setting, whatever came.
        Assert.Equal(5 * Gb, await QuotaAfterSignInAsync(null, new() { [ExternalIdentity.StorageQuota] = Gb }));

        // The value of MatIdentity is checked at an upload: 0 bytes leaves no room.
        await QuotaAfterSignInAsync("user", new() { [ExternalIdentity.StorageQuota] = 0 });
        Assert.False(await FileService.HasRoomAsync(test.Db, drive.Id, 1));

        // The usage for /api/quotas is MatDrive's own limit; MatPlatform puts MatIdentity's value over it.
        Assert.Equal((Gb, (long?)(5 * Gb)), await DriveService.PersonalUsageAsync(test.Db, "sub-anna"));
        Assert.Null(await DriveService.PersonalUsageAsync(test.Db, "sub-nobody"));
    }
}
