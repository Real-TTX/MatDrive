using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MatDrive.Data;
using MatDrive.Localization;
using MatDrive.Services.Files;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace MatDrive.Services.Office;

/// <summary>A kind of document MatOffice edits (Docs, Sheets, Slides, Base) with the file types it opens and writes.</summary>
public sealed record OfficeKind(string Kind, string Name, string Colour, string Native, string NewFormat, IReadOnlyList<OfficeFormat> Formats);

public sealed record OfficeFormat(string Ext, string Label, bool Open, bool Write);

/// <summary>The settings of the integration as the admin page shows them (the key itself is never shown again).</summary>
public sealed record OfficeSettings(bool Enabled, string? Url, string? InternalUrl, bool HasKey, string? DriveUrlForOffice, IReadOnlyList<OfficeKind> Kinds, DateTime? CheckedDate);

/// <summary>What the admin page sends; an empty key keeps the one that is stored.</summary>
public sealed record OfficeInput(bool Enabled, string? Url, string? InternalUrl, string? Key, string? DriveUrlForOffice);

/// <summary>
/// MatOffice as the editor of the office files of MatDrive (two containers, one integration). MatOffice made an API key for MatDrive (MatOffice: Administration →
/// Connected apps); MatDrive keeps it encrypted. To open a file, MatDrive starts a session at MatOffice (<c>POST /api/v1/host/sessions</c>) and hands over where
/// MatOffice reads and writes the file (<c>/api/v1/office/files/{id}/content</c> here) together with a token that names the file, the person and the right
/// and expires after 12 hours. MatOffice writes back with <c>If-Match</c>, so a file that changed meanwhile is not overwritten. The right of the person is
/// checked again on every read and write.
/// </summary>
public static class OfficeService
{
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(12);

    /// <summary>A save of the same person from MatOffice within this time replaces their last version (MatOffice saves every few seconds).</summary>
    public static readonly TimeSpan Coalesce = TimeSpan.FromMinutes(10);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public const string ProtectorPurpose = "MatDrive.Office.FileToken";
    private const string KeyPurpose = "MatDrive.Office.ApiKey";

    // ---- settings ---------------------------------------------------------------------------------------------------------------

    public static async Task<OfficeSettings?> SettingsAsync(AppDbContext db, long adminId, CancellationToken ct = default)
        => await IsAdminAsync(db, adminId, ct) ? Settings(await LoadAsync(db, ct)) : null;

