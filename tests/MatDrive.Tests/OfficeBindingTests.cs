using System.Net;
using System.Text;
using System.Text.Json;
using MatDrive.Data;
using MatDrive.Platform;
using MatDrive.Services;
using MatDrive.Services.Files;
using MatDrive.Services.Office;
using MatPlatform;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MatDrive.Tests;

/// <summary>
/// The office editor through the capability <c>office.edit</c> (MatPlatform 2.0): bound by the variables, entered by hand, or not at all; the formats are fetched per
/// binding; the settings of the old page Integrations become the binding by hand. The editor is a fake that answers like MatOffice.
/// </summary>
public sealed class OfficeBindingTests
{
    private const string Key = "mat_test-key";

    [Fact]
    public void The_formats_belong_to_one_binding_and_are_fetched_again_when_due()
    {
        var office = Binding("http://office-a:4820", Key);
        var now = DateTime.UtcNow;
        Assert.Equal(32, OfficeService.Fingerprint(office).Length);
        Assert.NotEqual(OfficeService.Fingerprint(office), OfficeService.Fingerprint(Binding("http://office-a:4820", "mat_other")));
        Assert.NotEqual(OfficeService.Fingerprint(office), OfficeService.Fingerprint(Binding("http://office-b:4820", Key)));
        Assert.Equal(string.Empty, OfficeService.Fingerprint(MatBinding.None(OfficeService.Capability)));

        var fetched = new SystemSetting { OfficeFormatsBinding = OfficeService.Fingerprint(office), OfficeCheckedDate = now.AddHours(-1) };
        Assert.False(OfficeService.NeedsRefresh(fetched, office, now));
        Assert.True(OfficeService.NeedsRefresh(fetched, office, now.AddHours(6)));
        Assert.True(OfficeService.NeedsRefresh(fetched, Binding("http://office-a:4820", "mat_new"), now));
        Assert.False(OfficeService.NeedsRefresh(fetched, MatBinding.None(OfficeService.Capability), now));

        var failed = new SystemSetting { OfficeFormatsBinding = OfficeService.Fingerprint(office), OfficeCheckedDate = now.AddMinutes(-1), OfficeProblem = "Unreachable" };
        Assert.False(OfficeService.NeedsRefresh(failed, office, now));
        Assert.True(OfficeService.NeedsRefresh(failed, office, now.AddMinutes(5)));
        Assert.True(OfficeService.NeedsRefresh(new SystemSetting(), office, now));
    }

    [DbFact]
    public async Task Without_a_binding_office_files_open_in_the_preview()
    {
        await using var t = await TestDatabase.CreateAsync();
        using var app = Platform.Create();
        var office = await app.Bindings.GetAsync(OfficeService.Capability);
        Assert.False(office.IsBound);
        Assert.Equal(MatBindingSource.None, office.Source);
        Assert.Empty(await OfficeService.ActiveKindsAsync(t.Db, app.Bindings));
        Assert.False((await OfficeService.StatusAsync(t.Db, office)).Bound);

        var fake = new FakeOffice("http://office-none:4820");
        Assert.False((await OfficeService.RefreshAsync(t.Db, fake.Client(), office, force: true)).Ok);
        Assert.Empty(fake.Calls);

        var (ada, file) = await FileAsync(t, "Report.docx");
        var opened = await OfficeService.OpenAsync(t.Db, app.Protection, fake.Client(), office, app.Settings, Actor.Of(ada), file, "http://localhost:4810", "en");
        Assert.False(opened.Ok);
        Assert.Empty(fake.Calls);
    }

