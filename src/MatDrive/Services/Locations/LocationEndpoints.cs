using System.Net.Security;
using System.Text.Json;
using MatDrive.Data;
using MatDrive.Localization;
using MatDrive.Services.Files;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;

namespace MatDrive.Services.Locations;

public sealed record LocationNameRequest(string? Path, string? Name);

public sealed record LocationPathsRequest(string[]? Paths);

public sealed record LocationTransferRequest(string[]? Paths, long TargetId, string? TargetPath, bool Move);

public sealed record LocationToDriveRequest(string[]? Paths, long DriveId, long? ParentId);

public sealed record LocationFromDriveRequest(long[]? Ids, string? Path);

public sealed record LocationTextRequest(string? Path, string? Text, string? ETag);

public sealed record LocationModeRequest(string? Path, int Mode);

public sealed record LocationExistsRequest(string? Folder, string[]? Paths);

/// <summary>The services of the storage locations (one call in Program.cs).</summary>
public static class LocationSetup
{
    public static IServiceCollection AddLocations(this IServiceCollection services)
    {
        services.AddSingleton<LocationConnections>();
        services.AddSingleton<LocationMonitor>();
        services.AddHostedService(sp => sp.GetRequiredService<LocationMonitor>());

        // WebDAV: redirects by hand (the authorization stays on the same host), no timeout for the bytes (each call has its own for the answer)
        services.AddHttpClient(LocationConnections.WebDavClient, c => c.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(15), PooledConnectionLifetime = TimeSpan.FromMinutes(5) });
        services.AddHttpClient(LocationConnections.WebDavClientAnyCertificate, c => c.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectTimeout = TimeSpan.FromSeconds(15),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),

                // chosen by the administrator for a NAS with a certificate of its own (the switch of the location)
                SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, _, _, _) => true },
            });
        return services;
    }
}