    /// <summary>The kinds MatOffice edits, while the integration is on (for the menus of everybody); empty otherwise.</summary>
    public static async Task<IReadOnlyList<OfficeKind>> ActiveKindsAsync(AppDbContext db, CancellationToken ct = default)
    {
        var s = await db.SystemSettings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == 1, ct);
        return s is { OfficeEnabled: true, OfficeUrl: not null, OfficeKeyProtected: not null } ? ParseKinds(s.OfficeFormats) : [];
    }

    /// <summary>The format of a file name MatOffice can open (and whether it can write it), or null.</summary>
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

    public static async Task<Outcome<bool>> SaveSettingsAsync(AppDbContext db, IDataProtectionProvider protection, long adminId, OfficeInput input, CancellationToken ct = default)
    {
        if (!await IsAdminAsync(db, adminId, ct))
        {
            return Outcome<bool>.Denied();
        }

        string? Url(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().TrimEnd('/');
        var url = Url(input.Url);
        var internalUrl = Url(input.InternalUrl);
        var driveUrl = Url(input.DriveUrlForOffice);
        foreach (var u in new[] { url, internalUrl, driveUrl })
        {
            if (u is not null && !(Uri.TryCreate(u, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https"))
            {
                return Outcome<bool>.Fail(Text.T("Office.UrlInvalid"));
            }
        }

        var s = await LoadAsync(db, ct);
        if (input.Enabled && url is null)
        {
            return Outcome<bool>.Fail(Text.T("Office.UrlRequired"));
        }

        if (!string.IsNullOrWhiteSpace(input.Key))
        {
            s.OfficeKeyProtected = protection.CreateProtector(KeyPurpose).Protect(input.Key.Trim());
        }

        if (input.Enabled && s.OfficeKeyProtected is null)
        {
            return Outcome<bool>.Fail(Text.T("Office.KeyRequired"));
        }

        s.OfficeEnabled = input.Enabled;
        s.OfficeUrl = url;
        s.OfficeInternalUrl = internalUrl;
        s.DriveUrlForOffice = driveUrl;
        ActivityLog.Add(db, adminId, "office", detail: input.Enabled ? "on" : "off", target: "MatOffice");
        await db.SaveChangesAsync(ct);
        return Outcome<bool>.Success(true);
    }

    /// <summary>Asks MatOffice which files it opens (and so checks the address and the key); keeps the answer for the menus.</summary>
    public static async Task<Outcome<IReadOnlyList<OfficeKind>>> CheckAsync(AppDbContext db, IDataProtectionProvider protection, HttpClient http, long adminId, CancellationToken ct = default)
    {
        if (!await IsAdminAsync(db, adminId, ct))
        {
            return Outcome<IReadOnlyList<OfficeKind>>.Denied();
        }

        var s = await LoadAsync(db, ct);
        if (s.OfficeUrl is null || s.OfficeKeyProtected is null)
        {
            return Outcome<IReadOnlyList<OfficeKind>>.Fail(Text.T("Office.NotSetUp"));
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ServerUrl(s) + "/api/v1/host/formats");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Key(protection, s));
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                return Outcome<IReadOnlyList<OfficeKind>>.Fail(Text.T("Office.KeyWrong"));
            }

            if (!response.IsSuccessStatusCode)
            {
                return Outcome<IReadOnlyList<OfficeKind>>.Fail(Text.T("Office.Answer", (int)response.StatusCode));
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            var kinds = ParseKinds(body);
            if (kinds.Count == 0)
            {
                return Outcome<IReadOnlyList<OfficeKind>>.Fail(Text.T("Office.NotMatOffice"));
            }

            s.OfficeFormats = body;
            s.OfficeCheckedDate = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return Outcome<IReadOnlyList<OfficeKind>>.Success(kinds);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or CryptographicLikeException)
        {
            return Outcome<IReadOnlyList<OfficeKind>>.Fail(Text.T("Office.Unreachable"));
        }
    }

    // ---- open and create --------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Opens a file in MatOffice: starts a session there and answers the address the browser goes to. Editors edit, everybody else reads; a format MatOffice
    /// cannot write opens read-only. <paramref name="driveOrigin"/> is the address of this request (used when no address is configured).
    /// </summary>
    public static async Task<Outcome<string>> OpenAsync(AppDbContext db, IDataProtectionProvider protection, HttpClient http, Actor actor, long nodeId, string driveOrigin, string language, CancellationToken ct = default)
    {
        var access = await DriveAccess.FindAsync(db, actor, nodeId, ct: ct);
        if (access is null || access.Node.Kind != NodeKind.File || actor.UserId is not { } userId)
        {
            return Outcome<string>.Denied();
        }

        var s = await LoadAsync(db, ct);
        var kinds = s.OfficeEnabled ? ParseKinds(s.OfficeFormats) : [];
        if (s.OfficeUrl is null || s.OfficeKeyProtected is null || FormatOf(kinds, access.Node.Name) is not { } format)
        {
            return Outcome<string>.Fail(Text.T("Office.CannotOpen"));
        }

        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, ct);
        var canWrite = access.CanEdit && format.Format.Write;
        var token = Protector(protection).Protect($"{nodeId}|{userId}|{(canWrite ? 1 : 0)}", TokenLifetime);
        var origin = InstanceService.NormalizePublicUrl(s.PublicUrl) ?? driveOrigin;
        var forOffice = s.DriveUrlForOffice ?? origin;
        var back = origin + (access.Node.ParentId is { } p ? $"/drive/folder/{p}" : access.Drive.Kind == DriveKind.Shared ? $"/drive/shared-drive/{access.Drive.Id}" : "/drive");
        var body = new
        {
            fileId = nodeId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            fileName = access.Node.Name,
            contentUrl = $"{forOffice}/api/v1/office/files/{nodeId}/content",
            accessToken = token,
            canWrite,
            user = new { id = userId.ToString(System.Globalization.CultureInfo.InvariantCulture), name = user.DisplayName, email = user.Email },
            returnUrl = back,
            language,
        };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, ServerUrl(s) + "/api/v1/host/sessions") { Content = JsonContent.Create(body, options: Json) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Key(protection, s));
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var problem = await response.Content.ReadFromJsonAsync<ErrorAnswer>(Json, ct).ConfigureAwait(false);
                return Outcome<string>.Fail(problem?.Error ?? Text.T("Office.Answer", (int)response.StatusCode));
            }

            var started = await response.Content.ReadFromJsonAsync<SessionAnswer>(Json, ct);
            if (started?.Path is null)
            {
                return Outcome<string>.Fail(Text.T("Office.NotMatOffice"));
            }

            await NodeService.VisitAsync(db, userId, nodeId, ct);
            await ActivityLog.WriteAsync(db, userId, "office", access.Node, canWrite ? "edit" : "view", ct: ct);
            return Outcome<string>.Success(s.OfficeUrl + started.Path);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        {
            return Outcome<string>.Fail(Text.T("Office.Unreachable"));
        }
    }

    /// <summary>A new document of MatOffice (an empty file of the kind, in the format MatOffice proposes) in a folder; answers the new file.</summary>
    public static async Task<Outcome<Node>> CreateAsync(AppDbContext db, BlobStore store, IDataProtectionProvider protection, HttpClient http, Actor actor, long driveId, long? parentId,
        string kind, string language, CancellationToken ct = default)
    {
        var s = await LoadAsync(db, ct);
        var k = (s.OfficeEnabled ? ParseKinds(s.OfficeFormats) : []).FirstOrDefault(x => x.Kind == kind);
        if (k is null || s.OfficeUrl is null || s.OfficeKeyProtected is null)
        {
            return Outcome<Node>.Fail(Text.T("Office.NotSetUp"));
        }

        var name = Languages.In(language, () => Text.T("Office.Untitled." + kind)) + k.NewFormat;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{ServerUrl(s)}/api/v1/host/new?kind={Uri.EscapeDataString(kind)}&format={Uri.EscapeDataString(k.NewFormat)}&language={language}&name={Uri.EscapeDataString(Path.GetFileNameWithoutExtension(name))}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Key(protection, s));
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                return Outcome<Node>.Fail(Text.T("Office.Answer", (int)response.StatusCode));
            }

            await using var content = await response.Content.ReadAsStreamAsync(ct);
            return await FileService.UploadAsync(db, store, actor, driveId, parentId, name, content, UploadMode.KeepBoth, "office", ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return Outcome<Node>.Fail(Text.T("Office.Unreachable"));
        }
    }

    // ---- the file API for MatOffice ---------------------------------------------------------------------------------------------

    /// <summary>The file, the person and the right a token of MatOffice stands for (null: wrong, expired, or not for this file).</summary>
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
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    private static ITimeLimitedDataProtector Protector(IDataProtectionProvider protection) => protection.CreateProtector(ProtectorPurpose).ToTimeLimitedDataProtector();

    // ---- helpers ----------------------------------------------------------------------------------------------------------------

    private static Task<SystemSetting> LoadAsync(AppDbContext db, CancellationToken ct) => SettingsStore.LoadAsync(db, ct);

    private static OfficeSettings Settings(SystemSetting s)
        => new(s.OfficeEnabled, s.OfficeUrl, s.OfficeInternalUrl, s.OfficeKeyProtected is not null, s.DriveUrlForOffice, ParseKinds(s.OfficeFormats), s.OfficeCheckedDate);

    private static string ServerUrl(SystemSetting s) => s.OfficeInternalUrl ?? s.OfficeUrl!;

    private static string Key(IDataProtectionProvider protection, SystemSetting s)
    {
        try
        {
            return protection.CreateProtector(KeyPurpose).Unprotect(s.OfficeKeyProtected!);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            throw new CryptographicLikeException();
        }
    }

    /// <summary>Reads the answer of <c>/api/v1/host/formats</c>.</summary>
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

    /// <summary>The stored key cannot be read (the keys of the data volume changed).</summary>
    private sealed class CryptographicLikeException : Exception;

    private sealed record ErrorAnswer(string? Error);

    private sealed record SessionAnswer(string? Token, string? Path, string? Url, string? Kind, bool ReadOnly);

    private sealed record FormatsAnswer(string? App, string? Version, List<KindAnswer>? Kinds);

    private sealed record KindAnswer(string? Kind, string? Name, string? Colour, string? Native, string? NewFormat, List<FormatAnswer>? Formats);

    private sealed record FormatAnswer(string? Ext, string? Label, string? Mime, bool Open, bool Write);
}
