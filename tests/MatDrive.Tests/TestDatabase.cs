using MatDrive.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MatDrive.Tests;

/// <summary>A test that needs PostgreSQL: it runs when <c>MATDRIVE_TEST_DB</c> names a server (an administrator connection string), else it is skipped.</summary>
public sealed class DbFactAttribute : FactAttribute
{
    public DbFactAttribute()
    {
        if (TestDatabase.Admin is null)
        {
            Skip = "MATDRIVE_TEST_DB is not set (a PostgreSQL connection string with the right to create databases).";
        }
    }
}

/// <summary>A database of its own for one test, migrated like the real one (so the migrations are tested too), dropped afterwards.</summary>
public sealed class TestDatabase : IAsyncDisposable
{
    private readonly string _name = "matdrive_t_" + Guid.NewGuid().ToString("N")[..12];

    private TestDatabase()
    {
    }

    public static string? Admin => Environment.GetEnvironmentVariable("MATDRIVE_TEST_DB");

    public AppDbContext Db { get; private set; } = null!;

    /// <summary>A new database; <paramref name="migrate"/> = false leaves it empty (a test of a migration takes it step by step).</summary>
    public static async Task<TestDatabase> CreateAsync(bool migrate = true)
    {
        var test = new TestDatabase();
        await using (var admin = new NpgsqlConnection(Admin))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{test._name}\"", admin);
            await create.ExecuteNonQueryAsync();
        }

        var connection = new NpgsqlConnectionStringBuilder(Admin) { Database = test._name }.ConnectionString;
        test.Db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options);
        if (migrate)
        {
            await test.Db.Database.MigrateAsync();
        }

        return test;
    }

    /// <summary>A person with an account (their own address, a name and a role).</summary>
    public async Task<User> AddUserAsync(string name, UserRole role = UserRole.User)
    {
        var user = new User { Email = $"{name.ToLowerInvariant()}@example.com", DisplayName = name, PasswordHash = "x", Role = role };
        Db.Users.Add(user);
        await Db.SaveChangesAsync();
        return user;
    }

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(Admin);
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();
    }
}
