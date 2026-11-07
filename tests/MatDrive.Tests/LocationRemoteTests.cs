using System.Text;
using MatDrive.Data;
using MatDrive.Services.Locations;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;

namespace MatDrive.Tests;

/// <summary>
/// A test against real servers of the four protocols: it runs when <c>MATDRIVE_TEST_LOCATIONS</c> names them, else it is skipped. <c>loctest</c> means the throwaway
/// servers of the compose project <c>matdrive-loctest</c> (SFTP 47222 with the folder /upload, SMB 47445 with the share public, FTP 47021, WebDAV 47080; tester /
/// Secret123); otherwise <c>host=…;sftp=…;smb=…;ftp=…;webdav=…;user=…;password=…</c>.
/// </summary>
public sealed class RemoteFactAttribute : FactAttribute
{
    public RemoteFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MATDRIVE_TEST_LOCATIONS")))
        {
            Skip = "MATDRIVE_TEST_LOCATIONS is not set (servers for SFTP, SMB, FTP and WebDAV, e.g. the compose project matdrive-loctest: \"loctest\").";
        }
    }
}

public sealed class LocationRemoteTests : IDisposable
{
    private static readonly CancellationToken None = CancellationToken.None;
    private readonly LocationConnections _connections;
    private readonly Dictionary<string, string> _servers;

