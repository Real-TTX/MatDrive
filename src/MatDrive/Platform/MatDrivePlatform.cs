using System.Text.Json;
using MatDrive.Services;
using MatPlatform;

namespace MatDrive.Platform;

/// <summary>
/// MatDrive as an app of the platform contract (MatDesign docs/PLATFORM.md) on the shared package MatPlatform (the folder <c>MatPlatform/</c>, a copy):
/// how a consumer such as Mattiverse shows MatDrive (<see cref="Describe"/>: tiles, the few settings, buttons), its values and what its buttons do.
/// The settings are those of the admin page System (registration, public address, space, trash, versions), kept in the same place (<see cref="InstanceService"/>).
/// The values come from the services (no DbContext here: this is the web layer).
/// </summary>
public sealed class MatDrivePlatform(InstanceService instance, ILogger<MatDrivePlatform> log)
    : IMatPlatformState, IMatPlatformConfiguration, IMatPlatformActions, IMatPlatformHealth
{
    /// <summary>The id of MatDrive in the platform (and the default client id at MatIdentity), as in MatDesign <c>AppRegistry.cs</c>.</summary>
    public const string AppId = "matdrive";

    /// <summary>The accent of MatDrive (a deep violet, white text 7:1; see <c>wwwroot/css/app.css</c>).</summary>
    public const string Accent = "#8e24aa";

    /// <summary>The MatApiDefinition of MatDrive (<c>GET /api</c>); <c>auth</c> and <c>endpoints</c> are set by MatPlatform.</summary>
    public static void Describe(MatPlatformBuilder o)
    {
        var T = o.T;

        o.App(AppId, "MatDrive", T("Dateien im Browser: Ablage, Teilen, Versionen, WebDAV und Speicherorte", "Files in the browser: storage, sharing, versions, WebDAV and storage locations"), AppInfo.Version, "/img/logo/matdrive-mark.svg", Accent, "/drive");

        o.Page("overview", T("Übersicht", "Overview"), "dashboard",
            MatSection.Tiles("users", "files", "folders", "used", "free", "sharedDrives", "sharedItems", "links", "locations", "uploads7", "trash", "registration", "lastActivity"),
            MatSection.Actions("openApp", "openUsers", "openLocations", "openIntegrations", "openActivity", "openSystem", "cleanUp"));
        o.Page("settings", T("Einstellungen", "Settings"), "settings",
            MatSection.Form(T("Allgemein", "General"), "registrationOpen", "publicUrl"),
            MatSection.Form(T("Speicher", "Storage"), "defaultQuotaGb", "maxUploadMb", "trashDays", "versionDays"));

        o.Tile("users", T("Aktive Benutzer", "Active users"), icon: "users");
        o.Tile("files", T("Dateien", "Files"), icon: "file");
        o.Tile("folders", T("Ordner", "Folders"), icon: "folder");
        o.Tile("used", T("Belegt", "Used"), "text", icon: "hard-drive");
        o.Tile("free", T("Frei auf dem Datenträger", "Free on the volume"), "text", icon: "hard-drive");
        o.Tile("sharedDrives", T("Geteilte Ablagen", "Shared drives"), icon: "users");
        o.Tile("sharedItems", T("Geteilte Elemente", "Shared items"), icon: "share");
        o.Tile("links", T("Aktive Links", "Active links"), icon: "link");
        o.Tile("locations", T("Speicherorte", "Storage locations"), icon: "server");
        o.Tile("uploads7", T("Hochgeladen in 7 Tagen", "Uploaded in 7 days"), icon: "upload");
        o.Tile("trash", T("Im Papierkorb", "In the trash"), icon: "trash");
        o.Tile("registration", T("Registrierung", "Sign-up"), "status", icon: "user");
        o.Tile("lastActivity", T("Letzte Aktivität", "Last activity"), "time", icon: "activity");

        o.Field(new MatField("registrationOpen", T("Registrierung offen", "Open sign-up"), "bool")
        {
            Hint = T("Aus: nur Administratoren legen Konten an.", "Off: only administrators create accounts."),
        });
        o.Field(new MatField("publicUrl", T("Öffentliche Adresse", "Public address"), "url")
        {
            Max = 300,
            Placeholder = "https://drive.example.com",
            Hint = T("Für Links, E-Mails und MatOffice; leer: die Adresse des Aufrufs.", "For links, mails and MatOffice; empty: the address of the request."),
        });
        o.Field(new MatField("defaultQuotaGb", T("Speicher pro Person (GB)", "Space per person (GB)"), "number") { Min = 0, Max = 1000000, Hint = T("0 = unbegrenzt.", "0 = no limit.") });
        o.Field(new MatField("maxUploadMb", T("Größte Datei (MB)", "Largest file (MB)"), "number") { Min = 0, Max = 10000000, Hint = T("0 = unbegrenzt.", "0 = no limit.") });
        o.Field(new MatField("trashDays", T("Tage im Papierkorb", "Days in the trash"), "number") { Min = 1, Max = 3650 });
        o.Field(new MatField("versionDays", T("Alte Versionen behalten (Tage)", "Keep old versions (days)"), "number") { Min = 0, Max = 36500, Hint = T("0 = alle behalten.", "0 = keep all.") });

        o.LinkAction("openApp", T("Öffnen", "Open"), "external", "/drive");
        o.LinkAction("openUsers", T("Benutzer", "Users"), "users", "/Admin/Users");
        o.LinkAction("openLocations", T("Speicherorte", "Storage locations"), "server", "/Admin/Locations");
        o.LinkAction("openIntegrations", T("Integrationen", "Integrations"), "plug", "/Admin/Integrations");
        o.LinkAction("openActivity", T("Aktivität", "Activity"), "activity", "/Admin/Activity");
        o.LinkAction("openSystem", T("System", "System"), "settings", "/Admin/System");
        o.PostAction(
            "cleanUp",
            T("Aufräumen", "Clean up"),
            "broom",
            T("Beendete Sitzungen, abgelaufene Elemente des Papierkorbs, alte Versionen und abgebrochene Uploads jetzt entfernen?",
              "Remove ended sessions, expired items of the trash, old versions and broken uploads now?"));

        o.Role("admin", T("Administrator", "Administrator"));
        o.Role("user", T("Benutzer", "User"));
    }

    public async Task<IReadOnlyList<MatHealthCheck>> CheckHealthAsync(MatPlatformCall call, CancellationToken ct)
    {
        if (!await instance.CanConnectAsync(ct))
        {
            return [new MatHealthCheck("database", "down", call.T("Die Datenbank ist nicht erreichbar.", "The database cannot be reached."))];
        }

        var checks = new List<MatHealthCheck> { new("database", "ok") };
        checks.Add(instance.CanWriteFiles() ? new MatHealthCheck("files", "ok") : new MatHealthCheck("files", "down", call.T("Der Datenordner ist nicht beschreibbar.", "The data folder cannot be written.")));
        if (await instance.AnyUserAsync() && (await instance.SnapshotAsync(ct)).Admins == 0)
        {
            checks.Add(new MatHealthCheck("administrators", "degraded", call.T("Kein aktiver Administrator.", "No active administrator.")));
        }

        return checks;
    }

    public async Task<IReadOnlyDictionary<string, MatStateValue>> GetStateAsync(MatPlatformCall call, CancellationToken ct)
    {
        var s = await instance.SnapshotAsync(ct);
        var (open, _) = await instance.SettingsAsync();
        return new Dictionary<string, MatStateValue>
        {
            ["users"] = new(s.Users),
            ["files"] = new(s.Files),
            ["folders"] = new(s.Folders),
            ["used"] = new(Ui.Bytes(s.UsedBytes)),
            ["free"] = new(s.FreeBytes < 0 ? null : Ui.Bytes(s.FreeBytes)),
            ["sharedDrives"] = new(s.SharedDrives),
            ["sharedItems"] = new(s.SharedItems),
            ["links"] = new(s.Links),
            ["locations"] = new(s.Locations),
            ["uploads7"] = new(s.UploadsLast7Days),
            ["trash"] = new(s.InTrash),
            ["registration"] = open
                ? new MatStateValue("info", Text: call.T("Offen", "Open"))
                : new MatStateValue("ok", Text: call.T("Geschlossen", "Closed")),
            ["lastActivity"] = new(s.LastActivityUtc is { } last ? new DateTimeOffset(DateTime.SpecifyKind(last, DateTimeKind.Utc)) : null),
        };
    }

    public async Task<IReadOnlyDictionary<string, object?>> GetConfigurationAsync(MatPlatformCall call, CancellationToken ct)
    {
        var st = await instance.StorageSettingsAsync();
        return new Dictionary<string, object?>
        {
            ["registrationOpen"] = st.RegistrationOpen,
            ["publicUrl"] = st.PublicUrl ?? string.Empty,
            ["defaultQuotaGb"] = st.DefaultQuotaBytes / (1024L * 1024 * 1024),
            ["maxUploadMb"] = st.MaxUploadBytes / (1024L * 1024),
            ["trashDays"] = st.TrashDays,
            ["versionDays"] = st.VersionDays,
        };
    }

    /// <summary>The values are checked against the definition already (MatPlatform, <c>CheckValues</c>); the address is checked once more like on the admin page.</summary>
    public async Task<IReadOnlyDictionary<string, string>> ConfigureAsync(IReadOnlyDictionary<string, JsonElement> values, MatPlatformCall call, CancellationToken ct)
    {
        var errors = new Dictionary<string, string>();
        bool? open = null;
        string? url = null;
        long? quotaGb = null, uploadMb = null;
        int? trashDays = null, versionDays = null;
        foreach (var (key, value) in values)
        {
            switch (key)
            {
                case "registrationOpen" when value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                    open = value.GetBoolean();
                    break;
                case "publicUrl" when value.ValueKind is JsonValueKind.String:
                    url = value.GetString()!;
                    break;
                case "publicUrl" when value.ValueKind is JsonValueKind.Null:
                    url = string.Empty;
                    break;
                case "defaultQuotaGb" when value.TryGetInt64(out var q) && q >= 0:
                    quotaGb = q;
                    break;
                case "maxUploadMb" when value.TryGetInt64(out var m) && m >= 0:
                    uploadMb = m;
                    break;
                case "trashDays" when value.TryGetInt32(out var td) && td is >= 1 and <= 3650:
                    trashDays = td;
                    break;
                case "versionDays" when value.TryGetInt32(out var vd) && vd is >= 0 and <= 36500:
                    versionDays = vd;
                    break;
                default:
                    errors[key] = call.T("Dieser Wert passt nicht.", "This value does not fit.");
                    break;
            }
        }

        if (url is not null && InstanceService.NormalizePublicUrl(url) is { } normalized && !InstanceService.IsPublicUrl(normalized))
        {
            errors["publicUrl"] = call.T("Eine vollständige Adresse mit http:// oder https://.", "A complete address with http:// or https://.");
        }

        if (errors.Count > 0)
        {
            return errors;
        }

        await instance.SaveSettingsAsync(open, url, quotaGb * 1024L * 1024 * 1024, uploadMb * 1024L * 1024, trashDays, versionDays);

        log.LogInformation("Settings changed through the platform API by {Caller}: {Keys}", call.Caller, string.Join(", ", values.Keys));
        return errors;
    }

    public async Task<MatActionResult?> RunActionAsync(string id, MatPlatformCall call, CancellationToken ct)
    {
        if (id != "cleanUp")
        {
            return null;
        }

        var result = await instance.CleanUpAsync(ct);
        log.LogInformation("Clean-up through the platform API by {Caller}: {Sessions} sessions, {Items} items", call.Caller, result.Sessions, result.Items);
        return new MatActionResult(true, call.T(
            $"Aufgeräumt: {result.Sessions} Sitzungen, {result.Items} Elemente des Papierkorbs, {result.Versions} alte Versionen, {result.Uploads} Uploads.",
            $"Cleaned up: {result.Sessions} sessions, {result.Items} items of the trash, {result.Versions} old versions, {result.Uploads} uploads."));
    }
}
