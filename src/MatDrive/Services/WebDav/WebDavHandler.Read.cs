using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using MatDrive.Localization;
using MatDrive.Services.Files;
using Microsoft.Net.Http.Headers;

namespace MatDrive.Services.WebDav;

/// <summary>Reading: GET and HEAD (files with ranges and their hash as the ETag; folders as a small HTML list), PROPFIND, PROPPATCH.</summary>
public sealed partial class WebDavHandler
{
    private async Task GetAsync(bool head)
    {
        var lookup = await ResolveAsync(_segments);
        if (lookup.Target is not { } target)
        {
            Status(StatusCodes.Status404NotFound);
            return;
        }

        if (target.IsCollection)
        {
            await ListingAsync(target, head);
            return;
        }

        // the current version is asked for by its id: WebDAV clients read files all the time (previews, sync), which is not a visit for "Recent"
        var node = target.Node!;
        var file = node.CurrentVersionId is { } version ? await FileService.OpenAsync(_db, _store, Actor, node.Id, version, _ct) : null;
        if (file is null)
        {
            Status(StatusCodes.Status404NotFound);
            return;
        }

        // the bytes of a person never run as a page of this site: a sandbox, and the real type only for what cannot carry script
        var headers = Response.Headers;
        headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        headers.XContentTypeOptions = "nosniff";
        headers.CacheControl = "private, no-cache";
        var type = FileEndpoints.IsSafeInline(file.MimeType) ? file.MimeType : "application/octet-stream";
        var modified = new DateTimeOffset(DateTime.SpecifyKind(node.ModifiedDate, DateTimeKind.Utc));
        await Results.File(file.PhysicalPath, type, null, modified, new EntityTagHeaderValue(Quote(file.ETag)), enableRangeProcessing: true).ExecuteAsync(_http);
    }

    /// <summary>A folder in a browser: a plain list with links (no script, the policy of the site applies).</summary>
    private async Task ListingAsync(DavTarget target, bool head)
    {
        var children = await DavStore.ChildrenAsync(_db, _user, target, _ct);
        var self = Href(_segments, collection: true);
        var parent = _segments.Count > 0 ? Href(_segments.Take(_segments.Count - 1).ToList(), collection: true) : null;
        var title = target.Place == DavPlace.Root ? "MatDrive" : target.Item.Name;
        var bytes = Encoding.UTF8.GetBytes(Listing(title, self, parent, children));
        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "text/html; charset=utf-8";
        Response.Headers.CacheControl = "no-store";
        Response.ContentLength = bytes.Length;
        if (!head)
        {
            await Response.Body.WriteAsync(bytes, _ct);
        }
    }

    private static string Listing(string title, string self, string? parent, IReadOnlyList<DavItem> items)
    {
        var html = HtmlEncoder.Default;
        var b = new StringBuilder(512 + (items.Count * 160));
        b.Append("<!DOCTYPE html><html lang=\"").Append(Languages.Current).Append("\"><head><meta charset=\"utf-8\">")
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><meta name=\"robots\" content=\"noindex\"><title>")
            .Append(html.Encode(Text.T("WebDav.Listing.Title", title))).Append("</title></head><body><h1>").Append(html.Encode(title)).Append("</h1><p>")
            .Append(html.Encode(Text.T("WebDav.Listing.Intro"))).Append("</p><table><thead><tr><th>").Append(html.Encode(Text.T("WebDav.Listing.Name")))
            .Append("</th><th>").Append(html.Encode(Text.T("WebDav.Listing.Size"))).Append("</th><th>").Append(html.Encode(Text.T("WebDav.Listing.Modified")))
            .Append("</th></tr></thead><tbody>");
        if (parent is not null)
        {
            b.Append("<tr><td><a href=\"").Append(html.Encode(parent)).Append("\">").Append(html.Encode(Text.T("WebDav.Listing.Up"))).Append("</a></td><td></td><td></td></tr>");
        }