    [DbFact]
    public async Task A_binding_from_the_variables_is_called_at_its_internal_address_with_its_key()
    {
        await using var t = await TestDatabase.CreateAsync();
        using var app = Platform.Create(new Dictionary<string, string?>
        {
            ["MAT_OFFICE_EDIT_URL"] = "https://office.example.com/",
            ["MAT_OFFICE_EDIT_INTERNAL_URL"] = "http://office-vars:4820",
            ["MAT_OFFICE_EDIT_KEY"] = Key,
            ["MAT_PUBLIC_URL"] = "https://drive.example.com",
            ["MAT_INTERNAL_URL"] = "http://mattiverse-matdrive-web:4810",
        });
        var office = await app.Bindings.GetAsync(OfficeService.Capability);
        Assert.Equal(MatBindingSource.Variables, office.Source);
        Assert.True(app.Bindings.IsFixed(OfficeService.Capability));

        // nothing is used before the formats of this binding are there
        Assert.Empty(await OfficeService.ActiveKindsAsync(t.Db, app.Bindings));
        Assert.True((await OfficeService.StatusAsync(t.Db, office)).Pending);

        var fake = new FakeOffice("http://office-vars:4820");
        var fetched = await OfficeService.RefreshAsync(t.Db, fake.Client(), office);
        Assert.True(fetched.Ok, fetched.Error);
        Assert.Equal(["docs", "sheets"], (await OfficeService.ActiveKindsAsync(t.Db, app.Bindings)).Select(k => k.Kind));
        var call = Assert.Single(fake.Calls);
        Assert.Equal("GET http://office-vars:4820/api/provides/office.edit/formats", call.Line);
        Assert.Equal(Key, call.Key);

        // not due again: no call
        Assert.True((await OfficeService.RefreshAsync(t.Db, fake.Client(), office)).Ok);
        Assert.Single(fake.Calls);

        // a session: the editor reads the file at MAT_INTERNAL_URL, the browser comes back to MAT_PUBLIC_URL and goes to the public address of the editor
        var (ada, file) = await FileAsync(t, "Report.docx");
        var opened = await OfficeService.OpenAsync(t.Db, app.Protection, fake.Client(), office, app.Settings, Actor.Of(ada), file, "http://localhost:4810", "en");
        Assert.True(opened.Ok, opened.Error);
        Assert.Equal("https://office.example.com/docs/edit/1?session=s1", opened.Value);
        var session = fake.Calls[^1];
        Assert.Equal("POST http://office-vars:4820/api/provides/office.edit/sessions", session.Line);
        Assert.Equal(Key, session.Key);
        using var body = JsonDocument.Parse(session.Body!);
        Assert.Equal($"http://mattiverse-matdrive-web:4810/api/v1/office/files/{file}/content", body.RootElement.GetProperty("contentUrl").GetString());
        Assert.StartsWith("https://drive.example.com/drive", body.RootElement.GetProperty("returnUrl").GetString(), StringComparison.Ordinal);
        Assert.True(body.RootElement.GetProperty("canWrite").GetBoolean());

        // the token of the session opens exactly this file
        var token = body.RootElement.GetProperty("accessToken").GetString();
        Assert.Equal((file, ada, true), OfficeService.ReadToken(app.Protection, token, file));
        Assert.Null(OfficeService.ReadToken(app.Protection, token, file + 1));
    }

    [DbFact]
    public async Task A_binding_entered_by_hand_is_used_and_a_new_key_needs_its_formats_again()
    {
        await using var t = await TestDatabase.CreateAsync();
        using var app = Platform.Create();
        Assert.True(app.Bindings.CanBindByHand);
        Assert.Empty(await app.Bindings.SaveByHandAsync(OfficeService.Capability, Values("http://office-hand:4820/", null, Key), "en"));
        var office = await app.Bindings.GetAsync(OfficeService.Capability);
        Assert.Equal(MatBindingSource.ByHand, office.Source);
        Assert.Equal("http://office-hand:4820", office.ServerUrl);

        var fake = new FakeOffice("http://office-hand:4820");
        Assert.True((await OfficeService.RefreshAsync(t.Db, fake.Client(), office)).Ok);
        Assert.Equal(2, (await OfficeService.ActiveKindsAsync(t.Db, app.Bindings)).Count);

        // without an internal address the server calls go to the public one, and the content address is the request's (no address is configured)
        var (ada, file) = await FileAsync(t, "Budget.xlsx");
        var opened = await OfficeService.OpenAsync(t.Db, app.Protection, fake.Client(), office, app.Settings, Actor.Of(ada), file, "http://localhost:4810", "de");
        Assert.True(opened.Ok, opened.Error);
        Assert.StartsWith("http://office-hand:4820/docs/edit/", opened.Value, StringComparison.Ordinal);
        using (var body = JsonDocument.Parse(fake.Calls[^1].Body!))
        {
            Assert.Equal($"http://localhost:4810/api/v1/office/files/{file}/content", body.RootElement.GetProperty("contentUrl").GetString());
        }

        // a new key by hand: the formats of the old one are not used, and they are due
        Assert.Empty(await app.Bindings.SaveByHandAsync(OfficeService.Capability, Values("http://office-hand:4820", null, "mat_second"), "en"));
        var renewed = await app.Bindings.GetAsync(OfficeService.Capability);
        Assert.Empty(await OfficeService.ActiveKindsAsync(t.Db, app.Bindings));
        Assert.True(OfficeService.NeedsRefresh(await SettingsStore.LoadAsync(t.Db), renewed, DateTime.UtcNow));
        fake.Key = "mat_second";
        Assert.True((await OfficeService.RefreshAsync(t.Db, fake.Client(), renewed)).Ok);
        Assert.Equal(2, (await OfficeService.ActiveKindsAsync(t.Db, app.Bindings)).Count);

        // disconnected: nothing opens in the editor any more
        Assert.True(await app.Bindings.ClearByHandAsync(OfficeService.Capability));
        Assert.Empty(await OfficeService.ActiveKindsAsync(t.Db, app.Bindings));
    }

