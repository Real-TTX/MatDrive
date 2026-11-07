using System.Collections.Concurrent;
using System.Text;

namespace MatDrive.Services;

/// <summary>
/// The icon sprites, which must be in the page for <c>&lt;use href="#..."&gt;</c>: the toolkit's and the app's own on every page, and on the page of an editor the
/// sprite of its ribbon too (<c>img/icons-&lt;app&gt;.svg</c>, e.g. icons-sheets.svg).
/// </summary>
public sealed class Assets
{
    private readonly IWebHostEnvironment _env;
    private readonly ConcurrentDictionary<string, string> _kindSprites = new(StringComparer.Ordinal);

    public Assets(IWebHostEnvironment env)
    {
        _env = env;
        Sprite = Read(env, "matui/matui-icons.svg") + Read(env, "img/app-icons.svg");
        IconNames = System.Text.RegularExpressions.Regex.Matches(Sprite, "id=\"mat-i-([a-z0-9-]+)\"").Select(m => m.Groups[1].Value).ToHashSet();
    }

    public string Sprite { get; }

    public IReadOnlySet<string> IconNames { get; }

    /// <summary>The sprite of the ribbon of one editor (empty when the app has none).</summary>
    public string SpriteOf(string slug) => _kindSprites.GetOrAdd(slug, s => Read(_env, $"img/icons-{s}.svg"));

    private static string Read(IWebHostEnvironment env, string path)
    {
        var file = env.WebRootFileProvider.GetFileInfo(path);
        if (!file.Exists)
        {
            return string.Empty;
        }

        using var reader = new StreamReader(file.CreateReadStream(), Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
