using System.Text.Json;
using MatDrive.Data;
using Microsoft.AspNetCore.DataProtection;

namespace MatDrive.Services.Files;

/// <summary>
/// A link that is protected by a password is opened once with it: the browser then keeps a cookie that names the link (protected by the keys of the data volume,
/// only for this session of the browser).
/// </summary>
public static class LinkGate
{
    private const string Purpose = "MatDrive.LinkUnlock";

    public static string CookieName(long linkId) => "mdl-" + linkId;

    public static bool IsOpen(HttpContext http, IDataProtectionProvider protection, ShareLink link)
    {
        if (link.PasswordHash is null)
        {
            return true;
        }

        var cookie = http.Request.Cookies[CookieName(link.Id)];
        if (string.IsNullOrEmpty(cookie))
        {
            return false;
        }

        try
        {
            return protection.CreateProtector(Purpose).Unprotect(cookie) == $"{link.Id}|{link.Token}|{Stamp(link)}";
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }

    /// <summary>A part of the password hash: a new password ends every cookie of the old one.</summary>
    private static string Stamp(ShareLink link) => link.PasswordHash is { Length: > 12 } h ? h[^12..] : link.PasswordHash ?? string.Empty;

    public static void Open(HttpContext http, IDataProtectionProvider protection, ShareLink link)
        => http.Response.Cookies.Append(CookieName(link.Id), protection.CreateProtector(Purpose).Protect($"{link.Id}|{link.Token}|{Stamp(link)}"),
            new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Lax, Secure = http.Request.IsHttps, Path = "/l/" + link.Token.ToString("N") });
}

/// <summary>The files behind a shared link (<c>/l/{token}/…</c>): anonymous, the link is the right; only inside the item the link points to.</summary>
public static class LinkEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapLinkEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/l/{token:guid}").AllowAnonymous();

        group.MapGet("/files/{id:long}/content", async (Guid token, long id, bool? download, HttpContext http, AppDbContext db, BlobStore store, IDataProtectionProvider protection, CurrentUser me) =>
        {
            var link = await ShareService.OpenLinkAsync(db, token, http.RequestAborted);
            if (link is null || !LinkGate.IsOpen(http, protection, link.Link) || (download == true && !link.Link.AllowDownload))
            {
                return Results.NotFound();
            }

            var file = await FileService.OpenAsync(db, store, link.ActorFor(null), id, ct: http.RequestAborted);
            if (file is null)
            {
                return Results.NotFound();
            }

            if (download == true)
            {
                await ShareService.CountAsync(db, link.Link.Id, true, http.RequestAborted);
                await ActivityLog.WriteAsync(db, null, "download", file.Node, "link", ct: http.RequestAborted);
            }

            return FileEndpoints.Serve(http, file, download == true);
        });

        group.MapGet("/zip", async (Guid token, string ids, HttpContext http, AppDbContext db, BlobStore store, IDataProtectionProvider protection) =>
        {
            var link = await ShareService.OpenLinkAsync(db, token, http.RequestAborted);
            if (link is null || !LinkGate.IsOpen(http, protection, link.Link) || !link.Link.AllowDownload)
            {
                return Results.NotFound();
            }

            var zip = await FileService.ZipAsync(db, store, link.ActorFor(null), FileEndpoints.ParseIds(ids), http.RequestAborted);
            if (zip is null)
            {
                return Results.NotFound();
            }

            await ShareService.CountAsync(db, link.Link.Id, true, http.RequestAborted);
            var stream = new FileStream(zip.Value.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
            return Results.File(stream, "application/zip", zip.Value.Name);
        });

        // a folder link with the right to add: uploads into the folder (or a folder below it)
        group.MapPost("/upload", async (Guid token, long? f, HttpContext http, AppDbContext db, BlobStore store, IDataProtectionProvider protection) =>
        {
            var link = await ShareService.OpenLinkAsync(db, token, http.RequestAborted);
            if (link is null || !LinkGate.IsOpen(http, protection, link.Link) || link.Link.Role < AccessRole.Editor || link.Node.Kind != NodeKind.Folder)
            {
                return Results.NotFound();
            }

            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            var results = new List<object>();
            foreach (var file in form.Files)
            {
                await using var stream = file.OpenReadStream();
                var up = await FileService.UploadAsync(db, store, link.ActorFor(null), link.Drive.Id, f ?? link.Node.Id, file.FileName, stream, UploadMode.KeepBoth, "link", http.RequestAborted);
                results.Add(up.Ok ? new { name = up.Value!.Name } : new { name = file.FileName, error = up.Error ?? "not allowed" });
            }

            return Results.Json(results, Json);
        }).AddEndpointFilter(FileEndpoints.Antiforgery).DisableRequestSizeLimitFor();
    }
}