    [DbFact]
    public async Task A_failed_fetch_leaves_no_kinds_and_says_why()
    {
        await using var t = await TestDatabase.CreateAsync();
        using var app = Platform.Create();
        Assert.Empty(await app.Bindings.SaveByHandAsync(OfficeService.Capability, Values("http://office-wrong:4820", null, "mat_wrong"), "en"));
        var office = await app.Bindings.GetAsync(OfficeService.Capability);

        var fake = new FakeOffice("http://office-wrong:4820");
        var fetched = await OfficeService.RefreshAsync(t.Db, fake.Client(), office);
        Assert.False(fetched.Ok);
        var status = await OfficeService.StatusAsync(t.Db, office);
        Assert.Equal("KeyWrong", status.Problem);
        Assert.Empty(status.Kinds);
        Assert.False(status.Pending);
        Assert.Equal("MatOffice does not accept the key.", MatDrive.Localization.Languages.In("en", () => OfficeService.ProblemText(status.Problem!)));

        // tried at both places of the endpoints, then again only after five minutes
        Assert.Equal(2, fake.Calls.Count);
        Assert.False(OfficeService.NeedsRefresh(await SettingsStore.LoadAsync(t.Db), office, DateTime.UtcNow));
        Assert.True(OfficeService.NeedsRefresh(await SettingsStore.LoadAsync(t.Db), office, DateTime.UtcNow.AddMinutes(6)));

        // an address nobody answers at is "unreachable"
        Assert.Empty(await app.Bindings.SaveByHandAsync(OfficeService.Capability, Values("http://office-down:4820", null, Key), "en"));
        var down = await app.Bindings.GetAsync(OfficeService.Capability);
        Assert.False((await OfficeService.RefreshAsync(t.Db, new FakeOffice("http://office-down:4820") { Down = true }.Client(), down)).Ok);
        Assert.Equal("Unreachable", (await OfficeService.StatusAsync(t.Db, down)).Problem);
    }

    [DbFact]
    public async Task An_editor_from_before_the_bindings_is_reached_at_its_old_endpoints()
    {
        await using var t = await TestDatabase.CreateAsync();
        using var app = Platform.Create();
        Assert.Empty(await app.Bindings.SaveByHandAsync(OfficeService.Capability, Values("http://office-old:4820", null, Key), "en"));
        var office = await app.Bindings.GetAsync(OfficeService.Capability);

        var fake = new FakeOffice("http://office-old:4820") { OldPaths = true };
        Assert.True((await OfficeService.RefreshAsync(t.Db, fake.Client(), office)).Ok);
        Assert.Equal(["GET http://office-old:4820/api/provides/office.edit/formats", "GET http://office-old:4820/api/v1/host/formats"], fake.Calls.Select(c => c.Line));

        // the place that answered is remembered: the session goes there at once
        var (ada, file) = await FileAsync(t, "Report.docx");
        Assert.True((await OfficeService.OpenAsync(t.Db, app.Protection, fake.Client(), office, app.Settings, Actor.Of(ada), file, "http://localhost:4810", "en")).Ok);
        Assert.Equal("POST http://office-old:4820/api/v1/host/sessions", fake.Calls[^1].Line);
        Assert.Equal(3, fake.Calls.Count);
    }

