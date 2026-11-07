using MatDrive.Services.Locations;

namespace MatDrive.Tests;

/// <summary>The one check of every path of a location: normalised, no <c>..</c>, never outside the folder of the location (links included), never the data folder.</summary>
public sealed class LocationPathGuardTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("/", "")]
    [InlineData("/photos/2026/", "photos/2026")]
    [InlineData(@"photos\2026\a.jpg", "photos/2026/a.jpg")]
    [InlineData("//photos///a.jpg", "photos/a.jpg")]
    public void A_path_is_normalised(string? path, string expected) => Assert.Equal(expected, PathGuard.Normalize(path));

    [Theory]
    [InlineData("..")]
    [InlineData("../etc/passwd")]
    [InlineData("photos/../../etc")]
    [InlineData(@"photos\..\..")]
    [InlineData("photos/./a.jpg")]
    [InlineData("photos/a\u0000.jpg")]
    [InlineData("photos/\u001b[1m")]
    public void Paths_that_step_outside_are_refused(string path)
    {
        var ex = Assert.Throws<LocationException>(() => PathGuard.Check(path));
        Assert.Equal(LocationError.InvalidPath, ex.Code);
    }

    [Theory]
    [InlineData("photos/..hidden")]
    [InlineData("photos/a..b.txt")]
    [InlineData(".config/settings.json")]
    [InlineData("März/Übersicht 2026.xlsx")]
    public void Names_with_dots_are_fine(string path) => Assert.Equal(path, PathGuard.Check(path));

    [Fact]
    public void Parent_name_combine_and_display()
    {
        Assert.Null(PathGuard.Parent(""));
        Assert.Equal(string.Empty, PathGuard.Parent("a.txt"));
        Assert.Equal("photos/2026", PathGuard.Parent("/photos/2026/a.jpg"));
        Assert.Equal("a.jpg", PathGuard.NameOf("photos/2026/a.jpg"));
        Assert.Equal("photos/a.jpg", PathGuard.Combine("photos/", "/a.jpg"));
        Assert.Equal("a.jpg", PathGuard.Combine("", "a.jpg"));
        Assert.Equal("/", PathGuard.Display(""));
        Assert.Equal("/photos/2026", PathGuard.Display("photos/2026"));
        Assert.True(PathGuard.IsSameOrBelow("photos/2026", "photos"));
        Assert.True(PathGuard.IsSameOrBelow("photos", "photos"));
        Assert.True(PathGuard.IsSameOrBelow("anything", ""));
        Assert.False(PathGuard.IsSameOrBelow("photos-old", "photos"));
    }

    [Fact]
    public void Remote_paths_keep_inside_the_folder_of_the_location()
    {
        Assert.Equal("/upload", PathGuard.RemoteBase("upload/"));
        Assert.Equal(string.Empty, PathGuard.RemoteBase("/"));
        Assert.Equal("/", PathGuard.Remote(string.Empty, ""));
        Assert.Equal("/upload", PathGuard.Remote("/upload", ""));
        Assert.Equal("/upload/a/b.txt", PathGuard.Remote("/upload", "/a/b.txt"));
        Assert.Throws<LocationException>(() => PathGuard.Remote("/upload", "../etc"));
    }

    [Fact]
    public void A_local_path_never_leaves_the_folder_of_the_location()
    {
        using var bed = new TempFolder();
        var root = bed.Make("location");
        var data = bed.Make("data");
        Directory.CreateDirectory(Path.Combine(root, "photos"));

        Assert.Equal(Path.Combine(root, "photos"), PathGuard.ResolveLocal(root, "photos"));
        Assert.Equal(root, PathGuard.ResolveLocal(root, "/"));

        // "/etc" is the folder etc inside the location, not the one of the machine
        Assert.Equal(Path.Combine(root, "etc"), PathGuard.ResolveLocal(root, "/etc"));
        Assert.Null(PathGuard.ResolveLocal(root, "../data"));
        Assert.Null(PathGuard.ResolveLocal(root, "photos/../../data"));
        if (OperatingSystem.IsWindows())
        {
            Assert.Null(PathGuard.ResolveLocal(root, @"C:\Windows"));
            Assert.Null(PathGuard.ResolveLocal(root, "C:x"));
        }

        // the data folder of MatDrive is invisible even when a location lies above it
        var above = bed.Root;
        Assert.Null(PathGuard.ResolveLocal(above, "data", hidden: data));
        Assert.Null(PathGuard.ResolveLocal(above, "data/files", hidden: data));
        Assert.NotNull(PathGuard.ResolveLocal(above, "location", hidden: data));
    }

    [Fact]
    public async Task A_link_out_of_the_location_is_not_followed()
    {
        using var bed = new TempFolder();
        var root = bed.Make("location");
        var outside = bed.Make("outside");
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "secret");
        var link = Path.Combine(root, "escape");
        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return; // Windows without the right to make links (developer mode off): nothing to check here
        }

        Assert.Null(PathGuard.ResolveLocal(root, "escape"));
        Assert.Null(PathGuard.ResolveLocal(root, "escape/secret.txt"));

        // a link that stays inside is fine
        Directory.CreateDirectory(Path.Combine(root, "real"));
        Directory.CreateSymbolicLink(Path.Combine(root, "inside"), Path.Combine(root, "real"));
        Assert.NotNull(PathGuard.ResolveLocal(root, "inside"));

        // and the listing leaves the escaping link out
        var provider = new LocalProvider(root, null);
        var names = (await provider.ListAsync(string.Empty, CancellationToken.None)).Select(e => e.Name).ToList();
        Assert.DoesNotContain("escape", names);
        Assert.Contains("inside", names);
    }
}

/// <summary>A folder of its own for a test, removed afterwards.</summary>
public sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Root = Path.Combine(Path.GetTempPath(), "matdrive-test-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Make(string name)
    {
        var path = Path.Combine(Root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // a file still open by the system: the temp folder is cleaned up later
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
