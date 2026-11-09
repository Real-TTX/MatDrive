using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MatDrive.Data;
using MatDrive.Localization;
using MatDrive.Services.Files;
using MatPlatform;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace MatDrive.Services.Office;

/// <summary>A kind of document the office editor edits (Docs, Sheets, Slides, Base) with the file types it opens and writes.</summary>
public sealed record OfficeKind(string Kind, string Name, string Colour, string Native, string NewFormat, IReadOnlyList<OfficeFormat> Formats);

public sealed record OfficeFormat(string Ext, string Label, bool Open, bool Write);

/// <summary>
/// The office editor as MatDrive knows it: whether the capability <c>office.edit</c> is bound, the kinds it opens (fetched for this binding), when they were fetched
/// and why the last fetch failed (a code for <see cref="OfficeService.ProblemText"/>).
/// </summary>
public sealed record OfficeStatus(bool Bound, IReadOnlyList<OfficeKind> Kinds, DateTime? CheckedDate, string? Problem)
{
    /// <summary>Bound, but the kinds of this binding were not fetched yet.</summary>
    public bool Pending => Bound && Kinds.Count == 0 && Problem is null;
}

/// <summary>What happened at the start to the settings of the old page Integrations (<see cref="OfficeService.MoveOldSettingsAsync"/>).</summary>
public enum OldOfficeSettings
{
    /// <summary>There were none.</summary>
    None,

    /// <summary>Moved into the binding entered by hand, then cleared.</summary>
    Moved,

    /// <summary>The variables bind the capability: the old values stay, they move once the variables are gone.</summary>
    Kept,

    /// <summary>Switched off, incomplete, unreadable, or a binding was entered by hand already: cleared without moving.</summary>
    Dropped,
}

/// <summary>
/// The office editor of the office files of MatDrive (MatOffice, its own container), reached through the capability <c>office.edit</c> of the Mat app model: the
/// binding (<see cref="MatBindings"/>) gives the address for the browser (<see cref="MatBinding.Url"/>), the address for the calls of this server
/// (<see cref="MatBinding.ServerUrl"/>) and the key. Mattiverse makes the binding by itself; without it an administrator enters the values in Administration →
/// Connections. Bound = on: nothing else switches the integration.
/// To open a file, MatDrive starts a session at the editor (<c>POST …/sessions</c>) and hands over where it reads and writes the file
/// (<c>/api/v1/office/files/{id}/content</c> here, at the address of MatDrive for other apps) together with a token that names the file, the person and the
/// right and expires after 12 hours. The editor writes back with <c>If-Match</c>, so a file that changed meanwhile is not overwritten. The right of the person is
/// checked again on every read and write. The kinds the editor opens are fetched once per binding and every six hours (<see cref="RefreshAsync"/>, run by
/// <see cref="OfficeMonitor"/> and the page Connections); until then, or when the fetch failed, office files open in the preview.
/// </summary>
public static class OfficeService
{
    public const string Capability = MatCapabilities.OfficeEdit;

    public static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(12);

    /// <summary>A save of the same person from the editor within this time replaces their last version (the editor saves every few seconds).</summary>
    public static readonly TimeSpan Coalesce = TimeSpan.FromMinutes(10);

    /// <summary>The formats are fetched again after this time.</summary>
    public static readonly TimeSpan FormatsMaxAge = TimeSpan.FromHours(6);

    /// <summary>A failed fetch of the formats is tried again after this time.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(5);

    /// <summary>How long a fetch of the formats may take.</summary>
    public static readonly TimeSpan FormatsTimeout = TimeSpan.FromSeconds(15);

    public const string ProtectorPurpose = "MatDrive.Office.FileToken";

    /// <summary>The purpose of the key of the old settings (only read to move them into the binding).</summary>
    public const string OldKeyPurpose = "MatDrive.Office.ApiKey";

    /// <summary>
    /// Where the endpoints of <c>office.edit</c> are at the provider: under <c>/api/provides/office.edit</c> (contract 2), else under <c>/api/v1/host</c> (a MatOffice
    /// from before the bindings). The one that answered is remembered per address.
    /// </summary>
    private static readonly string[] ApiBases = ["/api/provides/" + MatCapabilities.OfficeEdit, "/api/v1/host"];

    private static readonly ConcurrentDictionary<string, string> KnownBase = new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ---- the binding and the formats -------------------------------------------------------------------------------------------