    [DbFact]
    public async Task The_settings_of_the_old_page_become_the_binding_by_hand()
    {
        await using var t = await TestDatabase.CreateAsync();
        using var app = Platform.Create();
        await OldSettingsAsync(t.Db, app.Protection, enabled: true);

        Assert.Equal(OldOfficeSettings.Moved, await OfficeService.MoveOldSettingsAsync(t.Db, app.Protection, app.Bindings, app.Store));
        var office = await app.Bindings.GetAsync(OfficeService.Capability);
        Assert.Equal(MatBindingSource.ByHand, office.Source);
        Assert.Equal("http://localhost:4820", office.Url);
        Assert.Equal("http://host.docker.internal:4820", office.ServerUrl);
        Assert.Equal("mo_old-key", office.Key);

        // the old values are gone, the address of MatDrive for other apps is MatDrive's own and stays
        var s = await SettingsStore.LoadAsync(t.Db);
        await t.Db.Entry(s).ReloadAsync();
        Assert.False(s.OfficeEnabled);
        Assert.Null(s.OfficeUrl);
        Assert.Null(s.OfficeInternalUrl);
        Assert.Null(s.OfficeKeyProtected);
        Assert.Equal("http://host.docker.internal:4810", s.DriveUrlForOffice);

        // once: a second start finds nothing, and a disconnect by hand stays a disconnect
        Assert.True(await app.Bindings.ClearByHandAsync(OfficeService.Capability));
        Assert.Equal(OldOfficeSettings.None, await OfficeService.MoveOldSettingsAsync(t.Db, app.Protection, app.Bindings, app.Store));
        Assert.False((await app.Bindings.GetAsync(OfficeService.Capability)).IsBound);
    }

