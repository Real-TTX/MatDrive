using System.Text;
using MatDrive.Data;
using MatDrive.Services.Files;
using MatDrive.Services.Locations;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace MatDrive.Tests;

/// <summary>
/// The business layer of the storage locations against a real database and a local folder: only administrators manage them, secrets are encrypted and never
/// returned, every operation checks the role (Viewer reads, Editor changes, read-only and switched off locations refuse), changes are in the activity log,
/// files go to the drives and back through the drive's own business layer.
/// </summary>
public sealed class LocationServiceTests
{
    private static readonly LocationQuery All = new(null, null, false, "name", false, 1, 200);

    [DbFact]
    public async Task Only_administrators_manage_locations()
    {
        await using var t = await TestDatabase.CreateAsync();
        using var bed = new Bed();
        var admin = await t.AddUserAsync("Ada", UserRole.Admin);
        var bob = await t.AddUserAsync("Bob");

        var (none, errors) = await LocationAdmin.SaveAsync(t.Db, bed.Connections, bob.Id, null, bed.Local("Files", bed.Files));
        Assert.Null(none);
        Assert.Empty(errors);
        Assert.Null(await LocationAdmin.ListAsync(t.Db, bed.Connections, bob.Id, new LocationCriteria(null, null, null, "name", false, 1, 25)));

        var id = await bed.CreateAsync(t.Db, admin.Id, bed.Local("Files", bed.Files));
        Assert.Null(await LocationAdmin.GetAsync(t.Db, bed.Connections, bob.Id, id));
        Assert.False(await LocationAdmin.DeleteAsync(t.Db, bed.Connections, bob.Id, id));
        Assert.False(await LocationAdmin.SetEveryoneAsync(t.Db, bob.Id, id, AccessRole.Editor));
        Assert.True((await LocationAdmin.AddPersonAsync(t.Db, bob.Id, id, "bob@example.com", AccessRole.Editor)).NotAllowed);

        var list = await LocationAdmin.ListAsync(t.Db, bed.Connections, admin.Id, new LocationCriteria(null, "local", null, "name", false, 1, 25));
        Assert.Equal("Files", Assert.Single(list!.Items).Name);
    }

    [DbFact]
    public async Task A_local_folder_must_lie_below_the_storage_root_and_outside_the_data()
    {
        await using var t = await TestDatabase.CreateAsync();
        using var bed = new Bed();
        var admin = await t.AddUserAsync("Ada", UserRole.Admin);

        var outside = Path.Combine(bed.Root, "elsewhere");
        Directory.CreateDirectory(outside);
        var (_, errors) = await LocationAdmin.SaveAsync(t.Db, bed.Connections, admin.Id, null, bed.Local("Out", outside));
        Assert.Contains(errors, e => e.Field == nameof(LocationInput.RootPath));

        (_, errors) = await LocationAdmin.SaveAsync(t.Db, bed.Connections, admin.Id, null, bed.Local("Missing", Path.Combine(bed.Storage, "missing")));
        Assert.Contains(errors, e => e.Field == nameof(LocationInput.RootPath));

        // a storage root that holds the data folder: the data folder itself cannot become a location
        using var nested = new Bed(dataInsideStorage: true);
        (_, errors) = await LocationAdmin.SaveAsync(t.Db, nested.Connections, admin.Id, null, nested.Local("Data", nested.Data));
        Assert.Contains(errors, e => e.Field == nameof(LocationInput.RootPath));

        (_, errors) = await LocationAdmin.SaveAsync(t.Db, bed.Connections, admin.Id, null, bed.Local(" ", bed.Files) with { Kind = LocationKind.Sftp, Host = "bad host/" });
        Assert.Contains(errors, e => e.Field == nameof(LocationInput.Name));
        Assert.Contains(errors, e => e.Field == nameof(LocationInput.Host));
    }

