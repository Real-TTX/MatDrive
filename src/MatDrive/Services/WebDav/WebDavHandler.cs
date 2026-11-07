using System.Xml;
using System.Xml.Linq;
using MatDrive.Data;
using MatDrive.Localization;
using MatDrive.Services.Files;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Features;

namespace MatDrive.Services.WebDav;

/// <summary>
/// The WebDAV server at <c>/dav/</c> (RFC 4918, class 1 and 2): the Windows Explorer ("Map network drive"), the Finder ("Connect to server"), GNOME/KDE, davfs2,
/// rclone and Cyberduck mount the drives of a person and work with them like a disk. Sign-in with HTTP Basic (the address and an app password); no cookies, no
/// anti-forgery token (a browser cannot send these methods across sites without CORS, which is not offered). Every change goes through the business layer
/// (<see cref="NodeService"/>, <see cref="FileService"/>), which checks the rights with <see cref="DriveAccess"/>; the activity it writes is marked <c>webdav</c>.
/// One instance per request.
/// </summary>
public sealed partial class WebDavHandler
{
    /// <summary>Where versions written through WebDAV come from (<see cref="NodeVersion.Source"/>) and the mark of their activity.</summary>
    public const string Source = "webdav";

    /// <summary>The methods of the server (the header <c>Allow</c>).</summary>
    public const string Methods = "OPTIONS, GET, HEAD, PUT, DELETE, PROPFIND, PROPPATCH, MKCOL, COPY, MOVE, LOCK, UNLOCK";

    private const string XmlType = "application/xml; charset=utf-8";

    private readonly HttpContext _http;
    private readonly AppDbContext _db;
    private readonly BlobStore _store;
    private readonly DavLockManager _locks;
    private readonly DavUser _user;
    private readonly IReadOnlyList<string> _segments;
    private readonly CancellationToken _ct;
    private IReadOnlyList<DavIfList> _if = [];

    private WebDavHandler(HttpContext http, AppDbContext db, BlobStore store, DavLockManager locks, DavUser user, IReadOnlyList<string> segments)
    {
        _http = http;
        _db = db;
        _store = store;
        _locks = locks;
        _user = user;
        _segments = segments;
        _ct = http.RequestAborted;
    }

    private HttpRequest Request => _http.Request;

    private HttpResponse Response => _http.Response;

    private Actor Actor => Actor.Of(_user.Id);

    private string PathBase => Request.PathBase.Value ?? string.Empty;

    /// <summary>The endpoint: signs the person in and answers the method.</summary>
    public static async Task HandleAsync(HttpContext http)
    {
        // the status code pages of the site would turn an empty 401 or 404 into an HTML page: WebDAV answers as it is
        if (http.Features.Get<IStatusCodePagesFeature>() is { } pages)
        {
            pages.Enabled = false;
        }

        var method = http.Request.Method.ToUpperInvariant();
        if (method == "OPTIONS")
        {
            // without sign-in: clients ask before they send credentials, and the answer tells nothing about the drives
            WriteOptions(http.Response);
            return;
        }

        var segments = DavPath.Parse(EscapedPath(http), http.Request.PathBase.Value);
        if (segments is null)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var services = http.RequestServices;
        var db = services.GetRequiredService<AppDbContext>();
        var (result, user) = await services.GetRequiredService<DavAuthenticator>()
            .SignInAsync(db, http.Request.Headers.Authorization, http.Connection.RemoteIpAddress?.ToString(), http.RequestAborted);
        if (result != DavSignIn.Ok || user is null)
        {
            await RefuseAsync(http, result == DavSignIn.Throttled);
            return;
        }

        // the audit columns name the person, and every activity of the request is marked as WebDAV
        services.GetRequiredService<CurrentUser>().ActingUserId = user.Id;
        using var source = ActivityLog.From(Source);
        var handler = new WebDavHandler(http, db, services.GetRequiredService<BlobStore>(), services.GetRequiredService<DavLockManager>(), user, segments);
        await handler.RunAsync(method);
    }

