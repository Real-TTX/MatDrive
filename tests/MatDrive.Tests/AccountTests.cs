using MatDrive.Data;
using MatDrive.Services;
using MatDrive.Services.Accounts;

namespace MatDrive.Tests;

/// <summary>The business layer of the accounts: only administrators manage accounts, the last administrator stays, a person changes only their own profile.</summary>
public sealed class AccountTests
{
    private static readonly UserCriteria All = new(null, null, null, "name", false, 1, 25);

    [DbFact]
    public async Task OnlyAnAdministratorListsAndReadsAccounts()
    {
        await using var t = await TestDatabase.CreateAsync();
        var admin = await t.AddUserAsync("Ada", UserRole.Admin);
        var bob = await t.AddUserAsync("Bob");

        Assert.Null(await UserAdmin.ListAsync(t.Db, bob.Id, All));
        Assert.Null(await UserAdmin.GetAsync(t.Db, bob.Id, admin.Id));

        var list = await UserAdmin.ListAsync(t.Db, admin.Id, All);
        Assert.NotNull(list);
        Assert.Equal(2, list.Total);
        Assert.Equal("Ada", (await UserAdmin.GetAsync(t.Db, admin.Id, admin.Id))!.DisplayName);
    }

    [DbFact]
    public async Task APersonCannotChangeOrDeleteOtherAccounts()
    {
        await using var t = await TestDatabase.CreateAsync();
        var admin = await t.AddUserAsync("Ada", UserRole.Admin);
        var bob = await t.AddUserAsync("Bob");

        var saved = await UserAdmin.UpdateAsync(t.Db, new PasswordService(), bob.Id, admin.Id, new UserInput("Hacked", "ada@example.com", UserRole.User, false, string.Empty));
        Assert.True(saved.Denied);
        Assert.Equal(UserDeleted.Denied, await UserAdmin.DeleteAsync(t.Db, null!, null!, bob.Id, admin.Id));

        t.Db.ChangeTracker.Clear();
        Assert.Equal("Ada", (await UserAdmin.GetAsync(t.Db, admin.Id, admin.Id))!.DisplayName);
    }

    [DbFact]
    public async Task TheLastAdministratorKeepsTheRole()
    {
        await using var t = await TestDatabase.CreateAsync();
        var admin = await t.AddUserAsync("Ada", UserRole.Admin);

        var saved = await UserAdmin.UpdateAsync(t.Db, new PasswordService(), admin.Id, admin.Id, new UserInput("Ada", "ada@example.com", UserRole.User, true, string.Empty));
        Assert.Contains(saved.Errors, e => e.Field == nameof(UserInput.Role));
        Assert.Equal(UserDeleted.CannotDelete, await UserAdmin.DeleteAsync(t.Db, null!, null!, admin.Id, admin.Id));
    }

    [DbFact]
    public async Task TheProfileIsChecked()
    {
        await using var t = await TestDatabase.CreateAsync();
        var bob = await t.AddUserAsync("Bob");

        var (none, errors) = await Profiles.UpdateAsync(t.Db, bob.Id, new ProfileInput(" ", string.Empty, "Nowhere/City", true, "standard"), mailConfigured: false);
        Assert.Null(none);
        Assert.Equal(2, errors.Count);

        var (saved, _) = await Profiles.UpdateAsync(t.Db, bob.Id, new ProfileInput(" Robert ", "de", "Europe/Berlin", false, "oled"), mailConfigured: false);
        Assert.Equal("Robert", saved!.DisplayName);
        Assert.Equal("oled", saved.Scheme);
        Assert.True(saved.MailOnShare); // unchanged while no mail server is set up
        Assert.False(await Profiles.SetThemeAsync(t.Db, bob.Id, "pink"));
    }
}