/// <summary>
/// The addresses of the storage locations: the content of a file (<c>/location-files/{id}/content?path=</c>, with ranges, inline only for types that cannot run
/// script, with the policy of <see cref="FileEndpoints.Serve"/>), a selection as a zip file streamed while it is made (<c>/location-files/{id}/zip</c>), and the
/// JSON of the browse page under <c>/live/locations</c> (every change with the anti-forgery header). What a request may do is decided by the business layer.
/// </summary>
public static class LocationEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapLocationEndpoints(this IEndpointRouteBuilder app)
    {
        LocationErrors.Log ??= app.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("MatDrive.Locations");

        // ---- content ----------------------------------------------------------------------------------------------------------
        app.MapMethods("/location-files/{id:long}/content", ["GET", "HEAD"], async (long id, string? path, bool? download, HttpContext http, AppDbContext db, LocationConnections connections, CurrentUser me) =>
        {
            var file = await LocationService.FileAsync(db, connections, me.RequiredId, id, path, http.RequestAborted);
            if (file.NotAllowed)
            {
                return Results.NotFound();
            }

            if (!file.Ok)
            {
                return Problem(file.Error);
            }

            if (download == true && HttpMethods.IsGet(http.Request.Method) && !http.Request.Headers.ContainsKey(HeaderNames.Range))
            {
                await LocationService.LogAsync(db, me.RequiredId, file.Value!.Context, "download", file.Value.Entry.Path, ct: http.RequestAborted);
            }

            return await ServeAsync(http, connections, file.Value!, download == true);
        });

        app.MapGet("/location-files/{id:long}/zip", async (long id, HttpContext http, AppDbContext db, LocationConnections connections, CurrentUser me) =>
        {
            var paths = http.Request.Query["path"].Where(p => p is not null).Select(p => p!).Take(1000).ToList();
            var zip = await LocationService.PrepareZipAsync(db, connections, me.RequiredId, id, paths, http.RequestAborted);
            if (zip.NotAllowed)
            {
                return Results.NotFound();
            }

            if (!zip.Ok)
            {
                return Problem(zip.Error);
            }

            http.Response.Headers.CacheControl = "no-store";
            return Results.Stream(async output =>
            {
                try
                {
                    await LocationService.WriteZipAsync(zip.Value!, output, http.RequestAborted);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // the headers are out: a broken zip must not look complete
                    LocationErrors.Log?.LogWarning(ex, "A zip of location {Id} broke off.", id);
                    http.Abort();
                }
            }, "application/zip", zip.Value!.Name);
        });

        // ---- the browse page --------------------------------------------------------------------------------------------------
        var live = app.MapGroup("/live/locations").AddEndpointFilter(FileEndpoints.Antiforgery);

        // the locations of the person (the pickers of "Move to" and "Copy to")
        live.MapGet("/targets", async (HttpContext http, AppDbContext db, CurrentUser me) =>
        {
            var list = await LocationAccess.ListForPersonAsync(db, me.RequiredId, http.RequestAborted);
            return Results.Json(list.Select(l => new { id = l.Id, name = l.Name, kind = l.Kind.ToString().ToLowerInvariant(), canEdit = l.Role >= AccessRole.Editor }), Json);
        });

        live.MapGet("/{id:long}/folders", async (long id, string? path, HttpContext http, AppDbContext db, LocationConnections connections, CurrentUser me) =>
        {
            var folders = await LocationService.FoldersAsync(db, connections, me.RequiredId, id, path, http.RequestAborted);
            return folders.NotAllowed ? Results.NotFound() : folders.Ok ? Results.Json(folders.Value!.Select(f => new { name = f.Name, path = PathGuard.Display(f.Path) }), Json) : Problem(folders.Error);
        });

        live.MapPut("/{id:long}/file", async (long id, string? folder, string? path, string? conflict, HttpContext http, AppDbContext db, LocationConnections connections, CurrentUser me) =>
        {
            var mode = conflict switch
            {
                "replace" => LocationConflict.Replace,
                "keep" => LocationConflict.KeepBoth,
                _ => LocationConflict.Ask,
            };
            var written = await LocationService.UploadAsync(db, connections, me.RequiredId, id, folder, path, http.Request.Body, http.Request.ContentLength, mode, http.RequestAborted);
            if (written.NotAllowed)
            {
                return Results.NotFound();
            }

            if (written.Exists)
            {
                return Results.Json(new { error = written.Error, exists = true, name = written.Entry?.Name }, Json, statusCode: StatusCodes.Status409Conflict);
            }

            return written.Error is null ? Results.Json(new { name = written.Entry!.Name, path = PathGuard.Display(written.Entry.Path) }, Json) : Problem(written.Error);
        }).DisableRequestSizeLimitFor();

        // which files of an upload are there already (asked once before the bytes go out)
        live.MapPost("/{id:long}/exists", async (long id, LocationExistsRequest body, HttpContext http, AppDbContext db, LocationConnections connections, CurrentUser me) =>
            Answer(await LocationService.ExistingAsync(db, connections, me.RequiredId, id, body.Folder, body.Paths ?? [], http.RequestAborted), found => new { existing = found }));

        live.MapPost("/{id:long}/folders", async (long id, LocationNameRequest body, HttpContext http, AppDbContext db, LocationConnections connections, CurrentUser me) =>
            Answer(await LocationService.CreateFolderAsync(db, connections, me.RequiredId, id, body.Path, body.Name, http.RequestAborted), e => new { name = e.Name, path = PathGuard.Display(e.Path) }));

        live.MapPost("/{id:long}/rename", async (long id, LocationNameRequest body, HttpContext http, AppDbContext db, LocationConnections connections, CurrentUser me) =>
            Answer(await LocationService.RenameAsync(db, connections, me.RequiredId, id, body.Path, body.Name, http.RequestAborted), e => new { name = e.Name, path = PathGuard.Display(e.Path) }));

        live.MapPost("/{id:long}/delete", async (long id, LocationPathsRequest body, HttpContext http, AppDbContext db, LocationConnections connections, CurrentUser me) =>
            Answer(await LocationService.DeleteAsync(db, connections, me.RequiredId, id, body.Paths ?? [], http.RequestAborted), n => new { count = n }));

        live.MapPost("/{id:long}/transfer", async (long id, LocationTransferRequest body, HttpContext http, AppDbContext db, LocationConnections connections, CurrentUser me) =>
            Answer(await LocationService.TransferAsync(db, connections, me.RequiredId, id, body.Paths ?? [], body.TargetId, body.TargetPath, body.Move, http.RequestAborted), n => new { count = n }));

        live.MapPost("/{id:long}/to-drive", async (long id, LocationToDriveRequest body, HttpContext http, AppDbContext db, LocationConnections connections, BlobStore store, CurrentUser me) =>
            Answer(await LocationService.ToDriveAsync(db, connections, store, me.RequiredId, id, body.Paths ?? [], body.DriveId, body.ParentId, http.RequestAborted), n => new { count = n }));

        live.MapPost("/{id:long}/from-drive", async (long id, LocationFromDriveRequest body, HttpContext http, AppDbContext db, LocationConnections connections, BlobStore store, CurrentUser me) =>
            Answer(await LocationService.FromDriveAsync(db, connections, store, me.RequiredId, id, body.Ids ?? [], body.Path, http.RequestAborted), n => new { count = n }));

        live.MapPost("/{id:long}/unzip", async (long id, LocationNameRequest body, HttpContext http, AppDbContext db, LocationConnections connections, BlobStore store, CurrentUser me) =>
            Answer(await LocationService.UnzipAsync(db, connections, store, me.RequiredId, id, body.Path, http.RequestAborted), p => new { path = PathGuard.Display(p), name = PathGuard.NameOf(p) }));

        live.MapGet("/{id:long}/text", async (long id, string? path, HttpContext http, AppDbContext db, LocationConnections connections, CurrentUser me) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var read = await LocationService.ReadTextAsync(db, connections, me.RequiredId, id, path, http.RequestAborted);
            return read.NotAllowed ? Results.NotFound() : read.Ok
                ? Results.Json(new { text = read.Value.Text, etag = read.Value.ETag, name = read.Value.Entry.Name, canEdit = read.Value.CanEdit }, Json)
                : Problem(read.Error);
        });

        live.MapPut("/{id:long}/text", async (long id, LocationTextRequest body, HttpContext http, AppDbContext db, LocationConnections connections, CurrentUser me) =>
        {
            var (status, etag, error) = await LocationService.SaveTextAsync(db, connections, me.RequiredId, id, body.Path, body.Text ?? string.Empty, body.ETag, http.RequestAborted);
            return status switch
            {
                LocationSave.Saved => Results.Json(new { etag }, Json),
                LocationSave.Conflict => Results.Json(new { error }, Json, statusCode: StatusCodes.Status409Conflict),
                LocationSave.Failed => Problem(error),
                _ => Results.NotFound(),
            };
        });

        live.MapGet("/{id:long}/properties", async (long id, string? path, HttpContext http, AppDbContext db, LocationConnections connections, CurrentUser me) =>
        {
            var props = await LocationService.PropertiesAsync(db, connections, me.RequiredId, id, path, http.RequestAborted);
            if (!props.Ok)
            {
                return props.NotAllowed ? Results.NotFound() : Problem(props.Error);
            }

            var p = props.Value!;
            return Results.Json(new
            {
                name = p.Entry.Name, path = PathGuard.Display(p.Entry.Path), folder = p.Entry.IsFolder, size = p.Entry.Size,
                modified = p.Entry.Modified > DateTime.MinValue.AddDays(1) ? DateTime.SpecifyKind(p.Entry.Modified, DateTimeKind.Utc) : (DateTime?)null, hidden = p.Entry.IsHidden, link = p.Entry.IsLink,
                category = p.Entry.IsFolder ? FileTypes.Folder : FileTypes.CategoryOf(p.Entry.Name), mime = p.Entry.IsFolder ? null : FileTypes.MimeOf(p.Entry.Name),
                where = p.Where, kind = p.KindName, items = p.Items,
                permissions = p.Permissions is { } perm ? new { mode = perm.Mode, octal = LocationPermissions.ToText(perm.Mode), symbols = LocationPermissions.ToSymbols(perm.Mode), owner = perm.Owner, group = perm.Group } : null,
                canChange = p.CanChangePermissions,
            }, Json);
        });

        live.MapPost("/{id:long}/permissions", async (long id, LocationModeRequest body, HttpContext http, AppDbContext db, LocationConnections connections, CurrentUser me) =>
            Answer(await LocationService.SetPermissionsAsync(db, connections, me.RequiredId, id, body.Path, body.Mode, http.RequestAborted),
                p => new { mode = p.Mode, octal = LocationPermissions.ToText(p.Mode), symbols = LocationPermissions.ToSymbols(p.Mode) }));

        // the search below a folder: newline-delimited JSON, sent as it is found (closing the request stops it)
        live.MapGet("/{id:long}/search", async (long id, string? path, string? q, bool? content, bool? hidden, HttpContext http, AppDbContext db, LocationConnections connections, CurrentUser me) =>
        {
            var started = false;
            var found = await LocationSearch.RunAsync(db, connections, me.RequiredId, id, path, q, content == true, hidden == true, async e =>
            {
                if (!started)
                {
                    started = true;
                    http.Response.ContentType = "application/x-ndjson; charset=utf-8";
                    http.Response.Headers.CacheControl = "no-store";
                    http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
                }

                await JsonSerializer.SerializeAsync(http.Response.Body, e, Json, http.RequestAborted);
                await http.Response.Body.WriteAsync("\n"u8.ToArray(), http.RequestAborted);
                await http.Response.Body.FlushAsync(http.RequestAborted);
            }, http.RequestAborted);
            return found ? Results.Empty : Results.NotFound();
        });

        // the folders and files of a drive for "Copy from My Drive" (one level at a time)
        live.MapGet("/drive-items", async (long driveId, long? parentId, HttpContext http, AppDbContext db, CurrentUser me) =>
        {
            var page = await NodeService.ListFolderAsync(db, Actor.Of(me.RequiredId), driveId, parentId, new NodeQuery(null, null, "name", false, 1, 1000), http.RequestAborted);
            return page is null ? Results.NotFound() : Results.Json(page.Items.Select(n => new { n.Id, n.Name, folder = n.IsFolder, n.Size, category = n.Category, n.Colour }), Json);
        });
    }

    /// <summary>
    /// Streams a file of a location: one range (a video that jumps, a resumed download) or the whole, an ETag of its size and date, inline only for types that cannot
    /// run script (never HTML or SVG) and then with the same sandboxing policy as the files of the drives.
    /// </summary>
    private static async Task<IResult> ServeAsync(HttpContext http, LocationConnections connections, LocationFile file, bool download)
    {
        var entry = file.Entry;
        var mime = FileTypes.MimeOf(entry.Name);
        var inline = !download && FileEndpoints.IsSafeInline(mime);
        var request = http.Request;
        var response = http.Response;
        var headers = response.Headers;
        var etag = new EntityTagHeaderValue($"\"{LocationService.ETagOf(entry)}\"");

        headers[HeaderNames.XContentTypeOptions] = "nosniff";
        headers.CacheControl = "private, no-cache";
        headers.ETag = etag.ToString();
        headers.AcceptRanges = "bytes";
        if (entry.Modified > DateTime.MinValue)
        {
            headers.LastModified = new DateTimeOffset(DateTime.SpecifyKind(entry.Modified, DateTimeKind.Utc)).ToString("R");
        }

        if (inline)
        {
            // even an image is sandboxed: the same policy as FileEndpoints.Serve
            headers.ContentSecurityPolicy = mime == "application/pdf"
                ? "default-src 'none'; img-src 'self' data: blob:; style-src 'unsafe-inline'; frame-ancestors 'self'"
                : "default-src 'none'; img-src 'self' data:; media-src 'self'; style-src 'unsafe-inline'; sandbox; frame-ancestors 'self'";
            headers.XFrameOptions = "SAMEORIGIN";
        }

        var disposition = new ContentDispositionHeaderValue(inline ? "inline" : "attachment");
        disposition.SetHttpFileName(entry.Name);
        headers.ContentDisposition = disposition.ToString();

        if (request.Headers.IfNoneMatch.Count > 0 && request.Headers.IfNoneMatch.Any(v => v == etag.ToString() || v == "*"))
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        var size = entry.Size;
        long start = 0;
        var end = size - 1;
        var partial = false;
        var range = request.Headers.Range.ToString();
        var ifRange = request.Headers.IfRange.ToString();
        var rangeApplies = string.IsNullOrEmpty(ifRange) || ifRange == etag.ToString();
        if (!string.IsNullOrEmpty(range) && rangeApplies && RangeHeaderValue.TryParse(range, out var parsed) && parsed.Unit == "bytes" && parsed.Ranges.Count == 1)
        {
            var r = parsed.Ranges.First();
            if (r.From is { } from)
            {
                start = from;
                end = r.To is { } to ? Math.Min(to, size - 1) : size - 1;
            }
            else if (r.To is { } suffix)
            {
                start = Math.Max(0, size - suffix);
                end = size - 1;
            }

            if (start >= size || start > end)
            {
                headers.ContentRange = $"bytes */{size}";
                return Results.StatusCode(StatusCodes.Status416RangeNotSatisfiable);
            }

            partial = true;
        }

        var length = size == 0 ? 0 : end - start + 1;
        response.StatusCode = partial ? StatusCodes.Status206PartialContent : StatusCodes.Status200OK;
        response.ContentType = inline ? mime : "application/octet-stream";
        response.ContentLength = length;
        if (partial)
        {
            headers.ContentRange = $"bytes {start}-{end}/{size}";
        }

        if (HttpMethods.IsHead(request.Method) || length == 0)
        {
            return Results.Empty;
        }

        Stream input;
        try
        {
            input = await file.Context.Provider.OpenReadAsync(entry.Path, start, http.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LocationService.Report(connections, file.Context.Location.Id, ex);
            response.StatusCode = StatusCodes.Status200OK;
            response.ContentLength = null;
            headers.Remove(HeaderNames.ContentRange);
            headers.Remove(HeaderNames.ContentDisposition);
            headers.Remove(HeaderNames.ContentSecurityPolicy);
            return Problem(LocationErrors.Message(ex));
        }

        await using (input)
        {
            try
            {
                var buffer = new byte[81920];
                var left = length;
                while (left > 0)
                {
                    var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), http.RequestAborted);
                    if (read == 0)
                    {
                        break;
                    }

                    await response.Body.WriteAsync(buffer.AsMemory(0, read), http.RequestAborted);
                    left -= read;
                }

                if (left > 0)
                {
                    http.Abort(); // the file got shorter while it was read: the browser must not take it as complete
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LocationErrors.Log?.LogWarning(ex, "A download of location {Id} broke off.", file.Context.Location.Id);
                http.Abort();
            }
        }

        return Results.Empty;
    }

    private static IResult Answer<T>(Outcome<T> outcome, Func<T, object> shape)
        => outcome.NotAllowed ? Results.NotFound() : outcome.Ok ? Results.Json(shape(outcome.Value!), Json) : Problem(outcome.Error);

    private static IResult Problem(string? error) => Results.Json(new { error }, Json, statusCode: StatusCodes.Status422UnprocessableEntity);
}