    [DbFact]
    public async Task Secrets_are_encrypted_and_never_returned()
    {
        await using var t = await TestDatabase.CreateAsync();
        using var bed = new Bed();
        var admin = await t.AddUserAsync("Ada", UserRole.Admin);
        var input = new LocationInput("NAS", LocationKind.Sftp, "/upload", "nas.local", 2222, null, "anna", "pw-12345", false, null, null, false, false, false, null, false, true, null);
        var id = await bed.CreateAsync(t.Db, admin.Id, input);

        var stored = await t.Db.StorageLocations.AsNoTracking().FirstAsync(l => l.Id == id);
        Assert.NotNull(stored.SecretProtected);
        Assert.DoesNotContain("pw-12345", stored.SecretProtected, StringComparison.Ordinal);
        Assert.Equal("pw-12345", bed.Connections.Unprotect(stored.SecretProtected));

        var detail = await LocationAdmin.GetAsync(t.Db, bed.Connections, admin.Id, id);
        Assert.True(detail!.HasPassword);
        Assert.DoesNotContain(typeof(LocationDetail).GetProperties(), p => p.Name.Contains("Password", StringComparison.Ordinal) && p.PropertyType == typeof(string));

        // an empty field keeps the stored secret; "remove" removes it
        await bed.SaveAsync(t.Db, admin.Id, id, input with { Password = string.Empty, Note = "kept" });
        Assert.Equal("pw-12345", bed.Connections.Unprotect((await t.Db.StorageLocations.AsNoTracking().FirstAsync(l => l.Id == id)).SecretProtected));
        await bed.SaveAsync(t.Db, admin.Id, id, input with { Password = string.Empty, ClearPassword = true });
        Assert.Null((await t.Db.StorageLocations.AsNoTracking().FirstAsync(l => l.Id == id)).SecretProtected);

        // a key of SFTP: encrypted the same way; switching back to the password drops it
        const string key = "-----BEGIN OPENSSH PRIVATE KEY-----\nabc\n-----END OPENSSH PRIVATE KEY-----";
        await bed.SaveAsync(t.Db, admin.Id, id, input with { UseKey = true, PrivateKey = key });
        stored = await t.Db.StorageLocations.AsNoTracking().FirstAsync(l => l.Id == id);
        Assert.Equal(key, bed.Connections.Unprotect(stored.PrivateKeyProtected));
        Assert.DoesNotContain("PRIVATE KEY", stored.PrivateKeyProtected!, StringComparison.Ordinal);
        await bed.SaveAsync(t.Db, admin.Id, id, input with { UseKey = false });
        Assert.Null((await t.Db.StorageLocations.AsNoTracking().FirstAsync(l => l.Id == id)).PrivateKeyProtected);
    }

