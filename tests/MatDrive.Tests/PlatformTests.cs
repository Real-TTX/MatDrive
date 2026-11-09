using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MatDrive.Data;
using MatDrive.Platform;
using MatDrive.Services.Office;
using MatPlatform;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MatDrive.Tests;

/// <summary>The description of MatDrive for the platform (contract 2): valid, what it consumes, its launcher, quick actions and widgets, the public address fixed by MAT_PUBLIC_URL.</summary>
public sealed class PlatformTests
{
    private static readonly string Web = Path.Combine(Root(), "src", "MatDrive", "wwwroot");

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void The_definition_is_valid_with_the_icons_of_the_MatUi_sprite(string language)
    {
        var sprite = MatPlatformValidator.ReadSprite(Path.Combine(Web, "matui", "matui-icons.svg"));
        Assert.Empty(MatPlatformValidator.Validate(MatPlatformBuilder.Build(MatDrivePlatform.Describe, language), sprite));
        Assert.Empty(MatPlatformValidator.Validate(MatPlatformBuilder.Build(o => MatDrivePlatform.Describe(o, "https://drive.example.com"), language), sprite));
    }

    [Fact]
    public void MatDrive_consumes_the_office_editor_and_contributes_a_launcher_and_widgets_for_administrators()
    {
        var definition = MatPlatformBuilder.Build(MatDrivePlatform.Describe, "en");
        Assert.Equal(MatApiDefinition.ContractVersion, definition.Contract);
        Assert.Empty(definition.Provides);
        var office = Assert.Single(definition.Consumes, c => c.Capability == MatCapabilities.OfficeEdit);
        Assert.True(office.Optional);
        Assert.Equal("Opens office files with", office.Label);
        Assert.Equal([MatCapabilities.OfficeEdit, MatCapabilities.IdentityOidc, MatCapabilities.MailSmtp], definition.Consumes.Select(c => c.Capability));
        Assert.All(definition.Consumes, c => Assert.True(c.Optional));

        var contributes = definition.Contributes!;
        var launcher = Assert.Single(contributes.Launchers);
        Assert.Equal(("drive", "/drive"), (launcher.Id, launcher.Url));
        Assert.True(File.Exists(Path.Combine(Web, launcher.Icon.TrimStart('/'))), launcher.Icon);

        // the quick actions are views of the explorer (Pages/Drive/Index: /drive/{view})
        Assert.Equal(["/drive/shared", "/drive/recent", "/drive/starred", "/drive/trash"], contributes.QuickActions.Select(q => q.Url));
        Assert.All(contributes.QuickActions, q => Assert.Equal("drive", q.Launcher));

        // widgets only from existing values, only for administrators
        Assert.Equal(["storage", "users"], contributes.Widgets.Select(w => w.Id));
        Assert.All(contributes.Widgets, w => Assert.Equal(MatAudience.Admin, w.Audience));
        Assert.Equal(["files", "used", "free"], contributes.Widgets[0].Items);

        Assert.Contains(definition.Actions, a => a.Id == "openConnections" && a.Url == "/Admin/Connections");
        Assert.DoesNotContain(definition.Actions, a => a.Id == "openIntegrations");
    }

    [Fact]
    public void MAT_PUBLIC_URL_fixes_the_public_address()
    {
        Assert.Null(MatPlatformBuilder.Build(MatDrivePlatform.Describe, "en").Configure.Single(f => f.Key == "publicUrl").Fixed);
        var field = MatPlatformBuilder.Build(o => MatDrivePlatform.Describe(o, "https://drive.example.com"), "en").Configure.Single(f => f.Key == "publicUrl");
        Assert.True(field.Fixed);
        Assert.Contains("MAT_PUBLIC_URL", field.Hint, StringComparison.Ordinal);
    }

    internal static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MatDrive.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("MatDrive.slnx not found.");
    }
}

/// <summary>MatDrive in memory with the variables of a broker: the platform endpoints, the fixed public address, the health of office.edit, the old address of Integrations.</summary>
public sealed class PlatformAppTests : IAsyncLifetime
{
    private const string ApiKey = "test-api-key-0123456789";
    private TestDatabase? _database;
    private WebApplicationFactory<Program>? _factory;
    private readonly string _data = Path.Combine(Path.GetTempPath(), "matdrive-tests", Guid.NewGuid().ToString("N")[..12]);

