namespace MatDrive.Services.Locations;

/// <summary>
/// The one check of every path of a location: a path is normalised (slashes, no leading or trailing one), may not contain <c>..</c> or <c>.</c>, and never leaves the
/// folder of its location. For a local folder the real path is resolved segment by segment (symbolic links and junctions included), so a link that points outside
/// the folder of the location cannot be followed – neither as the entry itself nor as a folder on the way to it.
/// </summary>
public static class PathGuard
{
    private static readonly StringComparison Comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>The internal form: forward slashes, no leading or trailing slash; the top of the location is the empty string.</summary>
    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return string.Join('/', parts);
    }

    /// <summary>A path is allowed when no segment is <c>.</c> or <c>..</c> and nothing in it is a control character.</summary>
    public static bool IsSafe(string normalized)
        => !normalized.Split('/').Any(s => s is "." or "..") && !normalized.Any(char.IsControl);

    /// <summary>The normalised path, or a <see cref="LocationException"/> (InvalidPath) when it would step outside.</summary>
    public static string Check(string? path)
    {
        var normalized = Normalize(path);
        return IsSafe(normalized) ? normalized : throw new LocationException(LocationError.InvalidPath);
    }

    /// <summary>The folder above (empty for an entry at the top), or null for the top itself.</summary>
    public static string? Parent(string? path)
    {
        var normalized = Normalize(path);
        if (normalized.Length == 0)
        {
            return null;
        }

        var cut = normalized.LastIndexOf('/');
        return cut < 0 ? string.Empty : normalized[..cut];
    }

    public static string NameOf(string? path)
    {
        var normalized = Normalize(path);
        return normalized[(normalized.LastIndexOf('/') + 1)..];
    }

    public static string Combine(string? folder, string name)
    {
        var a = Normalize(folder);
        var b = Normalize(name);
        return a.Length == 0 ? b : b.Length == 0 ? a : a + "/" + b;
    }

    /// <summary>The path for people: <c>/photos/2026</c> (the top is <c>/</c>).</summary>
    public static string Display(string? path) => "/" + Normalize(path);

    /// <summary>Whether <paramref name="inner"/> is <paramref name="outer"/> or lies below it (both normalised).</summary>
    public static bool IsSameOrBelow(string inner, string outer)
        => outer.Length == 0 || inner.Equals(outer, StringComparison.Ordinal) || inner.StartsWith(outer + "/", StringComparison.Ordinal);

    // ---- local folders ------------------------------------------------------------------------------------------------------------

    /// <summary>Whether <paramref name="candidate"/> is <paramref name="root"/> or lies below it (full paths of this machine).</summary>
    public static bool IsInside(string root, string candidate)
    {
        var r = Path.TrimEndingDirectorySeparator(root);
        var c = Path.TrimEndingDirectorySeparator(candidate);
        if (c.Equals(r, Comparison))
        {
            return true;
        }

        // a root like "/" or "C:\" keeps its separator: one more would make "//" and refuse everything
        var prefix = r.EndsWith(Path.DirectorySeparatorChar) || r.EndsWith(Path.AltDirectorySeparatorChar) ? r : r + Path.DirectorySeparatorChar;
        return c.StartsWith(prefix, Comparison);
    }

    /// <summary>
    /// The real path of a local path: every segment that is a symbolic link (or a junction) is replaced by its final target; what does not exist yet is kept as
    /// written (a file about to be created). Null when a link is broken or loops.
    /// </summary>
    public static string? RealPath(string fullPath)
    {
        string current;
        string[] segments;
        try
        {
            var full = Path.GetFullPath(fullPath);
            current = Path.GetPathRoot(full) ?? string.Empty;
            segments = full[current.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        var exists = true;
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            if (!exists)
            {
                continue;
            }

            try
            {
                FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
                if (!info.Exists)
                {
                    exists = false;
                    continue;
                }

                if (info.LinkTarget is null)
                {
                    continue;
                }

                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                if (target is null || !target.Exists)
                {
                    return null;
                }

                // the target may itself lie below other links: resolve it as a whole once more
                var resolved = Path.GetFullPath(target.FullName);
                if (!resolved.Equals(current, Comparison))
                {
                    var again = RealPath(resolved);
                    if (again is null)
                    {
                        return null;
                    }

                    current = again;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // a link that loops or cannot be read: never followed
                return null;
            }
        }

        return Path.TrimEndingDirectorySeparator(current);
    }

    /// <summary>
    /// A path of a local location as a full path of this machine, or null when it would leave the folder of the location (by <c>..</c>, an absolute path or a link,
    /// also a link on the way to it) or reach into <paramref name="hidden"/> (the data folder of MatDrive).
    /// </summary>
    public static string? ResolveLocal(string realRoot, string? relative, string? hidden = null)
    {
        string normalized;
        try
        {
            normalized = Check(relative);
        }
        catch (LocationException)
        {
            return null;
        }

        // "/etc" is the folder etc inside the location (Normalize removed the leading slash); what is still rooted ("C:\x", "C:x" on Windows) is refused
        if (normalized.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || Path.IsPathRooted(normalized))
        {
            return null;
        }

        var candidate = normalized.Length == 0 ? realRoot : Path.Combine(realRoot, normalized.Replace('/', Path.DirectorySeparatorChar));
        string full;
        try
        {
            full = Path.GetFullPath(candidate);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        if (!IsInside(realRoot, full))
        {
            return null;
        }

        var real = RealPath(full);
        if (real is null || !IsInside(realRoot, real))
        {
            return null;
        }

        if (hidden is not null && IsInside(hidden, real))
        {
            return null;
        }

        return full;
    }

    // ---- remote servers -----------------------------------------------------------------------------------------------------------

    /// <summary>The folder of a location on a server: a leading slash, no trailing one ("" = the top of the server).</summary>
    public static string RemoteBase(string? root)
    {
        var normalized = Normalize(root);
        return normalized.Length == 0 ? string.Empty : "/" + normalized;
    }

    /// <summary>The full path on the server of a checked relative path.</summary>
    public static string Remote(string remoteBase, string? relative)
    {
        var normalized = Check(relative);
        return normalized.Length == 0 ? (remoteBase.Length == 0 ? "/" : remoteBase) : remoteBase + "/" + normalized;
    }
}
