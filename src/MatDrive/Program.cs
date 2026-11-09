using System.Globalization;
using System.Reflection;
using System.Threading.RateLimiting;
using MatDrive;
using MatDrive.Data;
using MatDrive.Localization;
using MatDrive.Platform;
using MatDrive.Services;
using MatDrive.Services.Files;
using MatDrive.Services.Locations;
using MatDrive.Services.Office;
using MatDrive.Services.Mail;
using MatDrive.Services.WebDav;
using MatPlatform;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;

if (args.Contains("--healthcheck"))
{
    return await HealthCheck.RunAsync();
}

var builder = WebApplication.CreateBuilder(args);

// ---- the database (PostgreSQL): the connection string of appsettings.json fits the compose files, no variable is needed ----
var connection = builder.Configuration.GetConnectionString("Default") ?? "Host=db;Port=5432;Database=matdrive;Username=matdrive;Password=matdrive";
builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connection));

// The keys that protect the cookie are in the volume: a session survives a restart of the container.
var dataDir = builder.Configuration["DataDirectory"] ?? "/data";
var keys = Path.Combine(dataDir, "keys");
Directory.CreateDirectory(keys);
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keys)).SetApplicationName("MatDrive");

builder.Services.AddMemoryCache();
builder.Services.AddAntiforgery(o => o.HeaderName = "RequestVerificationToken");
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<Assets>();
builder.Services.AddSingleton<PasswordService>();
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<SessionService>();
builder.Services.AddScoped<AccountService>();
builder.Services.AddScoped<ExternalSignInService>();
builder.Services.AddScoped<MatDrive.Services.Auth.AccountDataService>();
builder.Services.AddScoped<MenuService>();
builder.Services.AddScoped<InstanceService>();
builder.Services.AddHostedService<MaintenanceService>();

// The mail through MatSMTP (only when the environment names it).
builder.Services.AddSingleton<MailService>();

// The bytes of the files (the data volume) and the calls to the office editor (the capability office.edit): no redirects, so a sign-in page of a provider
// that lacks an endpoint is an answer, not a document; the formats of the editor are kept fresh in the background.
builder.Services.AddSingleton<BlobStore>();
builder.Services.AddHttpClient("office", c => c.Timeout = TimeSpan.FromSeconds(60))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
builder.Services.AddHostedService<OfficeMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MailService>());

// The storage locations (local folders, SMB, SFTP, FTP, WebDAV): providers, pools of connections, the check of their reachability.
builder.Services.AddLocations();

// WebDAV at /dav/ (Basic sign-in with an app password, locks in memory).
builder.Services.AddWebDav();

// The Mat platform contract (MatDesign docs/PLATFORM.md, contract 2): /api, /api/health, /api/state, /api/configure, /api/actions with MAT_API_KEY or a token of
// MatIdentity, the bindings of what MatDrive consumes (office.edit: MAT_OFFICE_EDIT_*, else entered by hand in Administration → Connections, kept in
// /data/matplatform) and the contributions to a shell. The package MatPlatform (a copy in MatPlatform/) serves the endpoints; MatDrivePlatform describes MatDrive
// and supplies the values through the services. MAT_PUBLIC_URL fixes the public address.
var publicUrl = MatPlatformSettings.From(builder.Configuration, MatDrivePlatform.AppId).AppPublicUrl;
var platform = builder.AddMatPlatform<MatDrivePlatform>(o => MatDrivePlatform.Describe(o, publicUrl), o =>
{
    o.DefaultLanguage = Languages.Default;
    o.DataDirectory = dataDir;
    o.ConnectionsAccess = http => Task.FromResult(http.User.IsInRole(nameof(UserRole.Admin)));
});