    private async Task RunAsync(string method)
    {
        var conditions = DavIfHeader.Parse(Request.Headers["If"].ToString());
        if (conditions is null)
        {
            Status(StatusCodes.Status400BadRequest);
            return;
        }

        _if = conditions;
        switch (method)
        {
            case "GET":
                await GetAsync(head: false);
                break;
            case "HEAD":
                await GetAsync(head: true);
                break;
            case "PROPFIND":
                await PropFindAsync();
                break;
            case "PROPPATCH":
                await PropPatchAsync();
                break;
            case "PUT":
                await PutAsync();
                break;
            case "DELETE":
                await DeleteAsync();
                break;
            case "MKCOL":
                await MkColAsync();
                break;
            case "COPY":
                await CopyMoveAsync(move: false);
                break;
            case "MOVE":
                await CopyMoveAsync(move: true);
                break;
            case "LOCK":
                await LockAsync();
                break;
            case "UNLOCK":
                await UnlockAsync();
                break;
            default:
                Response.Headers.Allow = Methods;
                Status(StatusCodes.Status405MethodNotAllowed);
                break;
        }
    }

    // ---- sign-in, OPTIONS ---------------------------------------------------------------------------------------------------------

    private static void WriteOptions(HttpResponse response)
    {
        response.StatusCode = StatusCodes.Status200OK;
        response.Headers["DAV"] = "1, 2";
        response.Headers["MS-Author-Via"] = "DAV";
        response.Headers.Allow = Methods;
        response.Headers.AcceptRanges = "bytes";
        response.ContentLength = 0;
    }

    /// <summary>401 with the challenge for Basic (UTF-8), or 429 after too many failed attempts.</summary>
    private static async Task RefuseAsync(HttpContext http, bool throttled)
    {
        var response = http.Response;
        if (throttled)
        {
            response.StatusCode = StatusCodes.Status429TooManyRequests;
            response.Headers.RetryAfter = "60";
        }
        else
        {
            response.StatusCode = StatusCodes.Status401Unauthorized;
            response.Headers.WWWAuthenticate = "Basic realm=\"MatDrive\", charset=\"UTF-8\"";
        }

        if (!HttpMethods.IsHead(http.Request.Method))
        {
            response.ContentType = "text/plain; charset=utf-8";
            await response.WriteAsync(Text.T(throttled ? "WebDav.Throttled" : "WebDav.SignIn"), http.RequestAborted);
        }
    }

