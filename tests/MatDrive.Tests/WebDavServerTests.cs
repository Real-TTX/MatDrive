using System.Data.Common;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using MatDrive.Data;
using MatDrive.Services;
using MatDrive.Services.Accounts;
using MatDrive.Services.Files;
using MatDrive.Services.WebDav;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace MatDrive.Tests;

/// <summary>
/// The whole app in memory for the WebDAV tests: a database of its own (made and migrated once for the class, dropped at the end) and a folder of its own for the
/// bytes of the files. Every test makes its own people, so their drives never meet. Only when <c>MATDRIVE_TEST_DB</c> is set.
/// </summary>
public sealed class WebDavServer : IAsyncLifetime
{
    private TestDatabase? _database;
    private WebApplicationFactory<Program>? _factory;
    private string _connection = string.Empty;

    public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), "matdrive-tests", Guid.NewGuid().ToString("N")[..12]);

    public HttpClient Client { get; private set; } = null!;

    /// <summary>The commands the app sent to the database (to see that a list costs a few queries, not one per item).</summary>
    public CommandCounter Commands { get; } = new();

    /// <summary>A context of the test (not the one of the app) to prepare and check the data.</summary>
    public AppDbContext NewDb() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_connection).Options);

    public async Task InitializeAsync()
    {
        if (TestDatabase.Admin is null)
        {
            return;
        }

        _database = await TestDatabase.CreateAsync();
        _connection = _database.Db.Database.GetConnectionString()!;
        Directory.CreateDirectory(DataDirectory);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
        {
            web.UseEnvironment("Development");
            web.UseSetting("ConnectionStrings:Default", _connection);
            web.UseSetting("DataDirectory", DataDirectory);
            web.UseSetting("Logging:LogLevel:Default", "Warning");
            web.ConfigureTestServices(services => services.ConfigureDbContext<AppDbContext>((_, options) => options.AddInterceptors(Commands)));
        });
        Client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }

        try
        {
            if (Directory.Exists(DataDirectory))
            {
                Directory.Delete(DataDirectory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a file still open: the temporary folder is cleaned up by the system
        }
    }

    public sealed class CommandCounter : DbCommandInterceptor
    {
        private int _count;

        public int Count => _count;

        public void Reset() => Interlocked.Exchange(ref _count, 0);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}

/// <summary>
/// The WebDAV server end to end through HTTP (the pipeline of the app with its sign-in, status pages and limits): PROPFIND, PUT and versions, GET with ranges,
/// MKCOL, MOVE and COPY, DELETE into the trash, roles, shares, quota and limits, locks, and the activity it leaves.
/// </summary>
public sealed class WebDavServerTests(WebDavServer server) : IClassFixture<WebDavServer>
{
    private static readonly XNamespace D = DavXml.Dav;

    private const string Win32Patch =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?><D:propertyupdate xmlns:D=\"DAV:\" xmlns:Z=\"urn:schemas-microsoft-com:\"><D:set><D:prop>" +
        "<Z:Win32CreationTime>Sat, 07 Nov 2026 18:00:00 GMT</Z:Win32CreationTime><Z:Win32FileAttributes>00000020</Z:Win32FileAttributes></D:prop></D:set></D:propertyupdate>";

    /// <summary>A person with the address and an app password to sign in to WebDAV.</summary>
    private sealed record Person(long Id, string Email, string Password);

    [DbFact]
    public async Task The_server_asks_for_basic_credentials_and_takes_an_app_password()
    {
        var ada = await PersonAsync("Ada");

        using (var options = await SendAsync(null, "OPTIONS", "/dav/"))
        {
            Assert.Equal(HttpStatusCode.OK, options.StatusCode);
            Assert.Equal("1, 2", options.Headers.GetValues("DAV").Single());
            Assert.Equal("DAV", options.Headers.GetValues("MS-Author-Via").Single());
            Assert.Contains("PROPFIND", options.Content.Headers.Allow);
            Assert.Contains("LOCK", options.Content.Headers.Allow);
        }

        using (var anonymous = await SendAsync(null, "PROPFIND", "/dav/", null, ("Depth", "0")))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode); // not a redirect to the sign-in page, not the error page of the site
            Assert.Contains("Basic realm=\"MatDrive\"", string.Join(", ", anonymous.Headers.GetValues("WWW-Authenticate")));
            Assert.Equal("text/plain", anonymous.Content.Headers.ContentType!.MediaType);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(ada with { Password = "wrong-password" }, "PROPFIND", "/dav/", null, ("Depth", "0")));

        var (status, xml) = await PropFindAsync(ada, "/dav/", "1");
        Assert.Equal(HttpStatusCode.MultiStatus, status);
        Assert.Equal(new[] { "/dav/", "/dav/My%20Drive/", "/dav/Shared%20drives/", "/dav/Shared%20with%20me/" }, Hrefs(xml!));
        Assert.Equal(HttpStatusCode.MultiStatus, (await PropFindAsync(ada, "/dav", "0")).Status); // the Finder asks without the slash
    }

    [DbFact]
    public async Task Failed_sign_ins_are_limited_per_address_and_a_sign_in_is_remembered()
    {
        await using var t = await TestDatabase.CreateAsync();
        var passwords = new PasswordService();
        var ada = await t.AddUserAsync("Ada");
        ada.PasswordHash = passwords.Hash("Start12345");
        await t.Db.SaveChangesAsync();
        var appPassword = (await AppPasswords.CreateAsync(t.Db, ada.Id, "WebDAV")).Value!;
        var auth = new DavAuthenticator(new MemoryCache(new MemoryCacheOptions()), passwords);
        static string Basic(string password) => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("Ada@Example.com:" + password));

        Assert.Equal(DavSignIn.Missing, (await auth.SignInAsync(t.Db, null, "10.0.0.1")).Result);
        Assert.Equal(DavSignIn.Ok, (await auth.SignInAsync(t.Db, Basic("Start12345"), "10.0.0.1")).Result); // the password of a local account works too
        for (var i = 0; i < DavAuthenticator.FailuresPerMinute; i++)
        {
            Assert.Equal(DavSignIn.Failed, (await auth.SignInAsync(t.Db, Basic("wrong-" + i), "10.0.0.1")).Result);
        }

        Assert.Equal(DavSignIn.Throttled, (await auth.SignInAsync(t.Db, Basic(appPassword), "10.0.0.1")).Result);
        var (result, user) = await auth.SignInAsync(t.Db, Basic(appPassword), "10.0.0.2");
        Assert.Equal(DavSignIn.Ok, result);
        Assert.Equal(ada.Id, user!.Id);

        // remembered for a while: the database is not asked again (the hash of PBKDF2 is not computed on every request)
        await t.Db.AppPasswords.ExecuteDeleteAsync();
        Assert.Equal(DavSignIn.Ok, (await auth.SignInAsync(t.Db, Basic(appPassword), "10.0.0.1")).Result);
    }

    [DbFact]
    public async Task Propfind_answers_a_place_and_its_children()
    {
        var ada = await PersonAsync("Ada");
        Assert.Equal(HttpStatusCode.Created, await StatusAsync(ada, "MKCOL", "/dav/My%20Drive/Reports"));
        using (var put = await SendAsync(ada, "PUT", "/dav/My%20Drive/Reports/%C3%9Cbersicht%20%F0%9F%98%80.txt", new StringContent("hello")))
        {
            Assert.Equal(HttpStatusCode.Created, put.StatusCode);
            Assert.Equal(Tag("hello"), put.Headers.ETag!.Tag);
        }

        var (_, zero) = await PropFindAsync(ada, "/dav/My%20Drive/Reports/", "0");
        Assert.Single(zero!.Root!.Elements(D + "response"));

        var (status, one) = await PropFindAsync(ada, "/dav/My%20Drive/Reports", "1", PropBody("displayname", "getcontentlength", "getetag", "getcontenttype", "resourcetype"));
        Assert.Equal(HttpStatusCode.MultiStatus, status);
        Assert.Equal(new[] { "/dav/My%20Drive/Reports/", "/dav/My%20Drive/Reports/%C3%9Cbersicht%20%F0%9F%98%80.txt" }, Hrefs(one!));
        Assert.NotNull(Found(one!, 0).Element(D + "resourcetype")!.Element(D + "collection"));
        var file = Found(one!, 1);
        Assert.Equal("Übersicht 😀.txt", file.Element(D + "displayname")!.Value);
        Assert.Equal("5", file.Element(D + "getcontentlength")!.Value);
        Assert.Equal(Tag("hello"), file.Element(D + "getetag")!.Value);
        Assert.Equal("text/plain", file.Element(D + "getcontenttype")!.Value);

        using (var infinity = await SendAsync(ada, "PROPFIND", "/dav/My%20Drive/", null, ("Depth", "infinity")))
        {
            Assert.Equal(HttpStatusCode.Forbidden, infinity.StatusCode);
            Assert.Contains("propfind-finite-depth", await infinity.Content.ReadAsStringAsync());
        }

        Assert.Equal(HttpStatusCode.NotFound, (await PropFindAsync(ada, "/dav/My%20Drive/Nothing/", "0")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await PropFindAsync(ada, "/dav/My%20Drive/", "0", "<propfind xmlns=\"DAV:\"><prop>")).Status);

        // the space of My Drive (RFC 4331)
        var (_, quota) = await PropFindAsync(ada, "/dav/My%20Drive/", "0", PropBody("quota-used-bytes", "quota-available-bytes"));
        Assert.Equal("5", Found(quota!, 0).Element(D + "quota-used-bytes")!.Value);
        Assert.True(long.Parse(Found(quota!, 0).Element(D + "quota-available-bytes")!.Value, System.Globalization.CultureInfo.InvariantCulture) > 0);
    }

    [DbFact]
    public async Task Put_makes_a_file_and_then_new_versions()
    {
        var ada = await PersonAsync("Ada");
        const string path = "/dav/My%20Drive/notes.txt";

        // the Windows Explorer makes an empty file first and then writes the content: one version
        Assert.Equal(HttpStatusCode.Created, await StatusAsync(ada, "PUT", path, new ByteArrayContent([])));
        Assert.Equal(HttpStatusCode.NoContent, await StatusAsync(ada, "PUT", path, new StringContent("one")));
        var id = await NodeIdAsync(ada, "notes.txt");
        Assert.Equal(1, await VersionCountAsync(id));

        // a save a while later is a new version; saves in a row become one
        await AgeVersionsAsync(id);
        Assert.Equal(HttpStatusCode.NoContent, await StatusAsync(ada, "PUT", path, new StringContent("two")));
        Assert.Equal(2, await VersionCountAsync(id));
        Assert.Equal("two", await GetTextAsync(ada, path));
        Assert.Equal(HttpStatusCode.NoContent, await StatusAsync(ada, "PUT", path, new StringContent("three")));
        Assert.Equal(2, await VersionCountAsync(id));

        // conditions
        Assert.Equal(HttpStatusCode.PreconditionFailed, await StatusAsync(ada, "PUT", path, new StringContent("x"), ("If-Match", "\"0000\"")));
        Assert.Equal(HttpStatusCode.PreconditionFailed, await StatusAsync(ada, "PUT", path, new StringContent("x"), ("If-None-Match", "*")));
        Assert.Equal(HttpStatusCode.NoContent, await StatusAsync(ada, "PUT", path, new StringContent("four"), ("If-Match", Tag("three"))));
        Assert.Equal("four", await GetTextAsync(ada, path));
        Assert.Equal(HttpStatusCode.Created, await StatusAsync(ada, "PUT", "/dav/My%20Drive/fresh.txt", new StringContent("x"), ("If-None-Match", "*")));

        // what cannot be written
        Assert.Equal(HttpStatusCode.Conflict, await StatusAsync(ada, "PUT", "/dav/My%20Drive/Missing/a.txt", new StringContent("x")));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, await StatusAsync(ada, "PUT", "/dav/My%20Drive/", new StringContent("x")));
        Assert.Equal(HttpStatusCode.BadRequest, await StatusAsync(ada, "PUT", "/dav/My%20Drive/a%3Ab.txt", new StringContent("x"))); // MatDrive would change the name
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(ada, "PUT", "/dav/loose.txt", new StringContent("x"))); // nothing goes next to the three folders
    }

    [DbFact]
    public async Task Get_answers_ranges_and_conditions_and_never_serves_a_page()
    {
        var ada = await PersonAsync("Ada");
        await PutTextAsync(ada, "/dav/My%20Drive/digits.txt", "0123456789");

        using (var range = await SendAsync(ada, "GET", "/dav/My%20Drive/digits.txt", null, ("Range", "bytes=2-5")))
        {
            Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode);
            Assert.Equal("2345", await range.Content.ReadAsStringAsync());
            Assert.Equal("bytes 2-5/10", range.Content.Headers.ContentRange!.ToString());
        }

        using (var head = await SendAsync(ada, "HEAD", "/dav/My%20Drive/digits.txt"))
        {
            Assert.Equal(HttpStatusCode.OK, head.StatusCode);
            Assert.Equal(10, head.Content.Headers.ContentLength);
            Assert.Equal(Tag("0123456789"), head.Headers.ETag!.Tag);
            Assert.NotNull(head.Content.Headers.LastModified);
            Assert.Empty(await head.Content.ReadAsByteArrayAsync());
        }

        Assert.Equal(HttpStatusCode.NotModified, await StatusAsync(ada, "GET", "/dav/My%20Drive/digits.txt", null, ("If-None-Match", Tag("0123456789"))));

        // what could run as a page of the site is never served as its type
        await PutTextAsync(ada, "/dav/My%20Drive/page.html", "<script>alert(1)</script>");
        using (var html = await SendAsync(ada, "GET", "/dav/My%20Drive/page.html"))
        {
            Assert.Equal("application/octet-stream", html.Content.Headers.ContentType!.MediaType);
            Assert.Contains("sandbox", html.Headers.GetValues("Content-Security-Policy").Single());
        }

        // a folder in a browser: a plain list
        using var listing = await SendAsync(ada, "GET", "/dav/My%20Drive/");
        Assert.Equal("text/html", listing.Content.Headers.ContentType!.MediaType);
        Assert.Contains("digits.txt", await listing.Content.ReadAsStringAsync());
    }

    [DbFact]
    public async Task Mkcol_makes_a_folder()
    {
        var ada = await PersonAsync("Ada");
        Assert.Equal(HttpStatusCode.Created, await StatusAsync(ada, "MKCOL", "/dav/My%20Drive/New%20folder"));
        Assert.Equal(HttpStatusCode.Created, await StatusAsync(ada, "MKCOL", "/dav/My%20Drive/New%20folder/Child/"));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, await StatusAsync(ada, "MKCOL", "/dav/My%20Drive/new%20FOLDER")); // names are unique without regard to case
        Assert.Equal(HttpStatusCode.Conflict, await StatusAsync(ada, "MKCOL", "/dav/My%20Drive/Missing/Child"));
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(ada, "MKCOL", "/dav/Elsewhere"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, await StatusAsync(ada, "MKCOL", "/dav/My%20Drive/With%20body", new StringContent("<x/>")));
        Assert.Equal(new[] { "/dav/My%20Drive/New%20folder/", "/dav/My%20Drive/New%20folder/Child/" }, Hrefs((await PropFindAsync(ada, "/dav/My%20Drive/New%20folder/", "1")).Xml!));
    }

    [DbFact]
    public async Task Move_and_copy_follow_the_destination_and_overwrite()
    {
        var ada = await PersonAsync("Ada");
        await PutTextAsync(ada, "/dav/My%20Drive/a.txt", "A");
        await PutTextAsync(ada, "/dav/My%20Drive/c.txt", "C");
        Assert.Equal(HttpStatusCode.Created, await StatusAsync(ada, "MKCOL", "/dav/My%20Drive/Folder"));

        // a rename is a MOVE in the same folder; the destination as an absolute URL
        Assert.Equal(HttpStatusCode.Created, await MoveAsync(ada, "/dav/My%20Drive/a.txt", "http://localhost/dav/My%20Drive/b.txt"));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(ada, "GET", "/dav/My%20Drive/a.txt"));
        Assert.Equal("A", await GetTextAsync(ada, "/dav/My%20Drive/b.txt"));

        // Overwrite: F keeps what is there; T puts it into the trash first
        Assert.Equal(HttpStatusCode.PreconditionFailed, await CopyAsync(ada, "/dav/My%20Drive/b.txt", "/dav/My%20Drive/c.txt", ("Overwrite", "F")));
        Assert.Equal(HttpStatusCode.NoContent, await CopyAsync(ada, "/dav/My%20Drive/b.txt", "/dav/My%20Drive/c.txt", ("Overwrite", "T")));
        Assert.Equal("A", await GetTextAsync(ada, "/dav/My%20Drive/c.txt"));
        Assert.Equal("A", await GetTextAsync(ada, "/dav/My%20Drive/b.txt"));
        await using (var db = server.NewDb())
        {
            Assert.True(await db.Nodes.AnyAsync(n => n.OwnerId == ada.Id && n.Name == "c.txt" && n.TrashedDate != null));
        }

        // into a folder under a new name, and a folder with everything in it
        Assert.Equal(HttpStatusCode.Created, await MoveAsync(ada, "/dav/My%20Drive/b.txt", "/dav/My%20Drive/Folder/b2.txt"));
        Assert.Equal(HttpStatusCode.Created, await CopyAsync(ada, "/dav/My%20Drive/Folder/", "/dav/My%20Drive/Folder%20copy/"));
        Assert.Equal("A", await GetTextAsync(ada, "/dav/My%20Drive/Folder%20copy/b2.txt"));
        Assert.Equal("A", await GetTextAsync(ada, "/dav/My%20Drive/Folder/b2.txt"));

        // a change of case only is a rename too
        Assert.Equal(HttpStatusCode.Created, await MoveAsync(ada, "/dav/My%20Drive/c.txt", "/dav/My%20Drive/C.TXT"));
        Assert.Contains("/dav/My%20Drive/C.TXT", Hrefs((await PropFindAsync(ada, "/dav/My%20Drive/", "1")).Xml!));

        // what cannot be done
        Assert.Equal(HttpStatusCode.Conflict, await MoveAsync(ada, "/dav/My%20Drive/Folder/", "/dav/My%20Drive/Folder/Inside/")); // into itself
        Assert.Equal(HttpStatusCode.Forbidden, await MoveAsync(ada, "/dav/My%20Drive/Folder/b2.txt", "/dav/My%20Drive/Folder", ("Overwrite", "T"))); // replacing the folder it is in
        Assert.Equal(HttpStatusCode.Conflict, await MoveAsync(ada, "/dav/My%20Drive/C.TXT", "/dav/My%20Drive/Missing/c.txt"));
        Assert.Equal(HttpStatusCode.BadGateway, await MoveAsync(ada, "/dav/My%20Drive/C.TXT", "http://localhost/files/1"));
        Assert.Equal(HttpStatusCode.Forbidden, await MoveAsync(ada, "/dav/My%20Drive/C.TXT", "/dav/My%20Drive/C.TXT"));
        Assert.Equal(HttpStatusCode.Forbidden, await MoveAsync(ada, "/dav/My%20Drive/", "/dav/Other/"));
        Assert.Equal(HttpStatusCode.Forbidden, await MoveAsync(ada, "/dav/My%20Drive/C.TXT", "/dav/Shared%20drives/C.TXT"));
        Assert.Equal("A", await GetTextAsync(ada, "/dav/My%20Drive/Folder/b2.txt"));
    }

    [DbFact]
    public async Task Delete_puts_an_item_into_the_trash_and_the_activity_says_webdav_once()
    {
        var ada = await PersonAsync("Ada");
        Assert.Equal(HttpStatusCode.Created, await StatusAsync(ada, "MKCOL", "/dav/My%20Drive/Old"));
        await PutTextAsync(ada, "/dav/My%20Drive/Old/a.txt", "A");
        Assert.Equal(HttpStatusCode.Created, await MoveAsync(ada, "/dav/My%20Drive/Old/a.txt", "/dav/My%20Drive/Old/b.txt"));

        Assert.Equal(HttpStatusCode.NoContent, await StatusAsync(ada, "DELETE", "/dav/My%20Drive/Old"));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(ada, "GET", "/dav/My%20Drive/Old/b.txt"));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(ada, "DELETE", "/dav/My%20Drive/Old"));
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(ada, "DELETE", "/dav/My%20Drive/"));

        await using var db = server.NewDb();
        var old = await db.Nodes.SingleAsync(n => n.OwnerId == ada.Id && n.Name == "Old");
        Assert.NotNull(old.TrashedDate);
        Assert.True(await db.Nodes.Where(n => n.OwnerId == ada.Id && n.Name == "b.txt").AllAsync(n => n.InTrash));

        var activity = await db.Activities.Where(a => a.UserId == ada.Id).OrderBy(a => a.Id).Select(a => new { a.Action, a.Detail }).ToListAsync();
        Assert.Equal(new[] { "create", "upload", "rename", "trash" }, activity.Select(a => a.Action));
        Assert.All(activity, a => Assert.EndsWith("webdav", a.Detail));
    }

    [DbFact]
    public async Task A_viewer_reads_but_cannot_write_and_a_share_opens_one_folder()
    {
        var ada = await PersonAsync("Ada");
        var bob = await PersonAsync("Bob");
        var carol = await PersonAsync("Carol");
        var (driveId, team) = await SharedDriveAsync(ada, "Team", (bob, AccessRole.Viewer));
        await PutTextAsync(ada, team + "plan.txt", "plan");
        Assert.Equal(HttpStatusCode.Created, await StatusAsync(ada, "MKCOL", team + "Shared"));
        await PutTextAsync(ada, team + "Shared/inside.txt", "inside");

        // Bob sees the drive and reads, but changes nothing
        Assert.Contains(team, Hrefs((await PropFindAsync(bob, "/dav/Shared%20drives/", "1")).Xml!));
        Assert.Equal("plan", await GetTextAsync(bob, team + "plan.txt"));
        Assert.Equal("1", Found((await PropFindAsync(bob, team + "plan.txt", "0", PropBody("isreadonly"))).Xml!, 0).Element(D + "isreadonly")!.Value);
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(bob, "PUT", team + "plan.txt", new StringContent("x")));
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(bob, "PUT", team + "new.txt", new StringContent("x")));
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(bob, "MKCOL", team + "Mine"));
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(bob, "DELETE", team + "plan.txt"));
        Assert.Equal(HttpStatusCode.Forbidden, await MoveAsync(bob, team + "plan.txt", team + "plan2.txt"));
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(bob, "LOCK", team + "plan.txt", Xml(LockBody(exclusive: true))));
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(bob, "PROPPATCH", team + "plan.txt", Xml(Win32Patch)));
        Assert.Equal("plan", await GetTextAsync(ada, team + "plan.txt"));

        // somebody who is not a member does not find the drive
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(carol, "GET", team + "plan.txt"));

        // a folder shared with Carol as an editor lies in "Shared with me", and she may write in it
        await using (var db = server.NewDb())
        {
            var folder = await db.Nodes.SingleAsync(n => n.DriveId == driveId && n.Name == "Shared");
            Assert.True((await ShareService.AddAsync(db, Actor.Of(ada.Id), folder.Id, carol.Email, AccessRole.Editor)).Ok);
        }

        Assert.Contains("/dav/Shared%20with%20me/Shared/", Hrefs((await PropFindAsync(carol, "/dav/Shared%20with%20me/", "1")).Xml!));
        Assert.Equal("inside", await GetTextAsync(carol, "/dav/Shared%20with%20me/Shared/inside.txt"));
        Assert.Equal(HttpStatusCode.Created, await StatusAsync(carol, "PUT", "/dav/Shared%20with%20me/Shared/from-carol.txt", new StringContent("hi")));
        Assert.Equal("hi", await GetTextAsync(ada, team + "Shared/from-carol.txt"));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(carol, "GET", "/dav/Shared%20with%20me/plan.txt"));
    }

    [DbFact]
    public async Task A_full_drive_answers_507_and_a_file_above_the_limit_413()
    {
        var ada = await PersonAsync("Ada", quota: 100);
        await PutTextAsync(ada, "/dav/My%20Drive/small.txt", new string('s', 60));

        // announced: refused before the bytes; streamed without a length: refused after them; nothing is kept
        Assert.Equal(HttpStatusCode.InsufficientStorage, await StatusAsync(ada, "PUT", "/dav/My%20Drive/big.txt", new ByteArrayContent(new byte[50])));
        Assert.Equal(HttpStatusCode.InsufficientStorage, await StatusAsync(ada, "PUT", "/dav/My%20Drive/big.txt", new UnknownLengthContent(new byte[50])));
        Assert.Equal(HttpStatusCode.InsufficientStorage, await CopyAsync(ada, "/dav/My%20Drive/small.txt", "/dav/My%20Drive/small%20copy.txt"));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(ada, "GET", "/dav/My%20Drive/big.txt"));
        Assert.Equal(new string('s', 60), await GetTextAsync(ada, "/dav/My%20Drive/small.txt"));

        await using (var db = server.NewDb())
        {
            var settings = await SettingsStore.LoadAsync(db);
            settings.MaxUploadBytes = 10;
            await db.SaveChangesAsync();
        }

        try
        {
            var bob = await PersonAsync("Bob");
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, await StatusAsync(bob, "PUT", "/dav/My%20Drive/b.txt", new ByteArrayContent(new byte[20])));
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, await StatusAsync(bob, "PUT", "/dav/My%20Drive/b.txt", new UnknownLengthContent(new byte[20])));
            Assert.Equal(HttpStatusCode.Created, await StatusAsync(bob, "PUT", "/dav/My%20Drive/b.txt", new ByteArrayContent(new byte[10])));
        }
        finally
        {
            await using var db = server.NewDb();
            await db.SystemSettings.Where(s => s.Id == 1).ExecuteUpdateAsync(s => s.SetProperty(x => x.MaxUploadBytes, 0L));
        }
    }

    [DbFact]
    public async Task A_lock_keeps_others_out_until_it_ends()
    {
        var ada = await PersonAsync("Ada");
        var bob = await PersonAsync("Bob");
        var (_, team) = await SharedDriveAsync(ada, "Locks", (bob, AccessRole.Editor));
        var file = team + "report.docx";
        await PutTextAsync(ada, file, "v1");

        string token;
        using (var locked = await SendAsync(ada, "LOCK", file, Xml(LockBody(exclusive: true)), ("Timeout", "Second-600")))
        {
            Assert.Equal(HttpStatusCode.OK, locked.StatusCode);
            token = locked.Headers.GetValues("Lock-Token").Single().Trim('<', '>');
            Assert.StartsWith(DavLockManager.TokenPrefix, token);
            Assert.Contains(token, await locked.Content.ReadAsStringAsync());
        }

        // Bob can neither write nor lock nor delete, not even with Ada's token; Ada writes with her token only
        Assert.Equal(HttpStatusCode.Locked, await StatusAsync(bob, "PUT", file, new StringContent("bob")));
        Assert.Equal(HttpStatusCode.Locked, await StatusAsync(bob, "LOCK", file, Xml(LockBody(exclusive: false))));
        Assert.Equal(HttpStatusCode.Locked, await StatusAsync(bob, "DELETE", file));
        Assert.Equal(HttpStatusCode.Locked, await MoveAsync(bob, file, team + "moved.docx"));
        Assert.Equal(HttpStatusCode.Locked, await StatusAsync(bob, "PUT", file, new StringContent("bob"), ("If", $"(<{token}>)")));
        Assert.Equal(HttpStatusCode.Locked, await StatusAsync(ada, "PUT", file, new StringContent("v2")));
        Assert.Equal(HttpStatusCode.PreconditionFailed, await StatusAsync(ada, "PUT", file, new StringContent("v2"), ("If", "(<opaquelocktoken:not-a-lock-of-this-file>)")));
        Assert.Equal(HttpStatusCode.NoContent, await StatusAsync(ada, "PUT", file, new StringContent("v2"), ("If", $"(<{token}>)")));
        Assert.Equal(HttpStatusCode.NoContent, await StatusAsync(ada, "PUT", file, new StringContent("v3"), ("If", $"<http://localhost{file}> (<{token}>)")));

        // the lock shows in the properties and can be refreshed
        var discovery = Found((await PropFindAsync(bob, file, "0", PropBody("lockdiscovery", "supportedlock"))).Xml!, 0);
        Assert.Equal(token, discovery.Descendants(D + "locktoken").Single().Element(D + "href")!.Value);
        Assert.Equal(2, discovery.Descendants(D + "lockentry").Count());
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(ada, "LOCK", file, null, ("If", $"(<{token}>)"), ("Timeout", "Second-300")));
        Assert.Equal(HttpStatusCode.PreconditionFailed, await StatusAsync(bob, "LOCK", file, null, ("If", $"(<{token}>)")));

        // only Ada ends it
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(bob, "UNLOCK", file, null, ("Lock-Token", $"<{token}>")));
        Assert.Equal(HttpStatusCode.NoContent, await StatusAsync(ada, "UNLOCK", file, null, ("Lock-Token", $"<{token}>")));
        Assert.Equal(HttpStatusCode.Conflict, await StatusAsync(ada, "UNLOCK", file, null, ("Lock-Token", $"<{token}>")));
        Assert.Equal(HttpStatusCode.NoContent, await StatusAsync(bob, "PUT", file, new StringContent("bob")));
        Assert.Equal("bob", await GetTextAsync(ada, file));
    }

    [DbFact]
    public async Task A_lock_on_a_new_name_makes_an_empty_file_and_a_folder_lock_covers_its_members()
    {
        var ada = await PersonAsync("Ada");
        var bob = await PersonAsync("Bob");
        var (_, team) = await SharedDriveAsync(ada, "Deep", (bob, AccessRole.Editor));

        // a lock on a name that is free (the Finder, Office): an empty file that the next PUT fills, still one version
        string token;
        using (var locked = await SendAsync(ada, "LOCK", team + "new.txt", Xml(LockBody(exclusive: true))))
        {
            Assert.Equal(HttpStatusCode.Created, locked.StatusCode);
            token = locked.Headers.GetValues("Lock-Token").Single().Trim('<', '>');
        }

        Assert.Equal(string.Empty, await GetTextAsync(bob, team + "new.txt"));
        Assert.Equal(HttpStatusCode.Locked, await StatusAsync(bob, "PUT", team + "new.txt", new StringContent("bob")));
        Assert.Equal(HttpStatusCode.NoContent, await StatusAsync(ada, "PUT", team + "new.txt", new StringContent("content"), ("If", $"(<{token}>)")));
        Assert.Equal("content", await GetTextAsync(bob, team + "new.txt"));
        Assert.Equal(1, await VersionCountAsync(await NodeIdAsync(ada, "new.txt")));
        Assert.Equal(HttpStatusCode.NoContent, await StatusAsync(ada, "UNLOCK", team + "new.txt", null, ("Lock-Token", $"<{token}>")));

        // a folder locked with depth infinity: what is in it, and what is added to it, needs the token
        Assert.Equal(HttpStatusCode.Created, await StatusAsync(ada, "MKCOL", team + "Folder"));
        await PutTextAsync(ada, team + "Folder/a.txt", "a");
        using (var deep = await SendAsync(ada, "LOCK", team + "Folder/", Xml(LockBody(exclusive: true)), ("Depth", "infinity")))
        {
            Assert.Equal(HttpStatusCode.OK, deep.StatusCode);
            token = deep.Headers.GetValues("Lock-Token").Single().Trim('<', '>');
        }

        Assert.Equal(HttpStatusCode.Locked, await StatusAsync(bob, "PUT", team + "Folder/a.txt", new StringContent("b")));
        Assert.Equal(HttpStatusCode.Locked, await StatusAsync(bob, "PUT", team + "Folder/b.txt", new StringContent("b")));
        Assert.Equal(HttpStatusCode.Locked, await StatusAsync(bob, "MKCOL", team + "Folder/Sub"));
        Assert.Equal(HttpStatusCode.Locked, await StatusAsync(bob, "DELETE", team + "Folder/a.txt"));
        Assert.Equal(HttpStatusCode.Locked, await MoveAsync(bob, team + "Folder/a.txt", team + "a.txt"));
        Assert.Equal(HttpStatusCode.Locked, await StatusAsync(bob, "LOCK", team + "Folder/a.txt", Xml(LockBody(exclusive: false))));
        Assert.Equal(HttpStatusCode.Created, await StatusAsync(ada, "PUT", team + "Folder/b.txt", new StringContent("b"), ("If", $"(<{token}>)")));
        Assert.Equal(HttpStatusCode.Locked, await StatusAsync(ada, "DELETE", team + "Folder"));
        Assert.Equal(HttpStatusCode.NoContent, await StatusAsync(ada, "DELETE", team + "Folder", null, ("If", $"(<{token}>)")));
        Assert.Equal(HttpStatusCode.Conflict, await StatusAsync(ada, "UNLOCK", team + "Folder/", null, ("Lock-Token", $"<{token}>"))); // the lock went with the folder
    }

    [DbFact]
    public async Task Proppatch_is_answered_for_the_explorer_and_the_properties_of_the_server_are_refused()
    {
        var ada = await PersonAsync("Ada");
        await PutTextAsync(ada, "/dav/My%20Drive/a.txt", "A");

        using (var ok = await SendAsync(ada, "PROPPATCH", "/dav/My%20Drive/a.txt", Xml(Win32Patch)))
        {
            Assert.Equal(HttpStatusCode.MultiStatus, ok.StatusCode);
            var statuses = XDocument.Parse(await ok.Content.ReadAsStringAsync()).Descendants(D + "status").Select(s => s.Value).ToList();
            Assert.Equal(new[] { "HTTP/1.1 200 OK" }, statuses);
        }

        const string serverProperty = "<D:propertyupdate xmlns:D=\"DAV:\" xmlns:Z=\"urn:schemas-microsoft-com:\"><D:set><D:prop><D:getetag>x</D:getetag>" +
                                      "<Z:Win32FileAttributes>00000020</Z:Win32FileAttributes></D:prop></D:set></D:propertyupdate>";
        using var refused = await SendAsync(ada, "PROPPATCH", "/dav/My%20Drive/a.txt", Xml(serverProperty));
        Assert.Equal(HttpStatusCode.MultiStatus, refused.StatusCode);
        var all = XDocument.Parse(await refused.Content.ReadAsStringAsync()).Descendants(D + "status").Select(s => s.Value).ToList();
        Assert.Contains("HTTP/1.1 403 Forbidden", all);
        Assert.Contains("HTTP/1.1 424 Failed Dependency", all);
    }

    [DbFact]
    public async Task A_decomposed_name_finds_its_file_and_names_are_unique_without_regard_to_case()
    {
        var ada = await PersonAsync("Ada");
        await PutTextAsync(ada, "/dav/My%20Drive/%C3%A4rger%20%F0%9F%8E%89.txt", "x"); // "ärger 🎉.txt", composed
        Assert.Equal("x", await GetTextAsync(ada, "/dav/My%20Drive/a%CC%88rger%20%F0%9F%8E%89.txt")); // the Finder decomposes the umlaut
        Assert.Contains("/dav/My%20Drive/%C3%A4rger%20%F0%9F%8E%89.txt", Hrefs((await PropFindAsync(ada, "/dav/My%20Drive/", "1")).Xml!));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, await StatusAsync(ada, "MKCOL", "/dav/My%20Drive/a%CC%88RGER%20%F0%9F%8E%89.TXT"));
        Assert.Equal(HttpStatusCode.NoContent, await StatusAsync(ada, "PUT", "/dav/My%20Drive/a%CC%88RGER%20%F0%9F%8E%89.TXT", new StringContent("y")));
        Assert.Equal("y", await GetTextAsync(ada, "/dav/My%20Drive/%C3%A4rger%20%F0%9F%8E%89.txt"));
    }

    [DbFact]
    public async Task A_folder_of_5000_items_is_listed_with_a_few_queries()
    {
        var ada = await PersonAsync("Ada");
        Assert.Equal(HttpStatusCode.Created, await StatusAsync(ada, "MKCOL", "/dav/My%20Drive/Many"));
        await using (var db = server.NewDb())
        {
            var folder = await db.Nodes.SingleAsync(n => n.OwnerId == ada.Id && n.Name == "Many");
            var now = DateTime.UtcNow;
            db.Nodes.AddRange(Enumerable.Range(1, 5000).Select(i => new Node
            {
                DriveId = folder.DriveId,
                ParentId = folder.Id,
                Kind = NodeKind.File,
                Name = $"file {i:D4}.txt",
                Size = i,
                MimeType = "text/plain",
                OwnerId = ada.Id,
                ModifiedDate = now,
                Path = folder.Path,
            }));
            await db.SaveChangesAsync();
            var path = folder.Path;
            await db.Nodes.Where(n => n.ParentId == folder.Id).ExecuteUpdateAsync(s => s.SetProperty(n => n.Path, n => path + n.Id.ToString() + "/"));
        }

        server.Commands.Reset();
        var watch = Stopwatch.StartNew();
        var (status, xml) = await PropFindAsync(ada, "/dav/My%20Drive/Many/", "1");
        watch.Stop();

        Assert.Equal(HttpStatusCode.MultiStatus, status);
        Assert.Equal(5001, xml!.Root!.Elements(D + "response").Count());
        Assert.InRange(server.Commands.Count, 1, 15); // a few queries, not one per item
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"PROPFIND of 5000 items took {watch.Elapsed}");
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------------------

    /// <summary>A new person (a unique address, no password of their own) with an app password.</summary>
    private async Task<Person> PersonAsync(string name, long? quota = null)
    {
        await using var db = server.NewDb();
        var user = new User { Email = $"{name.ToLowerInvariant()}.{Guid.NewGuid():N}@example.com", DisplayName = name, PasswordHash = string.Empty, QuotaBytes = quota };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var password = await AppPasswords.CreateAsync(db, user.Id, "WebDAV");
        return new Person(user.Id, user.Email, password.Value!);
    }

    /// <summary>A shared drive of <paramref name="manager"/> with members; answers its id and its WebDAV address (with a slash at the end).</summary>
    private async Task<(long DriveId, string Href)> SharedDriveAsync(Person manager, string name, params (Person Person, AccessRole Role)[] members)
    {
        var unique = $"{name} {manager.Id}";
        await using var db = server.NewDb();
        var drive = (await DriveService.CreateSharedAsync(db, manager.Id, new DriveInput(unique, null, null))).Value!;
        foreach (var (person, role) in members)
        {
            Assert.True((await DriveService.AddMemberAsync(db, manager.Id, drive.Id, person.Email, role)).Ok);
        }

        return (drive.Id, "/dav/Shared%20drives/" + Uri.EscapeDataString(unique) + "/");
    }

    private async Task<HttpResponseMessage> SendAsync(Person? who, string method, string path, HttpContent? content = null, params (string Name, string Value)[] headers)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = content };
        if (who is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(who.Email + ":" + who.Password)));
        }

        foreach (var (name, value) in headers)
        {
            Assert.True(request.Headers.TryAddWithoutValidation(name, value), name);
        }

        return await server.Client.SendAsync(request);
    }

    private async Task<HttpStatusCode> StatusAsync(Person who, string method, string path, HttpContent? content = null, params (string Name, string Value)[] headers)
    {
        using var response = await SendAsync(who, method, path, content, headers);
        return response.StatusCode;
    }

    private Task<HttpStatusCode> MoveAsync(Person who, string from, string to, params (string Name, string Value)[] headers)
        => StatusAsync(who, "MOVE", from, null, [("Destination", to), .. headers]);

    private Task<HttpStatusCode> CopyAsync(Person who, string from, string to, params (string Name, string Value)[] headers)
        => StatusAsync(who, "COPY", from, null, [("Destination", to), .. headers]);

    private async Task<(HttpStatusCode Status, XDocument? Xml)> PropFindAsync(Person who, string path, string depth, string? body = null)
    {
        using var response = await SendAsync(who, "PROPFIND", path, body is null ? null : Xml(body), ("Depth", depth));
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, response.StatusCode == HttpStatusCode.MultiStatus ? XDocument.Parse(text) : null);
    }

    private async Task PutTextAsync(Person who, string path, string text)
    {
        var status = await StatusAsync(who, "PUT", path, new StringContent(text));
        Assert.True(status is HttpStatusCode.Created or HttpStatusCode.NoContent, $"PUT {path}: {(int)status}");
    }

    private async Task<string> GetTextAsync(Person who, string path)
    {
        using var response = await SendAsync(who, "GET", path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private async Task<long> NodeIdAsync(Person owner, string name)
    {
        await using var db = server.NewDb();
        return await db.Nodes.Where(n => n.OwnerId == owner.Id && n.Name == name && !n.InTrash).Select(n => n.Id).SingleAsync();
    }

    private async Task<int> VersionCountAsync(long nodeId)
    {
        await using var db = server.NewDb();
        return await db.NodeVersions.CountAsync(v => v.NodeId == nodeId);
    }

    /// <summary>Makes the versions of a file an hour older (the next save is a new version, not a correction of the last one).</summary>
    private async Task AgeVersionsAsync(long nodeId)
    {
        await using var db = server.NewDb();
        await db.NodeVersions.Where(v => v.NodeId == nodeId).ExecuteUpdateAsync(s => s.SetProperty(v => v.CreateDate, v => v.CreateDate.AddHours(-1)));
    }

    /// <summary>The entity tag of a text: its SHA-256 in quotes.</summary>
    private static string Tag(string text) => "\"" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))) + "\"";

    private static IReadOnlyList<string> Hrefs(XDocument xml) => xml.Root!.Elements(D + "response").Select(r => r.Element(D + "href")!.Value).ToList();

    /// <summary>The properties that were found for the response at <paramref name="index"/>.</summary>
    private static XElement Found(XDocument xml, int index)
        => xml.Root!.Elements(D + "response").ElementAt(index).Elements(D + "propstat")
            .Single(p => p.Element(D + "status")!.Value == "HTTP/1.1 200 OK").Element(D + "prop")!;

    private static string PropBody(params string[] names)
        => "<?xml version=\"1.0\" encoding=\"utf-8\"?><D:propfind xmlns:D=\"DAV:\"><D:prop>" + string.Concat(names.Select(n => $"<D:{n}/>")) + "</D:prop></D:propfind>";

    private static string LockBody(bool exclusive)
        => "<?xml version=\"1.0\" encoding=\"utf-8\"?><D:lockinfo xmlns:D=\"DAV:\"><D:lockscope><D:" + (exclusive ? "exclusive" : "shared") +
           "/></D:lockscope><D:locktype><D:write/></D:locktype><D:owner><D:href>tests</D:href></D:owner></D:lockinfo>";

    private static StringContent Xml(string body) => new(body, Encoding.UTF8, "application/xml");

    /// <summary>A body without a known length (the client streams it, like the Finder).</summary>
    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