    [DbFact]
    public async Task Old_settings_wait_for_the_variables_to_go_and_switched_off_ones_are_dropped()
    {
        await using var t = await TestDatabase.CreateAsync();

        // the variables bind office.edit: the old values stay for later
        using (var bound = Platform.Create(new Dictionary<string, string?> { ["MAT_OFFICE_EDIT_URL"] = "https://office.example.com", ["MAT_OFFICE_EDIT_KEY"] = Key }))
        {
            await OldSettingsAsync(t.Db, bound.Protection, enabled: true);
            Assert.Equal(OldOfficeSettings.Kept, await OfficeService.MoveOldSettingsAsync(t.Db, bound.Protection, bound.Bindings, bound.Store));
            Assert.NotNull((await SettingsStore.LoadAsync(t.Db)).OfficeKeyProtected);
            Assert.Null(await bound.Store.UsedAsync(OfficeService.Capability));
        }

        // switched off: not moved (bound would mean on), cleared
        using (var app = Platform.Create())
        {
            await OldSettingsAsync(t.Db, app.Protection, enabled: false);
            Assert.Equal(OldOfficeSettings.Dropped, await OfficeService.MoveOldSettingsAsync(t.Db, app.Protection, app.Bindings, app.Store));
            Assert.False((await app.Bindings.GetAsync(OfficeService.Capability)).IsBound);
            Assert.Null((await SettingsStore.LoadAsync(t.Db)).OfficeUrl);
        }

        // something was entered by hand already: that wins, the old values go
        using (var app = Platform.Create())
        {
            Assert.Empty(await app.Bindings.SaveByHandAsync(OfficeService.Capability, Values("http://office-new:4820", null, "mat_new"), "en"));
            await OldSettingsAsync(t.Db, app.Protection, enabled: true);
            Assert.Equal(OldOfficeSettings.Dropped, await OfficeService.MoveOldSettingsAsync(t.Db, app.Protection, app.Bindings, app.Store));
            Assert.Equal("mat_new", (await app.Bindings.GetAsync(OfficeService.Capability)).Key);
            Assert.Null((await SettingsStore.LoadAsync(t.Db)).OfficeKeyProtected);
        }
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------------------

    private static MatBinding Binding(string server, string key)
        => new(OfficeService.Capability, MatBindingSource.Variables, new Dictionary<string, string> { [MatCapabilities.Url] = server, [MatCapabilities.Key] = key });

    private static Dictionary<string, string?> Values(string url, string? internalUrl, string key)
        => new() { [MatCapabilities.Url] = url, [MatCapabilities.InternalUrl] = internalUrl, [MatCapabilities.Key] = key };

    private static async Task OldSettingsAsync(AppDbContext db, IDataProtectionProvider protection, bool enabled)
    {
        var s = await SettingsStore.LoadAsync(db);
        s.OfficeEnabled = enabled;
        s.OfficeUrl = "http://localhost:4820";
        s.OfficeInternalUrl = "http://host.docker.internal:4820";
        s.OfficeKeyProtected = protection.CreateProtector(OfficeService.OldKeyPurpose).Protect("mo_old-key");
        s.DriveUrlForOffice = "http://host.docker.internal:4810";
        await db.SaveChangesAsync();
    }

    /// <summary>A person with a file in their drive.</summary>
    private static async Task<(long User, long File)> FileAsync(TestDatabase t, string name)
    {
        var ada = await t.AddUserAsync("Ada" + Guid.NewGuid().ToString("N")[..6]);
        var drive = await DriveService.MyDriveAsync(t.Db, ada.Id);
        using var temp = new TempFolder();
        var store = new BlobStore(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DataDirectory"] = temp.Root }).Build());
        var made = await FileService.UploadAsync(t.Db, store, Actor.Of(ada.Id), drive.Id, null, name, new MemoryStream(Encoding.UTF8.GetBytes("office")));
        Assert.True(made.Ok, made.Error);
        return (ada.Id, made.Value!.Id);
    }

    /// <summary>MatPlatform as MatDrive registers it, with a data folder of its own and the given variables.</summary>
    private sealed class Platform : IDisposable
    {
        private readonly IHost _host;
        private readonly TempFolder _data = new();

        private Platform(IDictionary<string, string?>? variables)
        {
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
            builder.Configuration.AddInMemoryCollection(variables ?? new Dictionary<string, string?>());
            builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            builder.AddMatPlatform(MatDrivePlatform.Describe, o => o.DataDirectory = _data.Root);
            _host = builder.Build();
        }

        public MatBindings Bindings => _host.Services.GetRequiredService<MatBindings>();

        public MatBindingStore Store => _host.Services.GetRequiredService<MatBindingStore>();

        public MatPlatformSettings Settings => _host.Services.GetRequiredService<MatPlatformSettings>();

        public IDataProtectionProvider Protection => _host.Services.GetRequiredService<IDataProtectionProvider>();

        public static Platform Create(IDictionary<string, string?>? variables = null) => new(variables);

        public void Dispose()
        {
            _host.Dispose();
            _data.Dispose();
        }
    }

    /// <summary>An editor that answers like MatOffice at <c>/api/provides/office.edit</c> (or, <see cref="OldPaths"/>, only at <c>/api/v1/host</c>) to one key.</summary>
    private sealed class FakeOffice(string server) : HttpMessageHandler
    {
        public const string Formats = """
            {"app":"matoffice","version":"1","kinds":[
              {"kind":"docs","name":"Docs","colour":"#185abd","native":".matdoc","newFormat":".docx","formats":[{"ext":".docx","label":"Word","open":true,"write":true}]},
              {"kind":"sheets","name":"Sheets","colour":"#107c41","native":".matsheet","newFormat":".xlsx","formats":[{"ext":".xlsx","label":"Excel","open":true,"write":true}]}]}
            """;

        public List<(string Line, string? Key, string? Body)> Calls { get; } = [];

        public string Key { get; set; } = OfficeBindingTests.Key;

        public bool OldPaths { get; init; }

        public bool Down { get; init; }

        public HttpClient Client() => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add(($"{request.Method} {url}", request.Headers.Authorization?.Parameter, body));
            if (Down)
            {
                throw new HttpRequestException("Connection refused");
            }

            var root = server + (OldPaths ? "/api/v1/host" : "/api/provides/office.edit");
            if (!url.StartsWith(root + "/", StringComparison.Ordinal) || request.Headers.Authorization?.Parameter != Key)
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            var path = url[root.Length..];
            return path switch
            {
                "/formats" => Json(Formats),
                "/sessions" when request.Method == HttpMethod.Post => Json("""{"token":"s1","path":"/docs/edit/1?session=s1"}"""),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        }

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