    /// <summary>The path of the request as the client escaped it (the raw target), so an escaped slash stays inside its name.</summary>
    private static string EscapedPath(HttpContext http)
    {
        var raw = http.Features.Get<IHttpRequestFeature>()?.RawTarget;
        if (!string.IsNullOrEmpty(raw))
        {
            if (raw.StartsWith('/'))
            {
                return raw;
            }

            if (Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            {
                return uri.AbsolutePath;
            }
        }

        return (http.Request.PathBase + http.Request.Path).ToUriComponent();
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------------------

    private void Status(int status) => Response.StatusCode = status;

    private async Task XmlAsync(int status, byte[] body)
    {
        Response.StatusCode = status;
        Response.ContentType = XmlType;
        Response.ContentLength = body.Length;
        await Response.Body.WriteAsync(body, _ct);
    }

    private Task ErrorAsync(int status, string condition, string? href = null) => XmlAsync(status, DavXml.Error(condition, href));

    private Task<DavLookup> ResolveAsync(IReadOnlyList<string> segments) => DavStore.ResolveAsync(_db, _user, segments, _ct);

    private string Href(IReadOnlyList<string> segments, bool collection) => DavPath.Href(Request.PathBase.ToUriComponent(), segments, collection);

    private static string Quote(string sha) => "\"" + sha + "\"";

    /// <summary>Reads an XML body of at most <see cref="DavXml.MaxBody"/>; the status to answer when it cannot be read (0: fine; no body: null).</summary>
    private async Task<(XDocument? Doc, int Error)> ReadXmlAsync()
    {
        if (Request.ContentLength > DavXml.MaxBody)
        {
            return (null, StatusCodes.Status413PayloadTooLarge);
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await Request.Body.ReadAsync(chunk, _ct)) > 0)
        {
            if (buffer.Length + read > DavXml.MaxBody)
            {
                return (null, StatusCodes.Status413PayloadTooLarge);
            }

            buffer.Write(chunk, 0, read);
        }

        try
        {
            return (DavXml.Read(buffer.GetBuffer().AsMemory(0, (int)buffer.Length)), 0);
        }
        catch (XmlException)
        {
            return (null, StatusCodes.Status400BadRequest);
        }
    }

    /// <summary>Whether the request has a body (MKCOL takes none).</summary>
    private async Task<bool> HasBodyAsync()
    {
        if (Request.ContentLength is { } length)
        {
            return length > 0;
        }

        if (Request.Headers.TransferEncoding.Count == 0)
        {
            return false;
        }

        var probe = new byte[1];
        return await Request.Body.ReadAsync(probe, _ct) > 0;
    }

    // ---- conditions and locks -----------------------------------------------------------------------------------------------------

    /// <summary>What the If header is evaluated against for a place that exists.</summary>
    private DavIfState StateOf(DavTarget? target)
        => target?.Node is not null
            ? new DavIfState(target.Item.Sha256 is { } sha ? Quote(sha) : null, _locks.Covering(target.Chain).Select(l => l.Token).ToList())
            : DavIfState.None;

    /// <summary>What the If header is evaluated against for a new name in a folder: the locks of the folder (they cover what is added to it).</summary>
    private DavIfState StateOfNew(DavTarget parent)
        => parent.Node is not null ? new DavIfState(null, _locks.Covering(parent.Chain).Select(l => l.Token).ToList()) : DavIfState.None;

    /// <summary>Answers 412 when the If header does not hold (true: the request may go on).</summary>
    private async Task<bool> PreconditionsAsync(DavIfState own, IReadOnlyList<string>? destination = null, DavIfState? destinationState = null)
    {
        if (_if.Count == 0)
        {
            return true;
        }

        var states = new Dictionary<string, DavIfState>(StringComparer.Ordinal);
        var requestKey = DavPath.Key(_segments);
        var destinationKey = destination is null ? null : DavPath.Key(destination);
        foreach (var tag in _if.Select(l => l.Resource).OfType<string>().Distinct(StringComparer.Ordinal))
        {
            var segments = DavPath.ParseUrl(tag, PathBase, out _);
            if (segments is null)
            {
                states[tag] = DavIfState.None;
                continue;
            }

            var key = DavPath.Key(segments);
            states[tag] = key == requestKey ? own
                : key == destinationKey ? destinationState ?? DavIfState.None
                : StateOf((await ResolveAsync(segments)).Target);
        }

        if (DavIfHeader.Evaluate(_if, resource => resource is null ? own : states[resource], _locks.IsKnown))
        {
            return true;
        }

        Status(StatusCodes.Status412PreconditionFailed);
        return false;
    }

    /// <summary>Answers 423 when something the request changes is locked and the request did not submit that lock (true: the request may go on).</summary>
    private async Task<bool> UnlockedAsync(IEnumerable<IReadOnlyCollection<DavLock>> groups)
    {
        var blocking = DavLockManager.Unsubmitted(groups, DavIfHeader.Tokens(_if), _user.Id);
        if (blocking is null)
        {
            return true;
        }

        await ErrorAsync(StatusCodes.Status423Locked, "lock-token-submitted", blocking.Root);
        return false;
    }

    /// <summary>The locks a change of the content or the properties of a node meets.</summary>
    private List<IReadOnlyCollection<DavLock>> WriteLocks(DavTarget target) => target.Node is null ? [] : [_locks.Covering(target.Chain)];

    /// <summary>The locks adding something to a folder meets (a lock on a folder covers its members).</summary>
    private List<IReadOnlyCollection<DavLock>> AddLocks(DavTarget parent) => parent.Node is null ? [] : [_locks.Covering(parent.Chain)];

    /// <summary>The locks removing a node meets: on the node, on its folder, and on everything below it (each locked node needs its own token).</summary>
    private List<IReadOnlyCollection<DavLock>> RemoveLocks(DavTarget target)
    {
        if (target.Node is null)
        {
            return [];
        }

        var chain = target.Chain;
        var groups = new List<IReadOnlyCollection<DavLock>> { _locks.Covering(chain) };
        if (chain.Length > 1)
        {
            groups.Add(_locks.Covering(chain[..^1]));
        }

        groups.AddRange(_locks.Below(chain).GroupBy(l => l.NodeId).Select(g => (IReadOnlyCollection<DavLock>)g.ToList()));
        return groups;
    }
}
