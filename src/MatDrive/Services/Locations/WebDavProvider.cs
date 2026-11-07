using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using MatDrive.Data;

namespace MatDrive.Services.Locations;

/// <summary>
/// A WebDAV share (Nextcloud, ownCloud, a NAS, Apache, IIS): PROPFIND, GET (with a range), PUT (streamed), MKCOL, MOVE, COPY, DELETE. Redirects are followed by
/// hand (HttpClient drops the authorization on a redirect; Apache sends one for a folder without its slash). Every call has a timeout, except the bytes of a
/// download or an upload, which may take as long as they take.
/// </summary>
public sealed class WebDavProvider : ILocationProvider
{
    private static readonly XNamespace Dav = "DAV:";
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly Uri _base;
    private readonly AuthenticationHeaderValue? _auth;

    public WebDavProvider(LocationSettings settings, HttpClient http)
    {
        _http = http;
        var url = (settings.Url ?? string.Empty).Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https"))
        {
            throw new LocationException(LocationError.Unreachable, "The address of the share is not valid.");
        }

        _base = new Uri(parsed.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/");
        if (!string.IsNullOrEmpty(settings.Username))
        {
            _auth = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.Username}:{settings.Password}")));
        }
    }

    public LocationKind Kind => LocationKind.WebDav;

    public bool HasPermissions => false;

    public async Task CheckAsync(CancellationToken ct)
    {
        using var response = await SendAsync(new HttpMethod("PROPFIND"), _base, "0", PropfindBody, ct);
        EnsureSuccess(response);
    }

    public async Task<IReadOnlyList<LocationEntry>> ListAsync(string path, CancellationToken ct)
    {
        var normalized = PathGuard.Check(path);
        var uri = Resolve(normalized, folder: true);
        using var response = await SendAsync(new HttpMethod("PROPFIND"), uri, "1", PropfindBody, ct);
        EnsureSuccess(response);
        var document = await ReadXmlAsync(response, ct);
        var self = Uri.UnescapeDataString(uri.AbsolutePath).TrimEnd('/');
        var entries = new List<LocationEntry>();
        foreach (var element in document.Descendants(Dav + "response"))
        {
            var href = element.Element(Dav + "href")?.Value;
            if (string.IsNullOrEmpty(href))
            {
                continue;
            }

            var absolute = Uri.UnescapeDataString(new Uri(_base, href).AbsolutePath).TrimEnd('/');
            if (string.Equals(absolute, self, StringComparison.Ordinal))
            {
                continue; // the folder itself
            }

            var name = absolute[(absolute.LastIndexOf('/') + 1)..];
            if (name.Length == 0 || name is "." or "..")
            {
                continue;
            }

            entries.Add(ToEntry(element, name, PathGuard.Combine(normalized, name)));
        }

        return entries;
    }