        foreach (var item in items)
        {
            var href = self + DavPath.Escape(item.Name) + (item.IsCollection ? "/" : string.Empty);
            b.Append("<tr><td><a href=\"").Append(html.Encode(href)).Append("\">").Append(html.Encode(DavText.Clean(item.Name))).Append(item.IsCollection ? "/" : string.Empty)
                .Append("</a></td><td>").Append(item.IsCollection ? string.Empty : html.Encode(Ui.Bytes(item.Size))).Append("</td><td>")
                .Append(item.Modified.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)).Append("</td></tr>");
        }

        if (items.Count == 0)
        {
            b.Append("<tr><td colspan=\"3\">").Append(html.Encode(Text.T("WebDav.Listing.Empty"))).Append("</td></tr>");
        }

        return b.Append("</tbody></table></body></html>").ToString();
    }

    /// <summary>
    /// PROPFIND with Depth 0 or 1 (infinity is refused with <c>propfind-finite-depth</c>; no Depth counts as 1). The children of a folder come from one query and
    /// the answer is streamed in pieces, so a folder of thousands of items stays fast.
    /// </summary>
    private async Task PropFindAsync()
    {
        var depth = Request.Headers["Depth"].ToString().Trim();
        if (depth.Equals("infinity", StringComparison.OrdinalIgnoreCase))
        {
            await ErrorAsync(StatusCodes.Status403Forbidden, "propfind-finite-depth");
            return;
        }

        if (depth is not ("" or "0" or "1"))
        {
            Status(StatusCodes.Status400BadRequest);
            return;
        }

        var (doc, error) = await ReadXmlAsync();
        if (error != 0)
        {
            Status(error);
            return;
        }

        var find = DavXml.PropFind(doc);
        if (find is null)
        {
            Status(StatusCodes.Status400BadRequest);
            return;
        }

        var lookup = await ResolveAsync(_segments);
        if (lookup.Target is not { } target)
        {
            Status(StatusCodes.Status404NotFound);
            return;
        }

        var children = depth != "0" && target.IsCollection ? await DavStore.ChildrenAsync(_db, _user, target, _ct) : [];
        var quota = DavProperties.WantsQuota(find) ? await DavStore.QuotaAsync(_db, _store, _user, target, _ct) : null;
        var childQuota = target.Place is DavPlace.DriveRoot or DavPlace.Node ? quota : null;
        var locks = _locks.All();
        var now = _locks.Now;

        Response.StatusCode = StatusCodes.Status207MultiStatus;
        Response.ContentType = XmlType;
        await using var answer = new DavMultiStatus(Response.Body);
        var self = Href(_segments, target.IsCollection);
        DavProperties.WriteResponse(answer.Writer, self, new DavPropContext(target.Item, LocksOf(target.Item, locks), quota, now), find);
        foreach (var child in children)
        {
            var href = self + DavPath.Escape(child.Name) + (child.IsCollection ? "/" : string.Empty);
            DavProperties.WriteResponse(answer.Writer, href, new DavPropContext(child, LocksOf(child, locks), childQuota, now), find);
            await answer.FlushAsync(_ct);
        }

        await answer.CompleteAsync(_ct);
    }

    private static IReadOnlyList<DavLock> LocksOf(DavItem item, IReadOnlyList<DavLock> all)
    {
        if (all.Count == 0 || item.NodePath is null)
        {
            return [];
        }

        var chain = item.Chain;
        return all.Where(l => l.Covers(chain)).ToList();
    }

    /// <summary>
    /// PROPPATCH: answered, not stored. The Windows Explorer sets its Win32 times after every upload and only needs to hear that it worked; the properties of
    /// the server (namespace <c>DAV:</c>) cannot be set (403, the rest of the request 424, as it is all or nothing).
    /// </summary>
    private async Task PropPatchAsync()
    {
        var (doc, error) = await ReadXmlAsync();
        if (error != 0)
        {
            Status(error);
            return;
        }

        var names = DavXml.PropPatch(doc);
        if (names is null)
        {
            Status(StatusCodes.Status400BadRequest);
            return;
        }

        var lookup = await ResolveAsync(_segments);
        if (lookup.Target is not { } target)
        {
            Status(StatusCodes.Status404NotFound);
            return;
        }

        if (!target.CanEdit)
        {
            Status(StatusCodes.Status403Forbidden);
            return;
        }

        if (!await PreconditionsAsync(StateOf(target)) || !await UnlockedAsync(WriteLocks(target)))
        {
            return;
        }

        var refused = names.Where(n => n.Namespace == DavXml.Dav).ToHashSet();
        var results = names.Select(n => (n, refused.Count == 0 ? StatusCodes.Status200OK : refused.Contains(n) ? StatusCodes.Status403Forbidden : StatusCodes.Status424FailedDependency)).ToList();
        await XmlAsync(StatusCodes.Status207MultiStatus, DavXml.PropPatchAnswer(Href(_segments, target.IsCollection), results));
    }
}