// The interface speaks English and German (MatDrive.Localization); every localizer reads the one catalog of JSON texts.
builder.Services.AddSingleton<Microsoft.Extensions.Localization.IStringLocalizerFactory, CatalogLocalizerFactory>();
builder.Services.AddSingleton(typeof(Microsoft.Extensions.Localization.IStringLocalizer<>), typeof(CatalogLocalizer<>));
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    options.DefaultRequestCulture = new RequestCulture(CultureInfo.InvariantCulture, Languages.UiCulture(Languages.Default));
    options.SupportedCultures = [CultureInfo.InvariantCulture];
    options.SupportedUICultures = [.. Languages.Codes.Select(Languages.UiCulture)];
    options.RequestCultureProviders = [new AccountLanguageProvider(), new AcceptLanguageHeaderRequestCultureProvider()];
});

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "matdrive.session";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.Cookie.IsEssential = true;
        o.LoginPath = "/Account/Login";
        o.AccessDeniedPath = "/Account/Login";
        o.SlidingExpiration = true;
        o.Events.OnValidatePrincipal = SessionService.ValidateAsync;
        o.Events.OnRedirectToLogin = context =>
        {
            // a script or an API call gets a status, not the sign-in page
            if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/live")
                || context.Request.Headers.Accept.ToString().Contains("application/json", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            }
            else
            {
                context.Response.Redirect(context.RedirectUri);
            }

            return Task.CompletedTask;
        };
    })

    // Sign-in through MatIdentity, only when MATIDENTITY_AUTHORITY is set (nothing changes without it); with it also the back-channel logout.
    .AddMatIdentitySignIn<MatDriveIdentityUsers>(platform, o =>
    {
        o.CookieScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        o.LoginPath = "/Account/Login";
        o.AccessDeniedPath = "/Account/Login?local=1&denied=1";
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy => policy.RequireRole(nameof(UserRole.Admin)));
    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
});

builder.Services.AddRazorPages(options =>
{
    options.Conventions.AllowAnonymousToPage("/Index");
    options.Conventions.AllowAnonymousToPage("/Error");
    options.Conventions.AllowAnonymousToPage("/Account/Login");
    options.Conventions.AllowAnonymousToPage("/Account/Register");
    options.Conventions.AllowAnonymousToPage("/Public/Link");
    options.Conventions.AllowAnonymousToPage("/Public/Folder");
    options.Conventions.AuthorizeFolder("/Admin", "AdminOnly");
}).AddMvcOptions(o =>
{
    o.ModelBinderProviders.Insert(0, new EmptyStringBinderProvider());
    o.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true;
});

// The sign-in form: 10 attempts a minute per address.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("signin", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    // behind a reverse proxy (Caddy, nginx, Traefik) the address and the scheme of the visitor are in the headers
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

var app = builder.Build();

await Database.MigrateWithRetryAsync(app);
await Database.MoveOldOfficeSettingsAsync(app);

app.UseForwardedHeaders();
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler("/Error");
}

app.UseStatusCodePagesWithReExecute("/Error", "?code={0}");
app.UseMiddleware<SecurityHeaders>();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        // files are versioned with ?v= (asp-append-version): they can be kept for a long time
        if (ctx.Context.Request.Query.ContainsKey("v"))
        {
            ctx.Context.Response.Headers.CacheControl = "public,max-age=31536000,immutable";
        }
        else
        {
            // the modules of the editor import each other without a version: the browser asks again (an unchanged file answers 304)
            ctx.Context.Response.Headers.CacheControl = "no-cache";
        }
    },
});
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseRequestLocalization();
app.UseAuthorization();

// The language switch (EN / DE): the cookie for the pages without an account, and the account of a signed-in person.
app.MapPost("/language", async (HttpContext http, CurrentUser me, AppDbContext db, Microsoft.AspNetCore.Antiforgery.IAntiforgery anti) =>
{
    await anti.ValidateRequestAsync(http);
    var form = await http.Request.ReadFormAsync();
    var code = Languages.Normalize(form["language"].ToString());
    Languages.SetCookie(http.Response, code, http.Request.IsHttps);
    if (me.Id is { } id)
    {
        await db.Users.Where(u => u.Id == id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Language, code));
        SessionService.Invalidate();
    }

    var back = form["returnUrl"].ToString();
    return Results.LocalRedirect(!string.IsNullOrEmpty(back) && back.StartsWith('/') && !back.StartsWith("//", StringComparison.Ordinal) ? back : "/");
}).AllowAnonymous();

app.MapGet("/health", async (AppDbContext db) => await db.Database.CanConnectAsync() ? Results.Ok("ok") : Results.StatusCode(503)).AllowAnonymous();

// The page Integrations became Connections (MatPlatform 2.2).
app.MapGet("/Admin/Integrations", () => Results.LocalRedirect("/Admin/Connections", permanent: true)).AllowAnonymous();
app.MapFileEndpoints();
app.MapLinkEndpoints();
app.MapLocationEndpoints();
app.MapWebDav();
app.MapMatPlatform();
app.MapRazorPages();

app.Run();
return 0;

/// <summary>The build: <c>0.1.42-20261031</c> (release), <c>nightly-42-20261031</c> (development) or <c>local-20261031</c>.</summary>
public static class AppInfo
{
    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "local";
}

public partial class Program;