    public async Task<LocationEntry?> GetAsync(string path, CancellationToken ct)
    {
        var normalized = PathGuard.Check(path);
        using var response = await SendAsync(new HttpMethod("PROPFIND"), Resolve(normalized), "0", PropfindBody, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        EnsureSuccess(response);
        var element = (await ReadXmlAsync(response, ct)).Descendants(Dav + "response").FirstOrDefault();
        return element is null ? null : ToEntry(element, PathGuard.NameOf(normalized), normalized);
    }

    public async Task<Stream> OpenReadAsync(string path, long offset, CancellationToken ct)
    {
        var normalized = PathGuard.Check(path);
        var response = await SendAsync(HttpMethod.Get, Resolve(normalized), null, null, ct, offset);
        try
        {
            EnsureSuccess(response);
            var stream = await response.Content.ReadAsStreamAsync(ct);
            if (offset > 0 && response.StatusCode != HttpStatusCode.PartialContent)
            {
                // the server ignored the range: skip to the offset
                await SkipAsync(stream, offset, ct);
            }

            return new HoldingStream(stream, response);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    public async Task WriteAsync(string path, Stream content, long? length, bool overwrite, CancellationToken ct)
    {
        var normalized = PathGuard.Check(path);
        var existing = await GetAsync(normalized, ct);
        if (existing is not null && (existing.IsFolder || !overwrite))
        {
            throw new LocationException(LocationError.Conflict);
        }

        await EnsureParentAsync(normalized, ct);
        using var request = new HttpRequestMessage(HttpMethod.Put, Resolve(normalized));
        request.Headers.Authorization = _auth;
        request.Content = new StreamContent(content, 81920);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        if (length is { } known)
        {
            request.Content.Headers.ContentLength = known;
        }

        if (!overwrite)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", "*");
        }

        using var response = await Call(() => _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct), ct);
        EnsureSuccess(response);
    }

    public async Task CreateFolderAsync(string path, CancellationToken ct)
    {
        var normalized = PathGuard.Check(path);
        if (await GetAsync(normalized, ct) is not null)
        {
            throw new LocationException(LocationError.Conflict);
        }

        await EnsureParentAsync(normalized, ct);
        using var response = await SendAsync(new HttpMethod("MKCOL"), Resolve(normalized, folder: true), null, null, ct);
        EnsureSuccess(response);
    }

    public async Task MoveAsync(string from, string to, bool overwrite, CancellationToken ct)
    {
        var source = PathGuard.Check(from);
        var target = PathGuard.Check(to);
        if (source.Length == 0)
        {
            throw new LocationException(LocationError.InvalidPath);
        }

        var entry = await GetAsync(source, ct) ?? throw new LocationException(LocationError.NotFound);
        await EnsureParentAsync(target, ct);
        await TransferAsync("MOVE", source, target, entry.IsFolder, overwrite, ct);
    }

    public async Task<bool> TryCopyAsync(string from, string to, CancellationToken ct)
    {
        var source = PathGuard.Check(from);
        var target = PathGuard.Check(to);
        var entry = await GetAsync(source, ct) ?? throw new LocationException(LocationError.NotFound);
        if (await GetAsync(target, ct) is not null)
        {
            throw new LocationException(LocationError.Conflict);
        }

        await EnsureParentAsync(target, ct);
        await TransferAsync("COPY", source, target, entry.IsFolder, overwrite: false, ct);
        return true;
    }

    public async Task DeleteAsync(string path, CancellationToken ct)
    {
        var normalized = PathGuard.Check(path);
        if (normalized.Length == 0)
        {
            throw new LocationException(LocationError.InvalidPath);
        }

        var entry = await GetAsync(normalized, ct) ?? throw new LocationException(LocationError.NotFound);
        using var response = await SendAsync(HttpMethod.Delete, Resolve(normalized, entry.IsFolder), null, null, ct);
        EnsureSuccess(response);
    }

    /// <summary>The quota of RFC 4331 (Nextcloud and others tell it; Apache does not).</summary>
    public async Task<LocationSpace?> SpaceAsync(CancellationToken ct)
    {
        using var response = await SendAsync(new HttpMethod("PROPFIND"), _base, "0", QuotaBody, ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var prop = (await ReadXmlAsync(response, ct)).Descendants(Dav + "prop").FirstOrDefault();
        if (!long.TryParse(prop?.Element(Dav + "quota-available-bytes")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var free) || free < 0)
        {
            return null;
        }

        long.TryParse(prop?.Element(Dav + "quota-used-bytes")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var used);
        return new LocationSpace(Math.Max(0, used) + free, free);
    }

    // ---- http ---------------------------------------------------------------------------------------------------------------------

    private async Task TransferAsync(string method, string source, string target, bool folder, bool overwrite, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), Resolve(source, folder));
        request.Headers.Authorization = _auth;
        request.Headers.TryAddWithoutValidation("Destination", Resolve(target, folder).AbsoluteUri);
        request.Headers.TryAddWithoutValidation("Overwrite", overwrite ? "T" : "F");
        if (folder)
        {
            request.Headers.TryAddWithoutValidation("Depth", "infinity");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        using var response = await Call(() => _http.SendAsync(request, timeout.Token), ct);
        EnsureSuccess(response);
    }

    /// <summary>A call with a timeout for the answer (not for reading a body afterwards); redirects are followed with the authorization kept.</summary>
    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri uri, string? depth, Func<HttpContent>? content, CancellationToken ct, long offset = 0)
    {
        var target = uri;
        for (var hop = 0; hop < 4; hop++)
        {
            using var request = new HttpRequestMessage(method, target);
            request.Headers.Authorization = _auth;
            if (depth is not null)
            {
                request.Headers.TryAddWithoutValidation("Depth", depth);
            }

            if (offset > 0)
            {
                request.Headers.Range = new RangeHeaderValue(offset, null);
            }

            if (content is not null)
            {
                request.Content = content();
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(CallTimeout);
            var response = await Call(() => _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token), ct);
            if ((int)response.StatusCode is 301 or 302 or 307 or 308 && response.Headers.Location is { } location)
            {
                var next = new Uri(target, location);
                response.Dispose();

                // only within the same server: a share does not send the password elsewhere
                if (!string.Equals(next.Authority, _base.Authority, StringComparison.OrdinalIgnoreCase))
                {
                    throw new LocationException(LocationError.Remote, "The server sent a redirect to another host.");
                }

                target = next;
                continue;
            }

            return response;
        }

        throw new LocationException(LocationError.Remote, "Too many redirects.");
    }

