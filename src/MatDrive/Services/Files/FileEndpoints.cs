using System.Text.Json;
using MatDrive.Data;
using MatDrive.Localization;
using MatDrive.Services.Office;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace MatDrive.Services.Files;

public sealed record IdsRequest(long[]? Ids);

public sealed record TargetRequest(long[]? Ids, long DriveId, long? ParentId);

public sealed record NameRequest(long Id, string? Name);

public sealed record FolderRequest(long DriveId, long? ParentId, string? Name);

public sealed record StarRequest(long Id, bool Starred);

public sealed record StartUploadRequest(long DriveId, long? ParentId, string? Path, long Size);

public sealed record TextRequest(string? Text, string? ETag);

public sealed record OfficeNewRequest(string? Kind, long DriveId, long? ParentId);

/// <summary>
/// The addresses of the explorer (JSON under <c>/live/drive</c>, every change with the anti-forgery header <c>RequestVerificationToken</c>), the content of files
/// (<c>/files/{id}/content</c>, with ranges, inline only for types that cannot run script) and the file API MatOffice uses (<c>/api/v1/office/files/{id}/content</c>,
/// a token of <see cref="OfficeService"/> instead of a cookie). What a request may do is decided by the business layer.
/// </summary>
public static class FileEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapFileEndpoints(this IEndpointRouteBuilder app)
    {
        // ---- content ----------------------------------------------------------------------------------------------------------
        app.MapGet("/files/{id:long}/content", async (long id, long? v, bool? download, HttpContext http, AppDbContext db, BlobStore store, CurrentUser me) =>
        {
            var file = await FileService.OpenAsync(db, store, Actor.Of(me.RequiredId), id, v, http.RequestAborted);
            if (file is null)
            {
                return Results.NotFound();
            }

            if (download == true)
            {
                await ActivityLog.WriteAsync(db, me.Id, "download", file.Node, ct: http.RequestAborted);
            }

            return Serve(http, file, download == true);
        });

        app.MapGet("/files/zip", async (string ids, HttpContext http, AppDbContext db, BlobStore store, CurrentUser me) =>
        {
            var list = ParseIds(ids);
            var zip = await FileService.ZipAsync(db, store, Actor.Of(me.RequiredId), list, http.RequestAborted);
            if (zip is null)
            {
                return Results.NotFound();
            }

            var stream = new FileStream(zip.Value.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
            return Results.File(stream, "application/zip", zip.Value.Name);
        });

        // ---- the explorer -----------------------------------------------------------------------------------------------------
        var live = app.MapGroup("/live/drive").AddEndpointFilter(Antiforgery);

        live.MapPost("/upload", async (HttpContext http, AppDbContext db, BlobStore store, CurrentUser me) =>
        {
            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            if (!long.TryParse(form["driveId"], out var driveId))
            {
                return Results.BadRequest();
            }

            long? parentId = long.TryParse(form["parentId"], out var p) ? p : null;
            var mode = form["mode"] == "keep" ? UploadMode.KeepBoth : UploadMode.NewVersion;
            var paths = form["path"];
            var results = new List<object>();
            for (var i = 0; i < form.Files.Count; i++)
            {
                var file = form.Files[i];
                var path = i < paths.Count && !string.IsNullOrWhiteSpace(paths[i]) ? paths[i]! : file.FileName;
                await using var stream = file.OpenReadStream();
                var up = await FileService.UploadAsync(db, store, Actor.Of(me.RequiredId), driveId, parentId, path, stream, mode, "upload", http.RequestAborted);
                if (up.NotAllowed)
                {
                    return Results.NotFound();
                }

                results.Add(up.Ok ? new { name = up.Value!.Name, id = up.Value.Id } : new { name = file.FileName, error = up.Error });
            }

            return Results.Json(results, Json);
        }).DisableRequestSizeLimitFor();

        live.MapPost("/uploads", async (StartUploadRequest body, HttpContext http, AppDbContext db, CurrentUser me) =>
        {
            var started = await FileService.StartUploadAsync(db, Actor.Of(me.RequiredId), body.DriveId, body.ParentId, body.Path ?? string.Empty, body.Size, http.RequestAborted);
            return started.NotAllowed ? Results.NotFound() : started.Ok ? Results.Json(new { token = started.Value!.Token }, Json) : Problem(started.Error);
        });

        live.MapPut("/uploads/{token:guid}", async (Guid token, long offset, HttpContext http, AppDbContext db, BlobStore store, CurrentUser me) =>
        {
            var appended = await FileService.AppendUploadAsync(db, store, me.RequiredId, token, offset, http.Request.Body, http.RequestAborted);
            return appended.NotAllowed ? Results.NotFound() : appended.Ok ? Results.Json(new { received = appended.Value }, Json) : Problem(appended.Error);
        }).DisableRequestSizeLimitFor();

        live.MapPost("/uploads/{token:guid}/finish", async (Guid token, string? mode, HttpContext http, AppDbContext db, BlobStore store, CurrentUser me) =>
        {
            var done = await FileService.FinishUploadAsync(db, store, me.RequiredId, token, mode == "keep" ? UploadMode.KeepBoth : UploadMode.NewVersion, http.RequestAborted);
            return done.NotAllowed ? Results.NotFound() : done.Ok ? Results.Json(new { id = done.Value!.Id, name = done.Value.Name }, Json) : Problem(done.Error);
        });

        live.MapPost("/folders", async (FolderRequest body, HttpContext http, AppDbContext db, CurrentUser me) =>
        {
            var made = await NodeService.CreateFolderAsync(db, Actor.Of(me.RequiredId), body.DriveId, body.ParentId, body.Name ?? string.Empty, http.RequestAborted);
            return made.NotAllowed ? Results.NotFound() : made.Ok ? Results.Json(new { id = made.Value!.Id, name = made.Value.Name }, Json) : Problem(made.Error);
        });

        live.MapPost("/rename", async (NameRequest body, HttpContext http, AppDbContext db, CurrentUser me) =>
        {
            var renamed = await NodeService.RenameAsync(db, Actor.Of(me.RequiredId), body.Id, body.Name ?? string.Empty, http.RequestAborted);
            return renamed.NotAllowed ? Results.NotFound() : renamed.Ok ? Results.Json(new { name = renamed.Value!.Name }, Json) : Problem(renamed.Error);
        });

        live.MapPost("/move", async (TargetRequest body, HttpContext http, AppDbContext db, CurrentUser me) =>
        {
            var moved = await NodeService.MoveAsync(db, Actor.Of(me.RequiredId), body.Ids ?? [], body.DriveId, body.ParentId, http.RequestAborted);
            return moved.NotAllowed ? Results.NotFound() : moved.Ok ? Results.Json(new { count = moved.Value }, Json) : Problem(moved.Error);
        });

        live.MapPost("/copy", async (TargetRequest body, HttpContext http, AppDbContext db, BlobStore store, CurrentUser me) =>
        {
            var copied = await NodeService.CopyAsync(db, store, Actor.Of(me.RequiredId), body.Ids ?? [], body.DriveId, body.ParentId, http.RequestAborted);
            return copied.NotAllowed ? Results.NotFound() : copied.Ok ? Results.Json(new { count = copied.Value }, Json) : Problem(copied.Error);
        });

        live.MapPost("/trash", async (IdsRequest body, HttpContext http, AppDbContext db, CurrentUser me) =>
            Results.Json(new { count = await NodeService.TrashAsync(db, Actor.Of(me.RequiredId), body.Ids ?? [], http.RequestAborted) }, Json));

        live.MapPost("/restore", async (IdsRequest body, HttpContext http, AppDbContext db, CurrentUser me) =>
            Results.Json(new { count = await NodeService.RestoreAsync(db, Actor.Of(me.RequiredId), body.Ids ?? [], http.RequestAborted) }, Json));

        live.MapPost("/delete", async (IdsRequest body, HttpContext http, AppDbContext db, BlobStore store, CurrentUser me) =>
        {
            var blobs = await NodeService.DeleteForeverAsync(db, Actor.Of(me.RequiredId), body.Ids ?? [], http.RequestAborted);
            foreach (var blob in blobs)
            {
                store.Delete(blob);
            }

            return Results.Json(new { count = blobs.Count }, Json);
        });

        live.MapPost("/star", async (StarRequest body, HttpContext http, AppDbContext db, CurrentUser me) =>
            await NodeService.StarAsync(db, me.RequiredId, body.Id, body.Starred, http.RequestAborted) ? Results.NoContent() : Results.NotFound());

        live.MapPost("/unzip", async (NameRequest body, HttpContext http, AppDbContext db, BlobStore store, CurrentUser me) =>
        {
            var folder = await FileService.UnzipAsync(db, store, Actor.Of(me.RequiredId), body.Id, http.RequestAborted);
            return folder.NotAllowed ? Results.NotFound() : folder.Ok ? Results.Json(new { id = folder.Value!.Id, name = folder.Value.Name }, Json) : Problem(folder.Error);
        });

        // the folders of a drive for the "Move to" picker (one level at a time)
        live.MapGet("/tree", async (long driveId, long? parentId, HttpContext http, AppDbContext db, CurrentUser me) =>
        {
            var page = await NodeService.ListFolderAsync(db, Actor.Of(me.RequiredId), driveId, parentId, new NodeQuery(null, "folder", "name", false, 1, 500), http.RequestAborted);
            return page is null ? Results.NotFound() : Results.Json(page.Items.Select(n => new { n.Id, n.Name, n.Colour }), Json);
        });

        live.MapGet("/drives", async (HttpContext http, AppDbContext db, CurrentUser me) =>
        {
            var mine = await DriveService.MyDriveAsync(db, me.RequiredId, http.RequestAborted);
            var shared = await DriveService.SharedDrivesAsync(db, me.RequiredId, http.RequestAborted);
            var list = new List<object> { new { id = mine.Id, name = Text.T("Drive.My"), kind = "personal", canEdit = true } };
            list.AddRange(shared.Select(d => new { id = d.Id, name = d.Name, kind = "shared", canEdit = d.Role >= AccessRole.Editor }));
            return Results.Json(list, Json);
        });

        live.MapGet("/details/{id:long}", async (long id, HttpContext http, AppDbContext db, CurrentUser me) =>
        {
            var actor = Actor.Of(me.RequiredId);
            var access = await DriveAccess.FindAsync(db, actor, id, trashed: true, ct: http.RequestAborted);
            if (access is null)
            {
                return Results.NotFound();
            }

            var n = access.Node;
            var path = await NodeService.PathAsync(db, actor, n, http.RequestAborted);
            var activity = await ActivityLog.OfNodeAsync(db, actor, id, 20, http.RequestAborted) ?? [];
            var shares = await ShareService.SharesAsync(db, actor, id, http.RequestAborted) ?? [];
            var versions = n.Kind == NodeKind.File ? (await FileService.VersionsAsync(db, actor, id, http.RequestAborted))?.Count ?? 0 : 0;
            var owner = await Owners(db, n.OwnerId, http.RequestAborted);
            return Results.Json(new
            {
                n.Id, n.Name, kind = n.Kind == NodeKind.Folder ? "folder" : "file", n.Size, n.MimeType, n.CreateDate, n.ModifiedDate, n.Description,
                category = n.Kind == NodeKind.Folder ? FileTypes.Folder : FileTypes.CategoryOf(n.Name, n.MimeType),
                owner, drive = access.Drive.Kind == DriveKind.Shared ? access.Drive.Name : Text.T("Drive.My"), role = access.Role.ToString().ToLowerInvariant(),
                path = path.Select(c => new { c.Id, c.Name }), shares = shares.Select(s => new { s.Name, s.Email, role = s.Role.ToString().ToLowerInvariant() }), versions,
                activity = activity.Select(a => new { a.Date, a.Action, a.Target, a.Detail, a.UserName }),
            }, Json);
        });

        // ---- the text editor --------------------------------------------------------------------------------------------------
        live.MapGet("/text/{id:long}", async (long id, HttpContext http, AppDbContext db, BlobStore store, CurrentUser me) =>
        {
            var read = await FileService.ReadTextAsync(db, store, Actor.Of(me.RequiredId), id, http.RequestAborted);
            http.Response.Headers.CacheControl = "no-store";
            return read.NotAllowed ? Results.NotFound() : read.Ok ? Results.Json(new { text = read.Value.Text, etag = read.Value.ETag, name = read.Value.Node.Name }, Json) : Problem(read.Error);
        });

        live.MapPut("/text/{id:long}", async (long id, TextRequest body, HttpContext http, AppDbContext db, BlobStore store, CurrentUser me) =>
        {
            var (status, etag) = await FileService.SaveTextAsync(db, store, Actor.Of(me.RequiredId), id, body.Text ?? string.Empty, body.ETag, http.RequestAborted);
            return status switch
            {
                WriteStatus.Written or WriteStatus.Created => Results.Json(new { etag }, Json),
                WriteStatus.Conflict => Results.Json(new { error = Text.T("Editor.Conflict") }, Json, statusCode: StatusCodes.Status409Conflict),
                WriteStatus.QuotaFull => Problem(Text.T("Files.QuotaFull")),
                WriteStatus.TooLarge => Problem(Text.T("Files.TooLarge")),
                _ => Results.NotFound(),
            };
        });

        // ---- MatOffice ----------------------------------------------------------------------------------------------------------
        live.MapPost("/office/new", async (OfficeNewRequest body, HttpContext http, AppDbContext db, BlobStore store, IDataProtectionProvider protection, IHttpClientFactory clients, CurrentUser me) =>
        {
            var made = await OfficeService.CreateAsync(db, store, protection, clients.CreateClient("office"), Actor.Of(me.RequiredId), body.DriveId, body.ParentId, body.Kind ?? string.Empty, Languages.Current, http.RequestAborted);
            return made.NotAllowed ? Results.NotFound() : made.Ok ? Results.Json(new { id = made.Value!.Id, name = made.Value.Name, open = $"/office/open/{made.Value.Id}" }, Json) : Problem(made.Error);
        });

        // the file API for MatOffice: the token of the session is the right (checked against the person again on every call)
        var office = app.MapGroup("/api/v1/office/files/{id:long}/content").AllowAnonymous();
        office.MapGet(string.Empty, async (long id, HttpContext http, AppDbContext db, BlobStore store, IDataProtectionProvider protection) =>
        {
            if (OfficeService.ReadToken(protection, Bearer(http), id) is not { } token)
            {
                return Results.Unauthorized();
            }

            var file = await FileService.OpenAsync(db, store, Actor.Of(token.UserId), id, ct: http.RequestAborted);
            return file is null ? Results.NotFound() : Serve(http, file, download: true);
        });

        office.MapPut(string.Empty, async (long id, HttpContext http, AppDbContext db, BlobStore store, IDataProtectionProvider protection) =>
        {
            if (OfficeService.ReadToken(protection, Bearer(http), id) is not { } token)
            {
                return Results.Unauthorized();
            }

            if (!token.CanWrite)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var ifMatch = http.Request.Headers.IfMatch.ToString();
            var (status, node, version) = await FileService.WriteAsync(db, store, Actor.Of(token.UserId), id, http.Request.Body, "office", ifMatch, OfficeService.Coalesce, http.RequestAborted);
            switch (status)
            {
                case WriteStatus.Written:
                    http.Response.Headers.ETag = $"\"{version!.Sha256}\"";
                    return Results.Json(new { version = version.Number, size = version.Size }, Json);
                case WriteStatus.Conflict:
                    return Results.StatusCode(StatusCodes.Status412PreconditionFailed);
                case WriteStatus.TooLarge:
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                case WriteStatus.QuotaFull:
                    return Results.StatusCode(StatusCodes.Status507InsufficientStorage);
                default:
                    return Results.NotFound();
            }
        }).DisableRequestSizeLimitFor();
    }

    /// <summary>Streams a file: ranges for video and audio, an ETag, and a download, or inline only for types that cannot run script in this origin.</summary>
    public static IResult Serve(HttpContext http, FileContent file, bool download)
    {
        var inline = !download && IsSafeInline(file.MimeType);
        var headers = http.Response.Headers;
        headers[HeaderNames.XContentTypeOptions] = "nosniff";
        headers.CacheControl = "private, no-cache";
        if (inline)
        {
            // even an image is sandboxed: an SVG can carry script
            headers.ContentSecurityPolicy = "default-src 'none'; img-src 'self' data:; media-src 'self'; style-src 'unsafe-inline'; sandbox; frame-ancestors 'self'";
            if (file.MimeType == "application/pdf")
            {
                // the PDF viewer of the browser does not run in a sandbox; a PDF cannot reach the page anyway
                headers.ContentSecurityPolicy = "default-src 'none'; img-src 'self' data: blob:; style-src 'unsafe-inline'; frame-ancestors 'self'";
            }

            headers.XFrameOptions = "SAMEORIGIN";
        }

        var type = inline ? file.MimeType : "application/octet-stream";
        var tag = new EntityTagHeaderValue($"\"{file.ETag}\"");
        return Results.File(file.PhysicalPath, type, inline ? null : file.Name, new DateTimeOffset(DateTime.SpecifyKind(file.LastModified, DateTimeKind.Utc)), tag, enableRangeProcessing: true);
    }

    /// <summary>The types a browser shows without running anything of the file (HTML and SVG are never inline).</summary>
    public static bool IsSafeInline(string mime)
        => mime is "application/pdf" or "text/plain" or "text/csv" or "text/markdown" or "application/json"
           || (mime.StartsWith("image/", StringComparison.Ordinal) && mime != "image/svg+xml")
           || mime.StartsWith("video/", StringComparison.Ordinal) || mime.StartsWith("audio/", StringComparison.Ordinal);

    public static long[] ParseIds(string? ids)
        => (ids ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => long.TryParse(s, out var v) ? v : 0).Where(v => v > 0).Distinct().Take(1000).ToArray();

    private static string? Bearer(HttpContext http)
    {
        var header = http.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : null;
    }

    private static async Task<string> Owners(AppDbContext db, long ownerId, CancellationToken ct)
        => await db.Users.IgnoreQueryFilters().Where(u => u.Id == ownerId).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) ?? string.Empty;

    private static IResult Problem(string? error) => Results.Json(new { error }, Json, statusCode: StatusCodes.Status422UnprocessableEntity);

    /// <summary>The anti-forgery token of the page in the header <c>RequestVerificationToken</c> for every change; reads pass.</summary>
    public static async ValueTask<object?> Antiforgery(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (!HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method))
        {
            try
            {
                await http.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(http);
            }
            catch (AntiforgeryValidationException)
            {
                return Results.BadRequest();
            }
        }

        return await next(context);
    }

    /// <summary>Uploads may be as large as the installation allows (checked while the bytes arrive).</summary>
    public static RouteHandlerBuilder DisableRequestSizeLimitFor(this RouteHandlerBuilder builder)
        => builder.WithMetadata(new Microsoft.AspNetCore.Mvc.DisableRequestSizeLimitAttribute()).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestFormLimitsAttribute { MultipartBodyLengthLimit = long.MaxValue, ValueLengthLimit = int.MaxValue });
}