    [DbFact]
    public async Task The_role_decides_what_a_person_may_do()
    {
        await using var t = await TestDatabase.CreateAsync();
        using var bed = new Bed();
        var admin = await t.AddUserAsync("Ada", UserRole.Admin);
        var bob = await t.AddUserAsync("Bob");
        var id = await bed.CreateAsync(t.Db, admin.Id, bed.Local("Files", bed.Files));
        File.WriteAllText(Path.Combine(bed.Files, "a.txt"), "a");

        // nobody: not even the administrator sees it without a place on the list
        Assert.Null(await LocationService.BrowseAsync(t.Db, bed.Connections, bob.Id, id, null, All));
        Assert.Null(await LocationService.BrowseAsync(t.Db, bed.Connections, admin.Id, id, null, All));
        Assert.Empty(await LocationAccess.ListForMenuAsync(t.Db, bob.Id));
        Assert.False(await LocationAccess.CanWriteAnyAsync(t.Db, bob.Id));

        // everyone may look (the explorer of the drives offers no copy to it)
        Assert.True(await LocationAdmin.SetEveryoneAsync(t.Db, admin.Id, id, AccessRole.Viewer));
        var folder = await LocationService.BrowseAsync(t.Db, bed.Connections, bob.Id, id, null, All);
        Assert.Equal("a.txt", Assert.Single(folder!.Items).Name);
        Assert.False(folder.Context.CanEdit);
        Assert.False(await LocationAccess.CanWriteAnyAsync(t.Db, bob.Id));
        Assert.True((await Upload(t.Db, bed, bob.Id, id, "b.txt")).NotAllowed);
        Assert.True((await LocationService.DeleteAsync(t.Db, bed.Connections, bob.Id, id, ["a.txt"])).NotAllowed);
        Assert.True((await LocationService.RenameAsync(t.Db, bed.Connections, bob.Id, id, "a.txt", "c.txt")).NotAllowed);
        Assert.True(File.Exists(Path.Combine(bed.Files, "a.txt")));

        // Bob on the list as Editor
        Assert.True((await LocationAdmin.AddPersonAsync(t.Db, admin.Id, id, "BOB@example.com ", AccessRole.Editor)).Ok);
        Assert.Null((await Upload(t.Db, bed, bob.Id, id, "b.txt")).Error);
        Assert.True(File.Exists(Path.Combine(bed.Files, "b.txt")));
        Assert.True(await LocationAccess.CanWriteAnyAsync(t.Db, bob.Id));
        Assert.False(await LocationAccess.CanWriteAnyAsync(t.Db, admin.Id));

        // read-only: nobody is more than Viewer, and a copy from the drive is refused like any write
        var detail = (await LocationAdmin.GetAsync(t.Db, bed.Connections, admin.Id, id))!;
        await bed.SaveAsync(t.Db, admin.Id, id, bed.Local(detail.Name, detail.RootPath) with { IsReadOnly = true });
        Assert.True((await Upload(t.Db, bed, bob.Id, id, "c.txt")).NotAllowed);
        Assert.Equal(AccessRole.Viewer, (await LocationAccess.ListForPersonAsync(t.Db, bob.Id)).Single().Role);
        Assert.False(await LocationAccess.CanWriteAnyAsync(t.Db, bob.Id));
        var mine = await FileService.UploadAsync(t.Db, bed.Store, Actor.Of(bob.Id), (await DriveService.MyDriveAsync(t.Db, bob.Id)).Id, null, "d.txt", new MemoryStream("d"u8.ToArray()));
        Assert.True((await LocationService.FromDriveAsync(t.Db, bed.Connections, bed.Store, bob.Id, id, [mine.Value!.Id], "/")).NotAllowed);
        Assert.False(File.Exists(Path.Combine(bed.Files, "d.txt")));

        // switched off: gone for everybody
        await bed.SaveAsync(t.Db, admin.Id, id, bed.Local(detail.Name, detail.RootPath) with { IsActive = false });
        Assert.Null(await LocationService.BrowseAsync(t.Db, bed.Connections, bob.Id, id, null, All));
        Assert.Empty(await LocationAccess.ListForMenuAsync(t.Db, bob.Id));
    }

