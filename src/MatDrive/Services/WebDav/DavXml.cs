using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using MatDrive.Services.Files;

namespace MatDrive.Services.WebDav;

public enum DavPropMode
{
    /// <summary>All properties (an empty body, or <c>&lt;allprop/&gt;</c> with an optional <c>&lt;include&gt;</c>).</summary>
    AllProp,

    /// <summary>The names of the properties only.</summary>
    PropName,

    /// <summary>The properties that are named.</summary>
    Prop,
}

/// <summary>What a PROPFIND asks for: the mode and the names (of <c>&lt;prop&gt;</c>, or of <c>&lt;include&gt;</c> with allprop).</summary>
public sealed record DavPropFind(DavPropMode Mode, IReadOnlyList<XName> Names)
{
    public static readonly DavPropFind AllProp = new(DavPropMode.AllProp, []);
}

/// <summary>The body of a LOCK request: the scope and the owner (an XML fragment the client wants to see again in the lock discovery).</summary>
public sealed record DavLockInfo(bool Exclusive, XElement? Owner);

/// <summary>The space of the drive of a resource (RFC 4331); <see cref="Available"/> null when it is unknown.</summary>
public sealed record DavQuota(long Used, long? Available);

/// <summary>
/// The XML of WebDAV: reading the bodies of PROPFIND, PROPPATCH and LOCK (bounded, without DTDs or external entities) and the elements of the answers
/// (namespace <c>DAV:</c> with the prefix <c>d</c>, the Microsoft properties in <c>urn:schemas-microsoft-com:</c> with <c>z</c>).
/// </summary>
public static class DavXml
{
    public static readonly XNamespace Dav = "DAV:";

    /// <summary>The namespace of the Win32 properties the Windows Explorer reads and writes.</summary>
    public static readonly XNamespace Ms = "urn:schemas-microsoft-com:";

    /// <summary>The largest XML body that is read (PROPFIND, PROPPATCH, LOCK).</summary>
    public const int MaxBody = 1024 * 1024;

    /// <summary>The longest owner of a lock that is kept (the rest of the lock is kept without it).</summary>
    public const int MaxOwner = 4000;

