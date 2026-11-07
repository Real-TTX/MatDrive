using System.Diagnostics;
using System.Text;
using MatDrive.Data;
using MatDrive.Localization;
using MatDrive.Services.Files;

namespace MatDrive.Services.Locations;

/// <summary>One message of a running search: progress, a hit, or the end.</summary>
public sealed record LocationSearchEvent(string Type, int Folders = 0, int Files = 0, string? Current = null, LocationSearchHit? Hit = null, int Hits = 0, bool Truncated = false, string? Error = null);

public sealed record LocationSearchHit(string Name, string Path, string Folder, bool IsFolder, long Size, DateTime Modified, string Category, string Preview, bool Text, string? Snippet);

/// <summary>
/// The search through a folder of a location and everything below it: by name, and on request inside text files (the line around the find). It walks the folders
/// breadth first and sends what it finds as it goes (the browser shows the hits, the folder being read and a button to stop; closing the request stops the walk).
/// Limits keep it a look and not a crawl: 500 hits, 20 000 folders, 24 levels, files up to 8 MB for the content, 5 minutes.
/// </summary>
public static class LocationSearch
{
    public const int MaxHits = 500;
    public const int MaxFolders = 20_000;
    public const int MaxDepth = 24;
    public const long MaxContentBytes = 8L * 1024 * 1024;
    public const int MinLength = 2;

    private static readonly TimeSpan MaxTime = TimeSpan.FromMinutes(5);