    [DbFact]
    public async Task Changes_work_and_are_in_the_activity_log()
    {
        await using var t = await TestDatabase.CreateAsync();
        using var bed = new Bed();
        var admin = await t.AddUserAsync("Ada", UserRole.Admin);
        var bob = await t.AddUserAsync("Bob");
        var files = await bed.CreateAsync(t.Db, admin.Id, bed.Local("Files", bed.Files));
        var other = await bed.CreateAsync(t.Db, admin.Id, bed.Local("Other", bed.Other));
        await LocationAdmin.SetEveryoneAsync(t.Db, admin.Id, files, AccessRole.Editor);
        await LocationAdmin.SetEveryoneAsync(t.Db, admin.Id, other, AccessRole.Editor);

        // an upload of a folder makes its folders; a taken name is asked about, kept with a number, or replaced
        Assert.Null((await Upload(t.Db, bed, bob.Id, files, "trip/day 1/a.txt", "first")).Error);
        var again = await Upload(t.Db, bed, bob.Id, files, "trip/day 1/a.txt", "second");
        Assert.True(again.Exists);
        Assert.Equal("trip/day 1/a.txt", Assert.Single((await LocationService.ExistingAsync(t.Db, bed.Connections, bob.Id, files, "/", ["trip/day 1/a.txt", "trip/new.txt", "x/y.txt"])).Value!));
        Assert.Equal("a (2).txt", (await Upload(t.Db, bed, bob.Id, files, "trip/day 1/a.txt", "second", LocationConflict.KeepBoth)).Entry!.Name);
        Assert.Null((await Upload(t.Db, bed, bob.Id, files, "trip/day 1/a.txt", "third", LocationConflict.Replace)).Error);
        Assert.Equal("third", File.ReadAllText(Path.Combine(bed.Files, "trip", "day 1", "a.txt")));

        Assert.True((await LocationService.CreateFolderAsync(t.Db, bed.Connections, bob.Id, files, "/", "archive")).Ok);
        Assert.False((await LocationService.CreateFolderAsync(t.Db, bed.Connections, bob.Id, files, "/", "archive")).Ok);
        Assert.Equal("day one", (await LocationService.RenameAsync(t.Db, bed.Connections, bob.Id, files, "/trip/day 1", "day one")).Value!.Name);

        // move inside the location, never into itself; copy to another location
        Assert.False((await LocationService.TransferAsync(t.Db, bed.Connections, bob.Id, files, ["/trip"], files, "/trip/day one", move: true)).Ok);
        Assert.Equal(1, (await LocationService.TransferAsync(t.Db, bed.Connections, bob.Id, files, ["/trip"], files, "/archive", move: true)).Value);
        Assert.True(File.Exists(Path.Combine(bed.Files, "archive", "trip", "day one", "a (2).txt")));
        Assert.Equal(1, (await LocationService.TransferAsync(t.Db, bed.Connections, bob.Id, files, ["/archive/trip"], other, "/", move: false)).Value);
        Assert.Equal("third", File.ReadAllText(Path.Combine(bed.Other, "trip", "day one", "a.txt")));
        Assert.True(Directory.Exists(Path.Combine(bed.Files, "archive", "trip")));

        // a move to another location deletes the source when everything arrived; the name taken there gets a number
        Assert.Equal(1, (await LocationService.TransferAsync(t.Db, bed.Connections, bob.Id, files, ["/archive/trip"], other, "/", move: true)).Value);
        Assert.False(Directory.Exists(Path.Combine(bed.Files, "archive", "trip")));
        Assert.True(Directory.Exists(Path.Combine(bed.Other, "trip (2)")));

        Assert.Equal(1, (await LocationService.DeleteAsync(t.Db, bed.Connections, bob.Id, other, ["/trip (2)", "/trip (2)/day one"])).Value);
        Assert.False(Directory.Exists(Path.Combine(bed.Other, "trip (2)")));

        var log = await t.Db.Activities.AsNoTracking().Where(a => a.Action == "location" && a.UserId == bob.Id).Select(a => a.Target + ": " + a.Detail).ToListAsync();
        Assert.Contains(log, l => l.StartsWith("Files: upload /trip/day 1/a.txt", StringComparison.Ordinal));
        Assert.Contains(log, l => l.StartsWith("Files: rename /trip/day 1", StringComparison.Ordinal));
        Assert.Contains(log, l => l.StartsWith("Files: move /trip", StringComparison.Ordinal));
        Assert.Contains(log, l => l.StartsWith("Files: copy /archive/trip → Other/trip", StringComparison.Ordinal));
        Assert.Contains(log, l => l == "Other: delete /trip (2)");
    }

