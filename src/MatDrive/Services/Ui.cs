using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Html;

namespace MatDrive.Services;

/// <summary>Small helpers for the markup of the toolkit: icons, the logo, initials, sizes.</summary>
public static partial class Ui
{
    public const string AppId = "matdrive";
    public const string AppName = "MatDrive";

    /// <summary>The accent: the orange of the office suites (white text on it 4.6:1). The four apps have colours of their own (<c>IKindHandler.Colour</c>).</summary>
    public const string Accent = "#d83b01";

    [GeneratedRegex("^[a-z0-9-]+$")]
    private static partial Regex NameRegex();

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex ColourRegex();

    /// <summary>An icon from the page's inline sprite (the toolkit's and the app's own).</summary>
    public static IHtmlContent Icon(string name, string? extraClass = null, string? colour = null)
    {
        if (!NameRegex().IsMatch(name))
        {
            throw new ArgumentException($"'{name}' is not an icon name.", nameof(name));
        }

        var cls = string.IsNullOrEmpty(extraClass) ? "mat-icon" : $"mat-icon {extraClass}";
        var data = IsColour(colour) ? $" data-colour=\"{colour}\"" : string.Empty;
        return new HtmlString($"<svg class=\"{cls}\"{data} aria-hidden=\"true\" focusable=\"false\"><use href=\"#mat-i-{name}\"></use></svg>");
    }

    public static bool IsColour(string? value) => value is not null && ColourRegex().IsMatch(value);

    /// <summary>The logo of the menu and the sign-in: the mark of MatDrive.</summary>
    public static IHtmlContent Logo(string size = "md")
        => new HtmlString($"<span class=\"mat-logo mat-logo--{size} md-logo\" role=\"img\" aria-label=\"{AppName}\"><svg class=\"mat-icon\" aria-hidden=\"true\" focusable=\"false\"><use href=\"#mat-i-matdrive\"></use></svg></span>");

    public static string Initials(string name)
    {
        var letters = string.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(p => char.ToUpperInvariant(p[0])));
        return letters.Length == 0 ? "?" : letters;
    }

    /// <summary>A size for people: 812 B, 14.2 KB, 3.1 MB.</summary>
    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var culture = CultureInfo.CurrentUICulture;
        return unit == 0 ? $"{bytes} B" : value.ToString(value < 10 ? "0.0" : "0", culture) + " " + units[unit];
    }
}