    public LocationRemoteTests()
    {
        var spec = Environment.GetEnvironmentVariable("MATDRIVE_TEST_LOCATIONS") ?? string.Empty;
        _servers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["host"] = "127.0.0.1", ["sftp"] = "47222", ["smb"] = "47445", ["ftp"] = "47021", ["webdav"] = "47080", ["user"] = "tester", ["password"] = "Secret123",
        };
        foreach (var pair in spec.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2)).Where(p => p.Length == 2))
        {
            _servers[pair[0].Trim()] = pair[1].Trim();
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["StorageRoot"] = Path.GetTempPath(), ["DataDirectory"] = Path.Combine(Path.GetTempPath(), "matdrive-remote-test-data") }).Build();
        _connections = new LocationConnections(new EphemeralDataProtectionProvider(), new PlainHttp(), configuration);
    }

    [RemoteFact]
    public Task Sftp() => ExerciseAsync(Settings(LocationKind.Sftp, root: "/upload"), permissions: true);

    [RemoteFact]
    public Task Smb() => ExerciseAsync(Settings(LocationKind.Smb, share: "public"), permissions: false);

    [RemoteFact]
    public Task Ftp() => ExerciseAsync(Settings(LocationKind.Ftp), permissions: true);

    [RemoteFact]
    public Task WebDav() => ExerciseAsync(Settings(LocationKind.WebDav), permissions: false);

    [RemoteFact]
    public async Task A_wrong_password_is_access_denied()
    {
        foreach (var kind in new[] { LocationKind.Sftp, LocationKind.Smb, LocationKind.Ftp, LocationKind.WebDav })
        {
            var status = await LocationMonitor.ProbeAsync(_connections, Settings(kind, share: kind == LocationKind.Smb ? "public" : null, root: kind == LocationKind.Sftp ? "/upload" : string.Empty) with { Password = "wrong" }, None);
            Assert.False(status.Reachable, kind.ToString());
            Assert.Equal(LocationError.AccessDenied, status.Error);
        }
    }

    [RemoteFact]
    public async Task Smb_without_a_share_shows_the_shares()
    {
        var provider = _connections.Create(Settings(LocationKind.Smb));
        var shares = (await provider.ListAsync(string.Empty, None)).Select(e => e.Name).ToList();
        Assert.Contains("public", shares);
        Assert.All(await provider.ListAsync(string.Empty, None), e => Assert.True(e.IsFolder));
        Assert.Equal(LocationError.ShareLevel, (await Assert.ThrowsAsync<LocationException>(() => provider.CreateFolderAsync("new-share", None))).Code);
        Assert.False(provider.CanMove("public/a.txt", "second/a.txt"));
    }

    private LocationSettings Settings(LocationKind kind, string? share = null, string root = "")
    {
        var host = _servers["host"];
        return kind switch
        {
            LocationKind.WebDav => new LocationSettings(kind, string.Empty, Username: _servers["user"], Password: _servers["password"], Url: $"http://{host}:{_servers["webdav"]}/"),
            _ => new LocationSettings(kind, root, host, int.Parse(_servers[kind.ToString().ToLowerInvariant()], System.Globalization.CultureInfo.InvariantCulture), share, _servers["user"], _servers["password"]),
        };
    }

    /// <summary>The same walk for every protocol: folders, writes (also two at once into a new folder), a range, move, copy, permissions, delete.</summary>
    private async Task ExerciseAsync(LocationSettings settings, bool permissions)
    {
        var provider = _connections.Create(settings);
        await provider.CheckAsync(None);
        var top = "remote-test-" + Guid.NewGuid().ToString("N")[..8];
        await provider.CreateFolderAsync(top, None);
        try
        {
            Assert.Equal(LocationError.Conflict, (await Assert.ThrowsAsync<LocationException>(() => provider.CreateFolderAsync(top, None))).Code);

            // two uploads into the same new folder at once: both arrive
            await Task.WhenAll(
                LocationService.SafeWriteAsync(provider, $"{top}/new/deep/a.txt", Text("0123456789"), 10, overwrite: false, None),
                LocationService.SafeWriteAsync(provider, $"{top}/new/deep/b.txt", Text("bbb"), 3, overwrite: false, None),
                LocationService.SafeWriteAsync(provider, $"{top}/new/c.txt", Text("c"), 1, overwrite: false, None));
            var names = (await provider.ListAsync($"{top}/new/deep", None)).Select(e => e.Name).Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { "a.txt", "b.txt" }, names); // no temporary names left

            var a = await provider.GetAsync($"{top}/new/deep/a.txt", None);
            Assert.NotNull(a);
            Assert.Equal(10, a.Size);
            Assert.False(a.IsFolder);

            // the time of the server is UTC, not shifted by the zone of this machine
            Assert.InRange(a.Modified, DateTime.UtcNow.AddMinutes(-10), DateTime.UtcNow.AddMinutes(10));

            await using (var stream = await provider.OpenReadAsync($"{top}/new/deep/a.txt", 4, None))
            {
                Assert.Equal("456789", await new StreamReader(stream).ReadToEndAsync());
            }

            Assert.Null(await provider.GetAsync($"{top}/missing.txt", None));
            Assert.Equal(LocationError.Conflict, (await Assert.ThrowsAsync<LocationException>(() => provider.WriteAsync($"{top}/new/c.txt", Text("x"), 1, overwrite: false, None))).Code);
            await provider.WriteAsync($"{top}/new/c.txt", Text("replaced"), 8, overwrite: true, None);
            Assert.Equal(8, (await provider.GetAsync($"{top}/new/c.txt", None))!.Size);

            await provider.MoveAsync($"{top}/new/c.txt", $"{top}/moved/c.txt", overwrite: false, None);
            Assert.Null(await provider.GetAsync($"{top}/new/c.txt", None));
            Assert.NotNull(await provider.GetAsync($"{top}/moved/c.txt", None));

            // a copy of a folder: by the server where it can, else through MatDrive
            var folder = (await provider.GetAsync($"{top}/new", None))!;
            if (!await provider.TryCopyAsync($"{top}/new", $"{top}/copy", None))
            {
                await LocationService.CopyTreeAsync(provider, folder, provider, $"{top}/copy", None);
            }

            Assert.Equal(3, (await provider.GetAsync($"{top}/copy/deep/b.txt", None))!.Size);

            if (permissions)
            {
                var before = await provider.PermissionsAsync($"{top}/copy/deep/b.txt", None);
                Assert.NotNull(before);
                await provider.SetPermissionsAsync($"{top}/copy/deep/b.txt", Convert.ToInt32("640", 8), None);
                Assert.Equal("640", LocationPermissions.ToText((await provider.PermissionsAsync($"{top}/copy/deep/b.txt", None))!.Mode));
            }

            _ = await provider.SpaceAsync(None); // not every server tells; it must not fail

            await provider.DeleteAsync($"{top}/copy", None);
            Assert.Null(await provider.GetAsync($"{top}/copy", None));
            Assert.Equal(LocationError.InvalidPath, (await Assert.ThrowsAsync<LocationException>(() => provider.ListAsync("../", None))).Code);
        }
        finally
        {
            await provider.DeleteAsync(top, None);
        }

        Assert.Null(await provider.GetAsync(top, None));
    }

    private static MemoryStream Text(string text) => new(Encoding.UTF8.GetBytes(text));

    public void Dispose() => _connections.Dispose();

    /// <summary>Like the clients of the app (LocationSetup): redirects by hand, so the authorization stays on the same host.</summary>
    private sealed class PlainHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    }
}