    [DbFact]
    public async Task Files_go_to_a_drive_and_come_back()
    {
        await using var t = await TestDatabase.CreateAsync();
        using var bed = new Bed();
        var admin = await t.AddUserAsync("Ada", UserRole.Admin);
        var bob = await t.AddUserAsync("Bob");
        var id = await bed.CreateAsync(t.Db, admin.Id, bed.Local("Files", bed.Files));
        await LocationAdmin.AddPersonAsync(t.Db, admin.Id, id, "bob@example.com", AccessRole.Editor);
        Directory.CreateDirectory(Path.Combine(bed.Files, "photos", "2026"));
        File.WriteAllText(Path.Combine(bed.Files, "photos", "2026", "a.txt"), "picture");
        File.WriteAllText(Path.Combine(bed.Files, "notes.txt"), "notes");
        var drive = await DriveService.MyDriveAsync(t.Db, bob.Id);

        var copied = await LocationService.ToDriveAsync(t.Db, bed.Connections, bed.Store, bob.Id, id, ["/photos", "/notes.txt"], drive.Id, null);
        Assert.True(copied.Ok, copied.Error);
        var top = await NodeService.ListFolderAsync(t.Db, Actor.Of(bob.Id), drive.Id, null, new NodeQuery(null, null, "name", false, 1, 50));
        Assert.Equal(new[] { "notes.txt", "photos" }, top!.Items.Select(n => n.Name).Order(StringComparer.Ordinal).ToArray());
        var photos = top.Items.Single(n => n.Name == "photos");
        var year = (await NodeService.ListFolderAsync(t.Db, Actor.Of(bob.Id), drive.Id, photos.Id, new NodeQuery(null, null, "name", false, 1, 50)))!.Items.Single();
        var file = (await NodeService.ListFolderAsync(t.Db, Actor.Of(bob.Id), drive.Id, year.Id, new NodeQuery(null, null, "name", false, 1, 50)))!.Items.Single();
        var content = await FileService.OpenAsync(t.Db, bed.Store, Actor.Of(bob.Id), file.Id);
        Assert.Equal("picture", File.ReadAllText(content!.PhysicalPath));

        // a drive of somebody else is not a target
        var ada = await DriveService.MyDriveAsync(t.Db, admin.Id);
        Assert.True((await LocationService.ToDriveAsync(t.Db, bed.Connections, bed.Store, bob.Id, id, ["/notes.txt"], ada.Id, null)).NotAllowed);

        // and back: a file and a folder of the drive into a folder of the location
        var notes = top.Items.Single(n => n.Name == "notes.txt");
        var back = await LocationService.FromDriveAsync(t.Db, bed.Connections, bed.Store, bob.Id, id, [notes.Id, photos.Id], "/photos/2026");
        Assert.True(back.Ok, back.Error);
        Assert.Equal("notes", File.ReadAllText(Path.Combine(bed.Files, "photos", "2026", "notes.txt")));
        Assert.Equal("picture", File.ReadAllText(Path.Combine(bed.Files, "photos", "2026", "photos", "2026", "a.txt")));

        // again into the same folder: the names that are taken get a number, nothing is overwritten
        Assert.Equal(2, (await LocationService.FromDriveAsync(t.Db, bed.Connections, bed.Store, bob.Id, id, [notes.Id, photos.Id], "/photos/2026")).Value);
        Assert.Equal("notes", File.ReadAllText(Path.Combine(bed.Files, "photos", "2026", "notes (2).txt")));
        Assert.Equal("picture", File.ReadAllText(Path.Combine(bed.Files, "photos", "2026", "photos (2)", "2026", "a.txt")));
        Assert.Equal("notes", File.ReadAllText(Path.Combine(bed.Files, "photos", "2026", "notes.txt")));

        // a node Bob may not see is refused
        var secret = await FileService.UploadAsync(t.Db, bed.Store, Actor.Of(admin.Id), ada.Id, null, "secret.txt", new MemoryStream("s"u8.ToArray()));
        Assert.True((await LocationService.FromDriveAsync(t.Db, bed.Connections, bed.Store, bob.Id, id, [secret.Value!.Id], "/")).NotAllowed);
        Assert.Contains(await t.Db.Activities.AsNoTracking().Where(a => a.Action == "location").Select(a => a.Detail).ToListAsync(), d => d!.StartsWith("copy-to-drive /photos", StringComparison.Ordinal));
    }

