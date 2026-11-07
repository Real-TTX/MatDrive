namespace MatDrive.Services.WebDav;

/// <summary>One condition of an If header: a state token (<c>&lt;opaquelocktoken:…&gt;</c>) or an entity tag (<c>["…"]</c>), possibly negated with <c>Not</c>.</summary>
public sealed record DavCondition(bool Not, string? Token, string? ETag);

/// <summary>A list of conditions that must all hold, for the request resource (<see cref="Resource"/> null) or for the resource of its tag.</summary>
public sealed record DavIfList(string? Resource, IReadOnlyList<DavCondition> Conditions);

/// <summary>What an If header is evaluated against: the entity tag of a resource (null: none, or it does not exist) and the tokens of the locks on it.</summary>
public sealed record DavIfState(string? ETag, IReadOnlyCollection<string> LockTokens)
{
    public static readonly DavIfState None = new(null, []);
}

/// <summary>
/// The If header of WebDAV (RFC 4918, 10.4): lists of conditions on lock tokens and entity tags, untagged (the request resource) or tagged with the URL of a
/// resource. The header holds when at least one list holds. It also carries the lock tokens a client submits to change a locked resource.
/// </summary>
public static class DavIfHeader
{
    /// <summary>The state token that matches no lock (used as <c>(Not &lt;DAV:no-lock&gt;)</c>).</summary>
    public const string NoLock = "DAV:no-lock";

    /// <summary>The lists of an If header; empty without a header, null when it is malformed (the request answers 400).</summary>
    public static IReadOnlyList<DavIfList>? Parse(string? header)
    {
        var lists = new List<DavIfList>();
        if (string.IsNullOrWhiteSpace(header))
        {
            return lists;
        }

        string? tag = null;
        var i = 0;
        while (true)
        {
            SkipSpace(header, ref i);
            if (i >= header.Length)
            {
                return lists;
            }

            if (header[i] == '<')
            {
                // a resource tag: the lists after it are about that resource
                var end = header.IndexOf('>', i + 1);
                if (end < 0)
                {
                    return null;
                }

                tag = header[(i + 1)..end].Trim();
                i = end + 1;
                SkipSpace(header, ref i);
                if (i >= header.Length || header[i] != '(')
                {
                    return null;
                }

                continue;
            }

            if (header[i] != '(')
            {
                return null;
            }

            i++;
            var conditions = new List<DavCondition>();
            while (true)
            {
                SkipSpace(header, ref i);
                if (i >= header.Length)
                {
                    return null;
                }

                if (header[i] == ')')
                {
                    i++;
                    break;
                }

                var not = false;
                if (string.Compare(header, i, "Not", 0, 3, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    not = true;
                    i += 3;
                    SkipSpace(header, ref i);
                    if (i >= header.Length)
                    {
                        return null;
                    }
                }

                var close = header[i] switch
                {
                    '<' => '>',
                    '[' => ']',
                    _ => '\0',
                };
                var stop = close == '\0' ? -1 : header.IndexOf(close, i + 1);
                if (stop < 0)
                {
                    return null;
                }

                var value = header[(i + 1)..stop].Trim();
                conditions.Add(close == '>' ? new DavCondition(not, value, null) : new DavCondition(not, null, value));
                i = stop + 1;
            }

            if (conditions.Count == 0)
            {
                return null;
            }

            lists.Add(new DavIfList(tag, conditions));
        }
    }

    /// <summary>The lock tokens a request submits: every token of the header that is not negated.</summary>
    public static IReadOnlySet<string> Tokens(IReadOnlyList<DavIfList> lists)
        => lists.SelectMany(l => l.Conditions).Where(c => !c.Not && c.Token is not null && c.Token != NoLock).Select(c => c.Token!).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Whether the header holds (no header holds). A token holds when it is a lock on the resource. A token this server does not know at all (<paramref name="isKnown"/>
    /// false: its lock expired, or the locks were lost with a restart) holds as long as nobody else locked the resource meanwhile, so a client that kept a document
    /// open across a restart can still save it; an entity tag still has to match.
    /// </summary>
    public static bool Evaluate(IReadOnlyList<DavIfList> lists, Func<string?, DavIfState> stateOf, Func<string, bool> isKnown)
    {
        if (lists.Count == 0)
        {
            return true;
        }

        foreach (var list in lists)
        {
            var state = stateOf(list.Resource);
            if (list.Conditions.All(c => Holds(c, state, isKnown) != c.Not))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Two entity tags are the same (weak or strong, with or without quotes).</summary>
    public static bool SameTag(string a, string b) => Bare(a) == Bare(b);

    private static bool Holds(DavCondition condition, DavIfState state, Func<string, bool> isKnown)
    {
        if (condition.Token is { } token)
        {
            if (token == NoLock)
            {
                return false;
            }

            return state.LockTokens.Contains(token) || (state.LockTokens.Count == 0 && !isKnown(token));
        }

        return state.ETag is { } etag && condition.ETag is { } wanted && SameTag(etag, wanted);
    }

    private static string Bare(string tag)
    {
        var t = tag.Trim();
        if (t.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            t = t[2..];
        }

        return t.Trim('"');
    }

    private static void SkipSpace(string text, ref int i)
    {
        while (i < text.Length && char.IsWhiteSpace(text[i]))
        {
            i++;
        }
    }
}