    private static async Task<HttpResponseMessage> Call(Func<Task<HttpResponseMessage>> send, CancellationToken ct)
    {
        try
        {
            return await send();
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new LocationException(LocationError.Timeout, null, ex);
        }
        catch (HttpRequestException ex)
        {
            throw new LocationException(LocationError.Unreachable, ex.InnerException?.Message ?? ex.Message, ex);
        }
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        throw response.StatusCode switch
        {
            HttpStatusCode.NotFound or HttpStatusCode.Gone => new LocationException(LocationError.NotFound),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new LocationException(LocationError.AccessDenied, $"{(int)response.StatusCode} {response.ReasonPhrase}"),
            HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed or HttpStatusCode.MethodNotAllowed => new LocationException(LocationError.Conflict),
            HttpStatusCode.Locked => new LocationException(LocationError.AccessDenied, "423 Locked"),
            HttpStatusCode.InsufficientStorage or HttpStatusCode.RequestEntityTooLarge => new LocationException(LocationError.TooLarge, $"{(int)response.StatusCode} {response.ReasonPhrase}"),
            _ => new LocationException(LocationError.Remote, $"{(int)response.StatusCode} {response.ReasonPhrase}"),
        };
    }

    private Uri Resolve(string normalized, bool folder = false)
    {
        var encoded = string.Join('/', normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
        return new Uri(_base, encoded + (folder && encoded.Length > 0 ? "/" : string.Empty));
    }

    private async Task EnsureParentAsync(string normalized, CancellationToken ct)
    {
        var parent = PathGuard.Parent(normalized);
        if (string.IsNullOrEmpty(parent))
        {
            return;
        }

        var current = string.Empty;
        foreach (var segment in parent.Split('/'))
        {
            current = PathGuard.Combine(current, segment);
            if (await GetAsync(current, ct) is not null)
            {
                continue;
            }

            using var response = await SendAsync(new HttpMethod("MKCOL"), Resolve(current, folder: true), null, null, ct);
            if (!response.IsSuccessStatusCode && await GetAsync(current, ct) is { IsFolder: true })
            {
                continue; // made at the same moment by another upload into the same new folder (405)
            }

            EnsureSuccess(response);
        }
    }

    private static async Task<XDocument> ReadXmlAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            return await XDocument.LoadAsync(stream, LoadOptions.None, ct);
        }
        catch (XmlException ex)
        {
            throw new LocationException(LocationError.Remote, "The answer of the server is not WebDAV.", ex);
        }
    }

    private static async Task SkipAsync(Stream stream, long count, CancellationToken ct)
    {
        var buffer = new byte[81920];
        while (count > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, count)), ct);
            if (read == 0)
            {
                return;
            }

            count -= read;
        }
    }

    private static StringContent PropfindBody() => new(
        """<?xml version="1.0" encoding="utf-8"?><D:propfind xmlns:D="DAV:"><D:prop><D:resourcetype/><D:getcontentlength/><D:getlastmodified/></D:prop></D:propfind>""",
        Encoding.UTF8, "application/xml");

    private static StringContent QuotaBody() => new(
        """<?xml version="1.0" encoding="utf-8"?><D:propfind xmlns:D="DAV:"><D:prop><D:quota-available-bytes/><D:quota-used-bytes/></D:prop></D:propfind>""",
        Encoding.UTF8, "application/xml");

    private static LocationEntry ToEntry(XElement response, string name, string path)
    {
        // a response may carry several propstats (200 and 404 for unknown properties): the found ones count
        var props = response.Descendants(Dav + "propstat")
            .Where(p => (p.Element(Dav + "status")?.Value ?? "200").Contains(" 200", StringComparison.Ordinal))
            .Select(p => p.Element(Dav + "prop"))
            .Where(p => p is not null)
            .ToList();
        var folder = props.Any(p => p!.Element(Dav + "resourcetype")?.Element(Dav + "collection") is not null);
        long.TryParse(props.Select(p => p!.Element(Dav + "getcontentlength")?.Value).FirstOrDefault(v => v is not null), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size);
        var modified = DateTime.MinValue;
        var text = props.Select(p => p!.Element(Dav + "getlastmodified")?.Value).FirstOrDefault(v => v is not null);
        if (!string.IsNullOrEmpty(text) && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
        {
            modified = parsed;
        }

        return new LocationEntry(name, path, folder, folder ? 0 : Math.Max(0, size), modified, name.StartsWith('.'));
    }
}