    /// <summary>A mark of a binding (the address for the server and the key): the formats kept belong to the binding with this mark. Empty when nothing is bound.</summary>
    public static string Fingerprint(MatBinding office)
    {
        ArgumentNullException.ThrowIfNull(office);
        return office.IsBound
            ? Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(office.ServerUrl + "\n" + office.Key)))[..32]
            : string.Empty;
    }

    /// <summary>The kinds the bound editor opens (for the menus of everybody); empty while nothing is bound or the formats of this binding are not there.</summary>
    public static async Task<IReadOnlyList<OfficeKind>> ActiveKindsAsync(AppDbContext db, MatBindings bindings, CancellationToken ct = default)
        => (await StatusAsync(db, await bindings.GetAsync(Capability, ct), ct)).Kinds;

    /// <summary>What MatDrive knows of the bound editor: the kinds fetched for this binding, when, and the problem of the last fetch.</summary>
    public static async Task<OfficeStatus> StatusAsync(AppDbContext db, MatBinding office, CancellationToken ct = default)
    {
        if (!office.IsBound)
        {
            return new OfficeStatus(false, [], null, null);
        }

        var s = await db.SystemSettings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == 1, ct);
        return s is null || s.OfficeFormatsBinding != Fingerprint(office)
            ? new OfficeStatus(true, [], null, null)
            : new OfficeStatus(true, KindsOf(s, office), s.OfficeCheckedDate, s.OfficeProblem);
    }

    /// <summary>The kinds kept for this binding (none when they belong to another one or the last fetch failed).</summary>
    private static IReadOnlyList<OfficeKind> KindsOf(SystemSetting s, MatBinding office)
        => office.IsBound && s.OfficeFormatsBinding == Fingerprint(office) && s.OfficeProblem is null ? ParseKinds(s.OfficeFormats) : [];

    /// <summary>
    /// Whether the formats have to be fetched: bound, and they belong to another binding, were never fetched, are older than <see cref="FormatsMaxAge"/>, or the
    /// last fetch failed more than <see cref="RetryAfter"/> ago.
    /// </summary>
    public static bool NeedsRefresh(SystemSetting s, MatBinding office, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(s);
        return office.IsBound
            && (s.OfficeFormatsBinding != Fingerprint(office) || s.OfficeCheckedDate is not { } at || nowUtc - at >= (s.OfficeProblem is null ? FormatsMaxAge : RetryAfter));
    }

    /// <summary>
    /// Asks the bound editor which files it opens (and so checks the address and the key) when that is due (<see cref="NeedsRefresh"/>) or <paramref name="force"/>,
    /// and keeps the answer with the mark of the binding. A failure keeps no kinds (office files open in the preview) and the problem, for the health check.
    /// </summary>
    public static async Task<Outcome<IReadOnlyList<OfficeKind>>> RefreshAsync(AppDbContext db, HttpClient http, MatBinding office, bool force = false, CancellationToken ct = default)
    {
        if (!office.IsBound)
        {
            return Outcome<IReadOnlyList<OfficeKind>>.Fail(Text.T("Office.NotSetUp"));
        }

        var s = await LoadAsync(db, ct);
        if (!force && !NeedsRefresh(s, office, DateTime.UtcNow))
        {
            return s.OfficeProblem is { } known
                ? Outcome<IReadOnlyList<OfficeKind>>.Fail(ProblemText(known))
                : Outcome<IReadOnlyList<OfficeKind>>.Success(ParseKinds(s.OfficeFormats));
        }

        var (body, problem) = await FetchFormatsAsync(http, office, ct);
        s.OfficeFormatsBinding = Fingerprint(office);
        s.OfficeCheckedDate = DateTime.UtcNow;
        s.OfficeFormats = problem is null ? body : null;
        s.OfficeProblem = problem;
        await db.SaveChangesAsync(ct);
        return problem is null
            ? Outcome<IReadOnlyList<OfficeKind>>.Success(ParseKinds(body))
            : Outcome<IReadOnlyList<OfficeKind>>.Fail(ProblemText(problem));
    }

    /// <summary>"Check now" of an administrator: fetches the formats of the bound editor now.</summary>
    public static async Task<Outcome<IReadOnlyList<OfficeKind>>> CheckAsync(AppDbContext db, HttpClient http, MatBinding office, long adminId, CancellationToken ct = default)
        => await IsAdminAsync(db, adminId, ct)
            ? await RefreshAsync(db, http, office, force: true, ct)
            : Outcome<IReadOnlyList<OfficeKind>>.Denied();

    /// <summary>The words of a problem kept by <see cref="RefreshAsync"/> (<c>KeyWrong</c>, <c>Unreachable</c>, <c>NotMatOffice</c>, <c>Answer:503</c>), in the language of the request.</summary>
    public static string ProblemText(string problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        var (code, status) = problem.Split(':', 2) is [var c, var n] ? (c, n) : (problem, string.Empty);
        return code switch
        {
            "KeyWrong" => Text.T("Office.KeyWrong"),
            "NotMatOffice" => Text.T("Office.NotMatOffice"),
            "Answer" => Text.T("Office.Answer", status),
            _ => Text.T("Office.Unreachable"),
        };
    }

    /// <summary>The answer of <c>…/formats</c> (as it came) or the code of the problem; at most <see cref="FormatsTimeout"/>, so a page that asks does not hang.</summary>
    private static async Task<(string? Body, string? Problem)> FetchFormatsAsync(HttpClient http, MatBinding office, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(FormatsTimeout);
        try
        {
            using var response = await SendAsync(http, office, HttpMethod.Get, "/formats", null, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return (null, response.StatusCode == HttpStatusCode.Unauthorized ? "KeyWrong" : "Answer:" + (int)response.StatusCode);
            }

            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            return ParseKinds(body).Count == 0 ? (null, "NotMatOffice") : (body, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException && !ct.IsCancellationRequested)
        {
            return (null, "Unreachable");
        }
    }

    /// <summary>Whether a click opens a file in the editor: its own formats and the ones it makes for new files (Word, Excel, PowerPoint, a database).</summary>
    public static bool OpensByDefault(IReadOnlyList<OfficeKind> kinds, string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return FormatOf(kinds, fileName) is not null && kinds.Any(k => ext == k.Native || ext == k.NewFormat);
    }

    /// <summary>The format of a file name the editor can open (and whether it can write it), or null.</summary>
    public static (OfficeKind Kind, OfficeFormat Format)? FormatOf(IReadOnlyList<OfficeKind> kinds, string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        foreach (var k in kinds)
        {
            var f = k.Formats.FirstOrDefault(f => f.Ext == ext && f.Open);
            if (f is not null)
            {
                return (k, f);
            }
        }

        return null;
    }

    // ---- the settings of the old page Integrations ---------------------------------------------------------------------------

    /// <summary>
    /// Once at the start: the settings of the old page Integrations become the binding entered by hand, when they say "on" with an address and a key, nothing was entered
    /// by hand yet and no variable binds <c>office.edit</c>; then they are cleared. While the variables bind it, they stay (and move once the variables are gone).
    /// Settings that were switched off, incomplete or unreadable are cleared. The address of MatDrive for other apps stays: it is MatDrive's own setting.
    /// </summary>
    public static async Task<OldOfficeSettings> MoveOldSettingsAsync(AppDbContext db, IDataProtectionProvider protection, MatBindings bindings, MatBindingStore store, CancellationToken ct = default)
    {
        var s = await db.SystemSettings.FirstOrDefaultAsync(x => x.Id == 1, ct);
        if (s is null || (!s.OfficeEnabled && s.OfficeUrl is null && s.OfficeInternalUrl is null && s.OfficeKeyProtected is null))
        {
            return OldOfficeSettings.None;
        }

        if (bindings.IsFixed(Capability) || !bindings.CanBindByHand)
        {
            return OldOfficeSettings.Kept;
        }

        var result = OldOfficeSettings.Dropped;
        var key = OldKey(protection, s.OfficeKeyProtected);
        if (s.OfficeEnabled && s.OfficeUrl is not null && key is not null && await store.UsedAsync(Capability, ct) is null)
        {
            var problems = await bindings.SaveByHandAsync(Capability, new Dictionary<string, string?>
            {
                [MatCapabilities.Url] = s.OfficeUrl,
                [MatCapabilities.InternalUrl] = s.OfficeInternalUrl,
                [MatCapabilities.Key] = key,
            }, Languages.Default, ct);
            result = problems.Count == 0 ? OldOfficeSettings.Moved : OldOfficeSettings.Dropped;
        }

        s.OfficeEnabled = false;
        s.OfficeUrl = null;
        s.OfficeInternalUrl = null;
        s.OfficeKeyProtected = null;
        await db.SaveChangesAsync(ct);
        return result;
    }

    private static string? OldKey(IDataProtectionProvider protection, string? protectedKey)
    {
        if (string.IsNullOrEmpty(protectedKey))
        {
            return null;
        }

        try
        {
            return protection.CreateProtector(OldKeyPurpose).Unprotect(protectedKey);
        }
        catch (CryptographicException)
        {
            return null; // the keys of the data volume changed: it cannot be read any more
        }
    }

    // ---- open and create --------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Opens a file in the editor: starts a session there and answers the address the browser goes to. Editors edit, everybody else reads; a format the editor
    /// cannot write opens read-only. The editor reads and writes the file at the address of MatDrive for other apps (<c>MAT_INTERNAL_URL</c>, else the stored
    /// address for other apps, else the public address); the browser comes back to the public address (<c>MAT_PUBLIC_URL</c>, else the stored one, else
    /// <paramref name="requestOrigin"/>, the address of this request).
    /// </summary>
    public static async Task<Outcome<string>> OpenAsync(AppDbContext db, IDataProtectionProvider protection, HttpClient http, MatBinding office, MatPlatformSettings platform,
        Actor actor, long nodeId, string requestOrigin, string language, CancellationToken ct = default)
    {
        var access = await DriveAccess.FindAsync(db, actor, nodeId, ct: ct);
        if (access is null || access.Node.Kind != NodeKind.File || actor.UserId is not { } userId)
        {
            return Outcome<string>.Denied();
        }

        if (!office.IsBound)
        {
            return Outcome<string>.Fail(Text.T("Office.NotSetUp"));
        }

        var s = await LoadAsync(db, ct);
        if (FormatOf(KindsOf(s, office), access.Node.Name) is not { } format)
        {
            return Outcome<string>.Fail(Text.T("Office.CannotOpen"));
        }

        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, ct);
        var canWrite = access.CanEdit && format.Format.Write;
        var token = Protector(protection).Protect($"{nodeId}|{userId}|{(canWrite ? 1 : 0)}", TokenLifetime);
        var origin = InstanceService.PublicUrlOf(s, platform) ?? requestOrigin.TrimEnd('/');
        var forApps = InstanceService.AppsUrlOf(s, platform) ?? origin;
        var back = origin + (access.Node.ParentId is { } p ? $"/drive/folder/{p}" : access.Drive.Kind == DriveKind.Shared ? $"/drive/shared-drive/{access.Drive.Id}" : "/drive");
        var body = new
        {
            fileId = nodeId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            fileName = access.Node.Name,
            contentUrl = $"{forApps}/api/v1/office/files/{nodeId}/content",
            accessToken = token,
            canWrite,
            user = new { id = userId.ToString(System.Globalization.CultureInfo.InvariantCulture), name = user.DisplayName, email = user.Email },
            returnUrl = back,
            language,
        };

        try
        {
            using var response = await SendAsync(http, office, HttpMethod.Post, "/sessions", () => JsonContent.Create(body, options: Json), ct);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    return Outcome<string>.Fail(Text.T("Office.KeyWrong"));
                }

                var problem = await ReadErrorAsync(response, ct);
                return Outcome<string>.Fail(problem ?? Text.T("Office.Answer", (int)response.StatusCode));
            }

            var started = await response.Content.ReadFromJsonAsync<SessionAnswer>(Json, ct);
            if (started?.Path is null)
            {
                return Outcome<string>.Fail(Text.T("Office.NotMatOffice"));
            }

            await NodeService.VisitAsync(db, userId, nodeId, ct);
            await ActivityLog.WriteAsync(db, userId, "office", access.Node, canWrite ? "edit" : "view", ct: ct);
            return Outcome<string>.Success(office.Url + started.Path);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException or InvalidOperationException or UriFormatException && !ct.IsCancellationRequested)
        {
            return Outcome<string>.Fail(Text.T("Office.Unreachable"));
        }
    }

    /// <summary>A new document of the editor (an empty file of the kind, in the format the editor proposes) in a folder; answers the new file.</summary>
    public static async Task<Outcome<Node>> CreateAsync(AppDbContext db, BlobStore store, HttpClient http, MatBinding office, Actor actor, long driveId, long? parentId,
        string kind, string language, CancellationToken ct = default)
    {
        var k = (await StatusAsync(db, office, ct)).Kinds.FirstOrDefault(x => x.Kind == kind);
        if (k is null)
        {
            return Outcome<Node>.Fail(Text.T("Office.NotSetUp"));
        }

        var name = Languages.In(language, () => Text.T("Office.Untitled." + kind)) + k.NewFormat;
        try
        {
            var query = $"/new?kind={Uri.EscapeDataString(kind)}&format={Uri.EscapeDataString(k.NewFormat)}&language={language}&name={Uri.EscapeDataString(Path.GetFileNameWithoutExtension(name))}";
            using var response = await SendAsync(http, office, HttpMethod.Get, query, null, ct);
            if (!response.IsSuccessStatusCode)
            {
                return Outcome<Node>.Fail(response.StatusCode == HttpStatusCode.Unauthorized ? Text.T("Office.KeyWrong") : Text.T("Office.Answer", (int)response.StatusCode));
            }

            await using var content = await response.Content.ReadAsStreamAsync(ct);
            return await FileService.UploadAsync(db, store, actor, driveId, parentId, name, content, UploadMode.KeepBoth, "office", ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException && !ct.IsCancellationRequested)
        {
            return Outcome<Node>.Fail(Text.T("Office.Unreachable"));
        }
    }

    // ---- the file API for the editor --------------------------------------------------------------------------------------------

    /// <summary>The file, the person and the right a token of the editor stands for (null: wrong, expired, or not for this file).</summary>
    public static (long NodeId, long UserId, bool CanWrite)? ReadToken(IDataProtectionProvider protection, string? token, long nodeId)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        try
        {
            var parts = Protector(protection).Unprotect(token).Split('|');
            if (parts.Length != 3 || !long.TryParse(parts[0], out var node) || !long.TryParse(parts[1], out var user) || node != nodeId)
            {
                return null;
            }

            return (node, user, parts[2] == "1");
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static ITimeLimitedDataProtector Protector(IDataProtectionProvider protection) => protection.CreateProtector(ProtectorPurpose).ToTimeLimitedDataProtector();

    // ---- helpers ----------------------------------------------------------------------------------------------------------------

    private static Task<SystemSetting> LoadAsync(AppDbContext db, CancellationToken ct) => SettingsStore.LoadAsync(db, ct);

    /// <summary>
    /// A call of an endpoint of <c>office.edit</c> at the address for the server, with the key of the binding: at the base that answered last for this address,
    /// else <c>/api/provides/office.edit</c> and then <c>/api/v1/host</c> (an endpoint a provider does not have answers 401, 404 or 405 there). The answer of the
    /// last base tried, a 401 before any other refusal.
    /// </summary>
    private static async Task<HttpResponseMessage> SendAsync(HttpClient http, MatBinding office, HttpMethod method, string path, Func<HttpContent>? content, CancellationToken ct)
    {
        var server = office.ServerUrl ?? throw new InvalidOperationException("office.edit is not bound.");
        IEnumerable<string> bases = KnownBase.TryGetValue(server, out var known) ? [known, .. ApiBases.Where(b => b != known)] : ApiBases;
        HttpResponseMessage? refused = null;
        foreach (var apiBase in bases)
        {
            using var request = new HttpRequestMessage(method, server + apiBase + path) { Content = content?.Invoke() };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", office.Key);
            var response = await http.SendAsync(request, ct);
            if (response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed) && (int)response.StatusCode is < 300 or >= 400)
            {
                KnownBase[server] = apiBase;
                refused?.Dispose();
                return response;
            }

            if (refused is null || refused.StatusCode != HttpStatusCode.Unauthorized)
            {
                refused?.Dispose();
                refused = response;
            }
            else
            {
                response.Dispose();
            }
        }

        KnownBase.TryRemove(server, out _);
        return refused!;
    }

    private static async Task<string?> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return (await response.Content.ReadFromJsonAsync<ErrorAnswer>(Json, ct))?.Error;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Reads the answer of <c>…/formats</c>.</summary>
    public static IReadOnlyList<OfficeKind> ParseKinds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            var answer = JsonSerializer.Deserialize<FormatsAnswer>(json, Json);
            return answer?.App == "matoffice" && answer.Kinds is { } kinds
                ? kinds.Where(k => k.Kind is not null).Select(k => new OfficeKind(k.Kind!, k.Name ?? k.Kind!, k.Colour ?? "#d83b01", k.Native ?? string.Empty, k.NewFormat ?? k.Native ?? string.Empty,
                    (k.Formats ?? []).Where(f => f.Ext is not null).Select(f => new OfficeFormat(f.Ext!.ToLowerInvariant(), f.Label ?? f.Ext!, f.Open, f.Write)).ToList())).ToList()
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static Task<bool> IsAdminAsync(AppDbContext db, long userId, CancellationToken ct)
        => db.Users.AnyAsync(u => u.Id == userId && u.IsActive && u.Role == UserRole.Admin, ct);

    private sealed record ErrorAnswer(string? Error);

    private sealed record SessionAnswer(string? Token, string? Path, string? Url, string? Kind, bool ReadOnly);

    private sealed record FormatsAnswer(string? App, string? Version, List<KindAnswer>? Kinds);

    private sealed record KindAnswer(string? Kind, string? Name, string? Colour, string? Native, string? NewFormat, List<FormatAnswer>? Formats);

    private sealed record FormatAnswer(string? Ext, string? Label, string? Mime, bool Open, bool Write);
}