    [DbFact]
    public async Task The_editor_saves_only_over_what_it_read()
    {
        await using var t = await TestDatabase.CreateAsync();
        using var bed = new Bed();
        var admin = await t.AddUserAsync("Ada", UserRole.Admin);
        var id = await bed.CreateAsync(t.Db, admin.Id, bed.Local("Files", bed.Files));
        await LocationAdmin.SetEveryoneAsync(t.Db, admin.Id, id, AccessRole.Editor);
        File.WriteAllText(Path.Combine(bed.Files, "todo.md"), "# Todo\n");

        var read = await LocationService.ReadTextAsync(t.Db, bed.Connections, admin.Id, id, "/todo.md");
        Assert.True(read.Value.CanEdit);
        var (status, etag, _) = await LocationService.SaveTextAsync(t.Db, bed.Connections, admin.Id, id, "/todo.md", "# Todo\n- milk\n", read.Value.ETag);
        Assert.Equal(LocationSave.Saved, status);
        Assert.NotEqual(read.Value.ETag, etag);
        Assert.Equal("# Todo\n- milk\n", File.ReadAllText(Path.Combine(bed.Files, "todo.md")));

        // the old ETag: somebody changed the file meanwhile
        (status, _, _) = await LocationService.SaveTextAsync(t.Db, bed.Connections, admin.Id, id, "/todo.md", "lost", read.Value.ETag);
        Assert.Equal(LocationSave.Conflict, status);
    }

    [DbFact]
    public async Task The_search_finds_names_and_text_below_the_folder()
    {
        await using var t = await TestDatabase.CreateAsync();
        using var bed = new Bed();
        var admin = await t.AddUserAsync("Ada", UserRole.Admin);
        var id = await bed.CreateAsync(t.Db, admin.Id, bed.Local("Files", bed.Files));
        await LocationAdmin.SetEveryoneAsync(t.Db, admin.Id, id, AccessRole.Viewer);
        Directory.CreateDirectory(Path.Combine(bed.Files, "a", "b"));
        File.WriteAllText(Path.Combine(bed.Files, "a", "b", "needle.txt"), "nothing");
        File.WriteAllText(Path.Combine(bed.Files, "a", "haystack.md"), "line one\nthere is a NEEDLE here\nline three");
        File.WriteAllBytes(Path.Combine(bed.Files, "a", "binary.dat"), [0, 1, 2, (byte)'n', (byte)'e', (byte)'e', (byte)'d', (byte)'l', (byte)'e']);

        var names = new List<LocationSearchEvent>();
        Assert.True(await LocationSearch.RunAsync(t.Db, bed.Connections, admin.Id, id, "/", "needle", false, false, e => { names.Add(e); return Task.CompletedTask; }, CancellationToken.None));
        Assert.Equal(new[] { "needle.txt" }, names.Where(e => e.Type == "hit").Select(e => e.Hit!.Name).ToArray());
        Assert.Equal("done", names[^1].Type);

        var texts = new List<LocationSearchEvent>();
        await LocationSearch.RunAsync(t.Db, bed.Connections, admin.Id, id, "/a", "needle", true, false, e => { texts.Add(e); return Task.CompletedTask; }, CancellationToken.None);
        var hit = texts.Single(e => e.Type == "hit" && e.Hit!.Name == "haystack.md").Hit!;
        Assert.Equal("there is a NEEDLE here", hit.Snippet);
        Assert.DoesNotContain(texts, e => e.Hit?.Name == "binary.dat");

        // a person without access gets nothing at all
        var bob = await t.AddUserAsync("Bob");
        await LocationAdmin.SetEveryoneAsync(t.Db, admin.Id, id, AccessRole.None);
        Assert.False(await LocationSearch.RunAsync(t.Db, bed.Connections, bob.Id, id, "/", "needle", false, false, _ => Task.CompletedTask, CancellationToken.None));
    }