    public static readonly XmlWriterSettings WriterSettings = new()
    {
        Encoding = new UTF8Encoding(false),
        Indent = false,
        CloseOutput = false,
        CheckCharacters = true,
    };

    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = MaxBody,
        MaxCharactersFromEntities = 1024,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
    };

    /// <summary>A body as XML; null when it is empty; throws <see cref="XmlException"/> when it is not well-formed.</summary>
    public static XDocument? Read(ReadOnlyMemory<byte> body)
    {
        if (body.IsEmpty || body.Span.Trim(" \t\r\n"u8).IsEmpty)
        {
            return null;
        }

        using var stream = new MemoryStream(body.ToArray(), writable: false);
        using var reader = XmlReader.Create(stream, ReaderSettings);
        return XDocument.Load(reader);
    }

    /// <summary>What a PROPFIND body asks for (no body: all properties); null when it is not a propfind.</summary>
    public static DavPropFind? PropFind(XDocument? doc)
    {
        if (doc?.Root is not { } root)
        {
            return DavPropFind.AllProp;
        }

        if (root.Name != Dav + "propfind")
        {
            return null;
        }

        if (root.Element(Dav + "propname") is not null)
        {
            return new DavPropFind(DavPropMode.PropName, []);
        }

        if (root.Element(Dav + "prop") is { } prop)
        {
            return new DavPropFind(DavPropMode.Prop, prop.Elements().Select(e => e.Name).Distinct().ToList());
        }

        var include = root.Element(Dav + "include")?.Elements().Select(e => e.Name).Distinct().ToList() ?? [];
        return new DavPropFind(DavPropMode.AllProp, include);
    }

    /// <summary>The properties a PROPPATCH sets or removes, in their order; null when it is not a propertyupdate.</summary>
    public static IReadOnlyList<XName>? PropPatch(XDocument? doc)
    {
        if (doc?.Root is not { } root || root.Name != Dav + "propertyupdate")
        {
            return null;
        }

        return root.Elements().Where(e => e.Name == Dav + "set" || e.Name == Dav + "remove")
            .SelectMany(e => e.Elements(Dav + "prop")).SelectMany(p => p.Elements()).Select(e => e.Name).Distinct().ToList();
    }

    /// <summary>The scope and owner of a LOCK body; null when it is not a lockinfo for a write lock.</summary>
    public static DavLockInfo? LockInfo(XDocument? doc)
    {
        if (doc?.Root is not { } root || root.Name != Dav + "lockinfo")
        {
            return null;
        }

        var scope = root.Element(Dav + "lockscope");
        var exclusive = scope?.Element(Dav + "exclusive") is not null;
        var shared = scope?.Element(Dav + "shared") is not null;
        if (exclusive == shared || root.Element(Dav + "locktype")?.Element(Dav + "write") is null)
        {
            return null;
        }

        var owner = root.Element(Dav + "owner") is { } o ? new XElement(o) : null;
        if (owner is not null && owner.ToString(SaveOptions.DisableFormatting).Length > MaxOwner)
        {
            owner = null;
        }

        return new DavLockInfo(exclusive, owner);
    }

    /// <summary>A date for <c>getlastmodified</c> and the Win32 times (RFC 1123: <c>Sat, 07 Nov 2026 18:07:00 GMT</c>).</summary>
    public static string Rfc1123(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("R", CultureInfo.InvariantCulture);

    /// <summary>A date for <c>creationdate</c> (RFC 3339: <c>2026-11-07T18:07:00Z</c>).</summary>
    public static string Iso(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>The XML of one lock (<c>activelock</c>), for the lock discovery and the answer of a LOCK.</summary>
    public static void WriteActiveLock(XmlWriter w, DavLock l, DateTimeOffset now)
    {
        w.WriteStartElement("activelock", Dav.NamespaceName);
        w.WriteStartElement("locktype", Dav.NamespaceName);
        Empty(w, Dav + "write");
        w.WriteEndElement();
        w.WriteStartElement("lockscope", Dav.NamespaceName);
        Empty(w, Dav + (l.Exclusive ? "exclusive" : "shared"));
        w.WriteEndElement();
        w.WriteElementString("depth", Dav.NamespaceName, l.Deep ? "infinity" : "0");
        l.Owner?.WriteTo(w);
        var left = Math.Max(0, (long)Math.Ceiling((l.Expires - now).TotalSeconds));
        w.WriteElementString("timeout", Dav.NamespaceName, "Second-" + left.ToString(CultureInfo.InvariantCulture));
        w.WriteStartElement("locktoken", Dav.NamespaceName);
        w.WriteElementString("href", Dav.NamespaceName, l.Token);
        w.WriteEndElement();
        w.WriteStartElement("lockroot", Dav.NamespaceName);
        w.WriteElementString("href", Dav.NamespaceName, l.Root);
        w.WriteEndElement();
        w.WriteEndElement();
    }

    /// <summary>The answer of a LOCK: <c>&lt;d:prop&gt;&lt;d:lockdiscovery&gt;</c> with the lock.</summary>
    public static byte[] LockAnswer(DavLock l, DateTimeOffset now)
    {
        using var buffer = new MemoryStream();
        using (var w = XmlWriter.Create(buffer, WriterSettings))
        {
            w.WriteStartDocument();
            w.WriteStartElement("d", "prop", Dav.NamespaceName);
            w.WriteStartElement("lockdiscovery", Dav.NamespaceName);
            WriteActiveLock(w, l, now);
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteEndDocument();
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// The body of an error with a precondition (RFC 4918, 16): <c>&lt;d:error&gt;&lt;d:lock-token-submitted&gt;&lt;d:href&gt;…</c>.
    /// </summary>
    public static byte[] Error(string condition, string? href = null)
    {
        using var buffer = new MemoryStream();
        using (var w = XmlWriter.Create(buffer, WriterSettings))
        {
            w.WriteStartDocument();
            w.WriteStartElement("d", "error", Dav.NamespaceName);
            w.WriteStartElement(condition, Dav.NamespaceName);
            if (href is not null)
            {
                w.WriteElementString("href", Dav.NamespaceName, href);
            }

            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteEndDocument();
        }

        return buffer.ToArray();
    }

    /// <summary>A PROPPATCH answer: every property with its status.</summary>
    public static byte[] PropPatchAnswer(string href, IReadOnlyList<(XName Name, int Status)> results)
    {
        using var buffer = new MemoryStream();
        using (var w = XmlWriter.Create(buffer, WriterSettings))
        {
            w.WriteStartDocument();
            w.WriteStartElement("d", "multistatus", Dav.NamespaceName);
            w.WriteAttributeString("xmlns", "z", null, Ms.NamespaceName);
            w.WriteStartElement("response", Dav.NamespaceName);
            w.WriteElementString("href", Dav.NamespaceName, href);
            foreach (var group in results.GroupBy(r => r.Status))
            {
                w.WriteStartElement("propstat", Dav.NamespaceName);
                w.WriteStartElement("prop", Dav.NamespaceName);
                foreach (var (name, _) in group)
                {
                    Empty(w, name);
                }

                w.WriteEndElement();
                w.WriteElementString("status", Dav.NamespaceName, StatusLine(group.Key));
                w.WriteEndElement();
            }

            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteEndDocument();
        }

        return buffer.ToArray();
    }

    /// <summary>The status line of a propstat or a response: <c>HTTP/1.1 404 Not Found</c>.</summary>
    public static string StatusLine(int status) => status switch
    {
        200 => "HTTP/1.1 200 OK",
        403 => "HTTP/1.1 403 Forbidden",
        404 => "HTTP/1.1 404 Not Found",
        423 => "HTTP/1.1 423 Locked",
        424 => "HTTP/1.1 424 Failed Dependency",
        _ => "HTTP/1.1 " + status.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>An empty element in its namespace (a namespace of a client gets a prefix declared on the element itself).</summary>
    public static void Empty(XmlWriter w, XName name)
    {
        w.WriteStartElement(name.LocalName, name.NamespaceName);
        w.WriteEndElement();
    }
}

/// <summary>One item of a PROPFIND answer with what its live properties need.</summary>
public sealed record DavPropContext(DavItem Item, IReadOnlyList<DavLock> Locks, DavQuota? Quota, DateTimeOffset Now);

/// <summary>
/// The live properties of the WebDAV namespace: those of RFC 4918 (with the lock discovery), the quota of RFC 4331, and the ones the Windows Explorer asks
/// for (<c>iscollection</c>, <c>ishidden</c>, <c>isreadonly</c>, the Win32 times and attributes). Dead properties are not kept.
/// </summary>
public static class DavProperties
{
    private static readonly XNamespace D = DavXml.Dav;
    private static readonly XNamespace Z = DavXml.Ms;

    public static readonly XName QuotaAvailable = D + "quota-available-bytes";
    public static readonly XName QuotaUsed = D + "quota-used-bytes";

    /// <summary>What <c>allprop</c> answers (RFC 4918: the live properties it defines; not the quota, not the Microsoft ones).</summary>
    private static readonly XName[] AllPropNames =
    [
        D + "creationdate", D + "displayname", D + "getcontentlength", D + "getcontenttype", D + "getetag", D + "getlastmodified", D + "resourcetype",
        D + "supportedlock", D + "lockdiscovery",
    ];

    private static readonly Dictionary<XName, (Func<DavPropContext, bool> Has, Action<XmlWriter, DavPropContext> Write)> Known = new()
    {
        [D + "creationdate"] = (_ => true, (w, c) => w.WriteString(DavXml.Iso(c.Item.Created))),
        [D + "displayname"] = (_ => true, (w, c) => w.WriteString(DavText.Clean(c.Item.Name))),
        [D + "getcontentlength"] = (c => !c.Item.IsCollection, (w, c) => w.WriteString(c.Item.Size.ToString(CultureInfo.InvariantCulture))),
        [D + "getcontenttype"] = (c => !c.Item.IsCollection, (w, c) => w.WriteString(c.Item.MimeType ?? FileTypes.MimeOf(c.Item.Name))),
        [D + "getetag"] = (c => !c.Item.IsCollection && c.Item.Sha256 is not null, (w, c) => w.WriteString("\"" + c.Item.Sha256 + "\"")),
        [D + "getlastmodified"] = (_ => true, (w, c) => w.WriteString(DavXml.Rfc1123(c.Item.Modified))),
        [D + "resourcetype"] = (_ => true, (w, c) =>
        {
            if (c.Item.IsCollection)
            {
                DavXml.Empty(w, D + "collection");
            }
        }),
        [D + "supportedlock"] = (_ => true, (w, c) =>
        {
            if (c.Item.NodeId is null)
            {
                return; // the places above the drives cannot be locked
            }

            foreach (var scope in new[] { "exclusive", "shared" })
            {
                w.WriteStartElement("lockentry", D.NamespaceName);
                w.WriteStartElement("lockscope", D.NamespaceName);
                DavXml.Empty(w, D + scope);
                w.WriteEndElement();
                w.WriteStartElement("locktype", D.NamespaceName);
                DavXml.Empty(w, D + "write");
                w.WriteEndElement();
                w.WriteEndElement();
            }
        }),
        [D + "lockdiscovery"] = (_ => true, (w, c) =>
        {
            foreach (var l in c.Locks)
            {
                DavXml.WriteActiveLock(w, l, c.Now);
            }
        }),
        [QuotaAvailable] = (c => c.Quota?.Available is not null, (w, c) => w.WriteString(c.Quota!.Available!.Value.ToString(CultureInfo.InvariantCulture))),
        [QuotaUsed] = (c => c.Quota is not null, (w, c) => w.WriteString(c.Quota!.Used.ToString(CultureInfo.InvariantCulture))),
        [D + "iscollection"] = (_ => true, (w, c) => w.WriteString(c.Item.IsCollection ? "1" : "0")),
        [D + "ishidden"] = (_ => true, (w, c) => w.WriteString(IsHidden(c.Item) ? "1" : "0")),
        [D + "isreadonly"] = (_ => true, (w, c) => w.WriteString(c.Item.ReadOnly ? "1" : "0")),
        [Z + "Win32CreationTime"] = (_ => true, (w, c) => w.WriteString(DavXml.Rfc1123(c.Item.Created))),
        [Z + "Win32LastAccessTime"] = (_ => true, (w, c) => w.WriteString(DavXml.Rfc1123(c.Item.Modified))),
        [Z + "Win32LastModifiedTime"] = (_ => true, (w, c) => w.WriteString(DavXml.Rfc1123(c.Item.Modified))),
        [Z + "Win32FileAttributes"] = (_ => true, (w, c) => w.WriteString(Attributes(c.Item))),
    };

    /// <summary>Whether a PROPFIND asks for the quota (it costs a query, so it is only looked up then).</summary>
    public static bool WantsQuota(DavPropFind find) => find.Names.Any(n => n == QuotaAvailable || n == QuotaUsed);

    /// <summary>Whether a property is one of the server (protected or computed); the rest are dead properties of the client.</summary>
    public static bool IsLive(XName name) => Known.ContainsKey(name) || name.Namespace == D;

    /// <summary>Writes the <c>response</c> of one item.</summary>
    public static void WriteResponse(XmlWriter w, string href, DavPropContext c, DavPropFind find)
    {
        w.WriteStartElement("response", D.NamespaceName);
        w.WriteElementString("href", D.NamespaceName, href);
        if (find.Mode == DavPropMode.PropName)
        {
            BeginPropStat(w);
            foreach (var (name, entry) in Known)
            {
                if (entry.Has(c))
                {
                    DavXml.Empty(w, name);
                }
            }

            EndPropStat(w, 200);
            w.WriteEndElement();
            return;
        }

        var missing = new List<XName>();
        var open = false;
        IEnumerable<XName> names = find.Mode == DavPropMode.AllProp ? AllPropNames.Concat(find.Names).Distinct() : find.Names;
        foreach (var name in names)
        {
            if (Known.TryGetValue(name, out var entry) && entry.Has(c))
            {
                if (!open)
                {
                    BeginPropStat(w);
                    open = true;
                }

                w.WriteStartElement(name.LocalName, name.NamespaceName);
                entry.Write(w, c);
                w.WriteEndElement();
            }
            else if (find.Mode == DavPropMode.Prop || !AllPropNames.Contains(name))
            {
                missing.Add(name);
            }
        }

        if (open || missing.Count == 0)
        {
            if (!open)
            {
                BeginPropStat(w);
            }

            EndPropStat(w, 200);
        }

        if (missing.Count > 0)
        {
            BeginPropStat(w);
            foreach (var name in missing)
            {
                DavXml.Empty(w, name);
            }

            EndPropStat(w, 404);
        }

        w.WriteEndElement();
    }

    /// <summary>Dot files (<c>.DS_Store</c>, <c>._name</c> of the Finder) are hidden in the Windows Explorer, like Samba does.</summary>
    private static bool IsHidden(DavItem item) => item.Name.StartsWith('.');

    /// <summary>FILE_ATTRIBUTE_*: directory 0x10 or archive 0x20, read-only 0x1, hidden 0x2 (eight hex digits).</summary>
    private static string Attributes(DavItem item)
    {
        var value = item.IsCollection ? 0x10 : 0x20;
        if (item.ReadOnly)
        {
            value |= 0x1;
        }

        if (IsHidden(item))
        {
            value |= 0x2;
        }

        return value.ToString("X8", CultureInfo.InvariantCulture);
    }

    private static void BeginPropStat(XmlWriter w)
    {
        w.WriteStartElement("propstat", D.NamespaceName);
        w.WriteStartElement("prop", D.NamespaceName);
    }

    private static void EndPropStat(XmlWriter w, int status)
    {
        w.WriteEndElement();
        w.WriteElementString("status", D.NamespaceName, DavXml.StatusLine(status));
        w.WriteEndElement();
    }
}

/// <summary>
/// A 207 Multi-Status answer written in pieces: the XML goes into a buffer that is sent to the client whenever it has grown, so a folder of thousands of items
/// never sits in memory as a whole (and the XML writer stays synchronous, which the server does not allow on the body itself).
/// </summary>
public sealed class DavMultiStatus : IAsyncDisposable
{
    private const int Chunk = 64 * 1024;

    private readonly Stream _body;
    private readonly MemoryStream _buffer = new();

    public DavMultiStatus(Stream body)
    {
        _body = body;
        Writer = XmlWriter.Create(_buffer, DavXml.WriterSettings);
        Writer.WriteStartDocument();
        Writer.WriteStartElement("d", "multistatus", DavXml.Dav.NamespaceName);
        Writer.WriteAttributeString("xmlns", "z", null, DavXml.Ms.NamespaceName);
    }

    public XmlWriter Writer { get; }

    /// <summary>Sends what was written when the buffer has grown.</summary>
    public async Task FlushAsync(CancellationToken ct)
    {
        Writer.Flush();
        if (_buffer.Length >= Chunk)
        {
            await SendAsync(ct);
        }
    }

    /// <summary>Closes the document and sends the rest.</summary>
    public async Task CompleteAsync(CancellationToken ct)
    {
        Writer.WriteEndElement();
        Writer.WriteEndDocument();
        Writer.Flush();
        await SendAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        Writer.Dispose();
        await _buffer.DisposeAsync();
    }

    private async Task SendAsync(CancellationToken ct)
    {
        if (_buffer.Length > 0)
        {
            await _body.WriteAsync(_buffer.GetBuffer().AsMemory(0, (int)_buffer.Length), ct);
            _buffer.SetLength(0);
        }
    }
}
