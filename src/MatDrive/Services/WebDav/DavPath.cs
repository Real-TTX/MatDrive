using System.Text;

namespace MatDrive.Services.WebDav;

/// <summary>
/// The addresses of the WebDAV namespace below <c>/dav/</c>. A request path is parsed from the raw (still escaped) request target one segment at a time, so an
/// escaped slash inside a name never becomes a separator; empty segments are skipped, dot segments, slashes and control characters are refused. Hrefs are
/// escaped per segment, so names with spaces, umlauts and emoji round-trip.
/// </summary>
public static class DavPath
{
    /// <summary>The first segment of every WebDAV address.</summary>
    public const string Root = "dav";

    /// <summary>The deepest path that is followed (a deeper one is not found).</summary>
    public const int MaxDepth = 128;

    private enum Parsed
    {
        Ok,
        NotDav,
        Invalid,
    }

    /// <summary>
    /// The decoded segments below <c>/dav</c> of an escaped absolute path (<c>/dav/My%20Drive/a.txt</c> → <c>My Drive</c>, <c>a.txt</c>), or null when the path is
    /// not below <c>/dav</c> (after <paramref name="pathBase"/>, the decoded base path of the application) or contains a segment that is not allowed.
    /// The segments are kept as the client sent them (normalise them with <see cref="Nfc"/> to look names up).
    /// </summary>
    public static IReadOnlyList<string>? Parse(string? escapedPath, string? pathBase = null)
        => ParseCore(escapedPath, pathBase, out var segments) == Parsed.Ok ? segments : null;

    /// <summary>
    /// The segments of a URL in a header (<c>Destination</c>, the resource tag of an <c>If</c> header): an absolute URL or an absolute path. Null when it is
    /// missing or malformed; <paramref name="foreign"/> is set when it is well-formed but not below <c>/dav</c> (another server or another part of the site).
    /// The host is not compared: behind a reverse proxy the client and the app see different names for the same server.
    /// </summary>
    public static IReadOnlyList<string>? ParseUrl(string? value, string? pathBase, out bool foreign)
    {
        foreign = false;
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        string path;
        if (text.StartsWith('/') && !text.StartsWith("//", StringComparison.Ordinal))
        {
            path = text;
        }
        else if (Uri.TryCreate(text.Replace(" ", "%20", StringComparison.Ordinal), UriKind.Absolute, out var uri)
                 && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            path = uri.AbsolutePath;
        }
        else
        {
            return null;
        }

        var parsed = ParseCore(path, pathBase, out var segments);
        foreign = parsed == Parsed.NotDav;
        return parsed == Parsed.Ok ? segments : null;
    }

    /// <summary>The escaped href of a place: <c>/dav/My%20Drive/Reports/</c> (a collection ends with a slash).</summary>
    public static string Href(string escapedPathBase, IEnumerable<string> segments, bool collection)
    {
        var builder = new StringBuilder(escapedPathBase.TrimEnd('/')).Append('/').Append(Root).Append('/');
        var any = false;
        foreach (var segment in segments)
        {
            if (any)
            {
                builder.Append('/');
            }

            builder.Append(Escape(segment));
            any = true;
        }

        if (collection && any)
        {
            builder.Append('/');
        }

        return builder.ToString();
    }

    /// <summary>One segment for an href: everything but the unreserved characters of RFC 3986 percent-encoded as UTF-8.</summary>
    public static string Escape(string segment) => Uri.EscapeDataString(DavText.Clean(segment));

    /// <summary>A form of a path for comparisons: names in NFC and without regard to case (names are unique per folder without regard to case).</summary>
    public static string Key(IEnumerable<string> segments) => string.Join('/', segments.Select(s => Nfc(s).ToLowerInvariant()));

    /// <summary>A name in Unicode NFC (macOS sends umlauts decomposed; the names in the database are composed).</summary>
    public static string Nfc(string text)
    {
        try
        {
            return text.IsNormalized(NormalizationForm.FormC) ? text : text.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            return text;
        }
    }

    private static Parsed ParseCore(string? escapedPath, string? pathBase, out List<string> segments)
    {
        segments = [];
        if (string.IsNullOrEmpty(escapedPath) || escapedPath[0] != '/')
        {
            return Parsed.Invalid;
        }

        var end = escapedPath.IndexOfAny(['?', '#']);
        if (end >= 0)
        {
            escapedPath = escapedPath[..end];
        }

        var raw = escapedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var next = 0;
        foreach (var part in (pathBase ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (next >= raw.Length || !string.Equals(Uri.UnescapeDataString(raw[next]), part, StringComparison.OrdinalIgnoreCase))
            {
                return Parsed.NotDav;
            }

            next++;
        }

        if (next >= raw.Length || !string.Equals(Uri.UnescapeDataString(raw[next]), Root, StringComparison.OrdinalIgnoreCase))
        {
            return Parsed.NotDav;
        }

        next++;
        if (raw.Length - next > MaxDepth)
        {
            return Parsed.Invalid;
        }

        for (var i = next; i < raw.Length; i++)
        {
            var segment = Uri.UnescapeDataString(raw[i]);
            if (segment is "." or ".." || segment.Any(c => c is '/' or '\\' || char.IsControl(c)))
            {
                segments = [];
                return Parsed.Invalid;
            }

            segments.Add(segment);
        }

        return Parsed.Ok;
    }
}

/// <summary>Text for XML and URLs: characters XML cannot carry (and lone surrogates) become U+FFFD.</summary>
public static class DavText
{
    public static string Clean(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (!IsValid(text, i, out var pair))
            {
                return Replace(text);
            }

            if (pair)
            {
                i++;
            }
        }

        return text;
    }

    private static string Replace(string text)
    {
        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (!IsValid(text, i, out var pair))
            {
                builder.Append('�');
                continue;
            }

            builder.Append(text[i]);
            if (pair)
            {
                builder.Append(text[++i]);
            }
        }

        return builder.ToString();
    }

    private static bool IsValid(string text, int i, out bool pair)
    {
        pair = false;
        var c = text[i];
        if (char.IsHighSurrogate(c))
        {
            pair = i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]);
            return pair;
        }

        if (char.IsLowSurrogate(c))
        {
            return false;
        }

        return c is '\t' or '\n' or '\r' || (c >= 0x20 && c <= 0xD7FF) || (c >= 0xE000 && c <= 0xFFFD);
    }
}