    [DbFact]
    public async Task Removing_a_location_keeps_its_files()
    {
        await using var t = await TestDatabase.CreateAsync();
        using var bed = new Bed();
        var admin = await t.AddUserAsync("Ada", UserRole.Admin);
        var id = await bed.CreateAsync(t.Db, admin.Id, bed.Local("Files", bed.Files));
        await LocationAdmin.AddPersonAsync(t.Db, admin.Id, id, "ada@example.com", AccessRole.Editor);
        File.WriteAllText(Path.Combine(bed.Files, "keep.txt"), "keep");

        Assert.True(await LocationAdmin.DeleteAsync(t.Db, bed.Connections, admin.Id, id));
        Assert.True(File.Exists(Path.Combine(bed.Files, "keep.txt")));
        Assert.Null(await LocationService.BrowseAsync(t.Db, bed.Connections, admin.Id, id, null, All));
        Assert.Empty(await t.Db.StorageLocationAccess.AsNoTracking().Where(a => a.LocationId == id).ToListAsync());
        Assert.Contains(await t.Db.Activities.AsNoTracking().Select(a => a.Detail).ToListAsync(), d => d == "delete");
    }

    private static Task<LocationWrite> Upload(AppDbContext db, Bed bed, long userId, long id, string path, string text = "x", LocationConflict conflict = LocationConflict.Ask)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return LocationService.UploadAsync(db, bed.Connections, userId, id, "/", path, new MemoryStream(bytes), bytes.Length, conflict);
    }

    /// <summary>The folders of a test: the storage root with two folders for locations, the data folder of MatDrive (blobs), and the live side of the locations.</summary>
    private sealed class Bed : IDisposable
    {
        private readonly TempFolder _temp = new();

        public Bed(bool dataInsideStorage = false)
        {
            Storage = _temp.Make("storage");
            Files = Path.Combine(Storage, "files");
            Other = Path.Combine(Storage, "other");
            Directory.CreateDirectory(Files);
            Directory.CreateDirectory(Other);
            Data = dataInsideStorage ? Path.Combine(Storage, "data") : _temp.Make("data");
            Directory.CreateDirectory(Data);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["StorageRoot"] = Storage, ["DataDirectory"] = Data }).Build();
            Connections = new LocationConnections(new EphemeralDataProtectionProvider(), new PlainHttp(), configuration);
            Store = new BlobStore(configuration);
        }

        public string Root => _temp.Root;

        public string Storage { get; }

        public string Files { get; }

        public string Other { get; }

        public string Data { get; }

        public LocationConnections Connections { get; }

        public BlobStore Store { get; }

        public LocationInput Local(string name, string folder)
            => new(name, LocationKind.Local, folder, null, null, null, null, null, false, null, null, false, false, false, null, false, true, null);

        public async Task<long> CreateAsync(AppDbContext db, long adminId, LocationInput input)
        {
            var (id, errors) = await LocationAdmin.SaveAsync(db, Connections, adminId, null, input);
            Assert.Empty(errors);
            return id!.Value;
        }

        public async Task SaveAsync(AppDbContext db, long adminId, long id, LocationInput input)
        {
            var (saved, errors) = await LocationAdmin.SaveAsync(db, Connections, adminId, id, input);
            Assert.Empty(errors);
            Assert.Equal(id, saved);
            db.ChangeTracker.Clear();
        }

        public void Dispose()
        {
            Connections.Dispose();
            _temp.Dispose();
        }
    }

    /// <summary>Like the clients of the app (LocationSetup): redirects by hand, so the authorization stays on the same host.</summary>
    private sealed class PlainHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    }
}