    /// <summary>Runs the search; null when the person may not see the location (nothing was sent then).</summary>
    public static async Task<bool> RunAsync(AppDbContext db, LocationConnections connections, long userId, long locationId, string? path, string? query, bool content, bool hidden,
        Func<LocationSearchEvent, Task> emit, CancellationToken ct)
    {
        var context = await LocationService.OpenAsync(db, connections, userId, locationId, ct: ct);
        if (context is null)
        {
            return false;
        }

        var needle = (query ?? string.Empty).Trim();
        if (needle.Length < MinLength)
        {
            await emit(new LocationSearchEvent("done", Error: Text.T("Locations.Search.TooShort", MinLength)));
            return true;
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(MaxTime);
        var token = limit.Token;
        var folders = 0;
        var files = 0;
        var hits = 0;
        var truncated = false;
        var clock = Stopwatch.StartNew();
        var lastProgress = TimeSpan.Zero;
        string? error = null;

        var queue = new Queue<(string Path, int Depth)>();
        string start;
        try
        {
            start = PathGuard.Check(path);
        }
        catch (LocationException ex)
        {
            await emit(new LocationSearchEvent("done", Error: LocationErrors.Message(ex)));
            return true;
        }

        queue.Enqueue((start, 0));
        try
        {
            while (queue.Count > 0 && !truncated)
            {
                token.ThrowIfCancellationRequested();
                var (folder, depth) = queue.Dequeue();
                folders++;
                if (clock.Elapsed - lastProgress > TimeSpan.FromMilliseconds(250))
                {
                    lastProgress = clock.Elapsed;
                    await emit(new LocationSearchEvent("progress", folders, files, PathGuard.Display(folder), Hits: hits));
                }

                IReadOnlyList<LocationEntry> entries;
                try
                {
                    entries = await context.Provider.ListAsync(folder, token);
                }
                catch (Exception ex) when (depth > 0 && ex is not OperationCanceledException)
                {
                    continue; // a folder without the right to read it does not end the search; the first one does
                }

                foreach (var entry in entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
                {
                    token.ThrowIfCancellationRequested();
                    if (entry.IsHidden && !hidden)
                    {
                        continue;
                    }

                    var named = entry.Name.Contains(needle, StringComparison.OrdinalIgnoreCase);
                    string? snippet = null;
                    if (entry.IsFolder)
                    {
                        if (depth < MaxDepth && !entry.IsLink)
                        {
                            queue.Enqueue((entry.Path, depth + 1));
                        }
                    }
                    else
                    {
                        files++;
                        if (!named && content && IsTextCandidate(entry))
                        {
                            snippet = await FindAsync(context.Provider, entry, needle, token);
                        }
                    }

                    if (named || snippet is not null)
                    {
                        hits++;
                        await emit(new LocationSearchEvent("hit", folders, files, Hit: ToHit(entry, snippet), Hits: hits));
                        if (hits >= MaxHits)
                        {
                            truncated = true;
                            break;
                        }
                    }
                }

                if (folders >= MaxFolders)
                {
                    truncated = queue.Count > 0;
                    break;
                }
            }

            connections.Seen(locationId, true);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            truncated = true; // the time ran out: what was found stays
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LocationService.Report(connections, locationId, ex);
            error = LocationErrors.Message(ex);
        }

        if (!ct.IsCancellationRequested)
        {
            await emit(new LocationSearchEvent("done", folders, files, Hits: hits, Truncated: truncated, Error: error));
        }

        return true;
    }

    private static LocationSearchHit ToHit(LocationEntry entry, string? snippet)
    {
        var category = entry.IsFolder ? FileTypes.Folder : FileTypes.CategoryOf(entry.Name);
        return new LocationSearchHit(entry.Name, PathGuard.Display(entry.Path), PathGuard.Display(PathGuard.Parent(entry.Path) ?? string.Empty), entry.IsFolder, entry.Size,
            entry.Modified, category, entry.IsFolder ? "none" : FileTypes.PreviewOf(entry.Name, entry.Size), !entry.IsFolder && FileTypes.IsEditableText(entry.Name, entry.Size), snippet);
    }

    /// <summary>Text and code, and files of no known kind (tried, and dropped at the first zero byte); pictures, films, music, archives and documents are left out.</summary>
    private static bool IsTextCandidate(LocationEntry entry)
        => entry.Size is > 0 and <= MaxContentBytes
           && (FileTypes.CategoryOf(entry.Name) is "text" or "code" or "other" || Path.GetExtension(entry.Name).ToLowerInvariant() is ".csv" or ".tsv" or ".svg");

    /// <summary>Looks for the text in a file and answers the line around the first find (null: not there, or not text).</summary>
    private static async Task<string?> FindAsync(ILocationProvider provider, LocationEntry entry, string needle, CancellationToken ct)
    {
        try
        {
            await using var stream = await provider.OpenReadAsync(entry.Path, 0, ct);
            var decoder = Encoding.UTF8.GetDecoder();
            var bytes = new byte[64 * 1024];
            var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
            var overlap = Math.Max(0, needle.Length - 1);
            var tail = string.Empty;
            long total = 0;
            var first = true;
            while (true)
            {
                var count = await stream.ReadAsync(bytes, ct);
                if (count == 0)
                {
                    return null;
                }

                if (first)
                {
                    first = false;
                    if (Array.IndexOf(bytes, (byte)0, 0, count) >= 0)
                    {
                        return null; // a binary file
                    }
                }

                var written = decoder.GetChars(bytes, 0, count, chars, 0);
                var window = tail + new string(chars, 0, written);
                var index = window.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
                if (index >= 0)
                {
                    return Snippet(window, index, needle.Length);
                }

                total += count;
                if (total >= MaxContentBytes)
                {
                    return null;
                }

                tail = overlap == 0 ? string.Empty : window[^Math.Min(overlap, window.Length)..];
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null; // a file that cannot be read is not a hit
        }
    }

    /// <summary>The line around the find; of a very long line only a part around it.</summary>
    private static string Snippet(string text, int index, int length)
    {
        var start = text.LastIndexOfAny(['\n', '\r'], Math.Max(0, index - 1)) + 1;
        var end = text.IndexOfAny(['\n', '\r'], Math.Min(text.Length - 1, index + length));
        if (end < 0)
        {
            end = text.Length;
        }

        var line = text[start..end];
        var relative = index - start;
        if (line.Length > 200)
        {
            var from = Math.Max(0, relative - 60);
            var take = Math.Min(200, line.Length - from);
            line = (from > 0 ? "…" : string.Empty) + line.Substring(from, take) + (from + take < line.Length ? "…" : string.Empty);
        }

        return line.Trim();
    }
}