    public async Task InitializeAsync()
    {
        if (TestDatabase.Admin is null)
        {
            return;
        }

        _database = await TestDatabase.CreateAsync();
        Directory.CreateDirectory(_data);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
        {
            web.UseEnvironment("Development");
            web.UseSetting("ConnectionStrings:Default", _database.Db.Database.GetConnectionString());
            web.UseSetting("DataDirectory", _data);
            web.UseSetting("Logging:LogLevel:Default", "Warning");
            web.UseSetting("MAT_API_KEY", ApiKey);
            web.UseSetting("MAT_PUBLIC_URL", "https://drive.example.com/");
            web.UseSetting("MAT_INTERNAL_URL", "http://mattiverse-matdrive-web:4810");
            web.UseSetting("MAT_OFFICE_EDIT_URL", "https://office.example.com");
            web.UseSetting("MAT_OFFICE_EDIT_INTERNAL_URL", "http://127.0.0.1:9");
            web.UseSetting("MAT_OFFICE_EDIT_KEY", "mat_nobody-answers");
        });
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }

        if (Directory.Exists(_data))
        {
            Directory.Delete(_data, recursive: true);
        }
    }

    private HttpClient Api()
    {
        var client = _factory!.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");
        return client;
    }

    [DbFact]
    public async Task The_variables_of_a_broker_bind_office_edit_and_fix_the_public_address()
    {
        using var api = Api();
        var definition = await api.GetFromJsonAsync<JsonElement>("/api");
        Assert.Equal(2, definition.GetProperty("contract").GetInt32());
        Assert.Equal("drive", definition.GetProperty("contributes").GetProperty("launchers")[0].GetProperty("id").GetString());
        var publicUrl = definition.GetProperty("configure").EnumerateArray().Single(f => f.GetProperty("key").GetString() == "publicUrl");
        Assert.True(publicUrl.GetProperty("fixed").GetBoolean());

        var values = (await api.GetFromJsonAsync<JsonElement>("/api/configure")).GetProperty("values");
        Assert.Equal("https://drive.example.com", values.GetProperty("publicUrl").GetString());
        var refused = await api.PutAsync("/api/configure", new StringContent("""{"values":{"publicUrl":"https://other.example.com"}}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        // the Connections area shows the binding as set by the variables, never its key
        var connections = await api.GetStringAsync("/api/connections");
        Assert.DoesNotContain("mat_nobody-answers", connections, StringComparison.Ordinal);
        var use = JsonDocument.Parse(connections).RootElement.GetProperty("uses")[0];
        Assert.Equal(("office.edit", "variables", "Opens office files with"), (use.GetProperty("capability").GetString(), use.GetProperty("source").GetString(), use.GetProperty("label").GetString()));
    }

    [DbFact]
    public async Task An_editor_that_does_not_answer_makes_office_edit_degraded()
    {
        using (var scope = _factory!.Services.CreateScope())
        {
            var services = scope.ServiceProvider;
            var office = await services.GetRequiredService<MatBindings>().GetAsync(OfficeService.Capability);
            var result = await OfficeService.RefreshAsync(services.GetRequiredService<AppDbContext>(), services.GetRequiredService<IHttpClientFactory>().CreateClient("office"), office, force: true);
            Assert.False(result.Ok);
        }

        using var api = Api();
        var health = await api.GetFromJsonAsync<JsonElement>("/api/health");
        Assert.Equal("degraded", health.GetProperty("status").GetString());
        var check = health.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("name").GetString() == "office.edit");
        Assert.Equal("degraded", check.GetProperty("status").GetString());
        Assert.Equal("MatOffice cannot be reached right now.", check.GetProperty("message").GetString());
    }

    [DbFact]
    public async Task The_old_address_of_Integrations_leads_to_Connections()
    {
        using var api = Api();
        var answer = await api.GetAsync("/Admin/Integrations");
        Assert.Equal(HttpStatusCode.MovedPermanently, answer.StatusCode);
        Assert.Equal("/Admin/Connections", answer.Headers.Location!.OriginalString);
    }
}
