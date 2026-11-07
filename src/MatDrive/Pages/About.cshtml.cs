namespace MatDrive.Pages;

/// <summary>The About page, reached through the version number at the foot of the menu: what this build is and the keys of the editor.</summary>
public sealed class AboutModel : AppPageModel
{
    /// <summary>Release, Nightly or Local: told by the form of the version (<c>0.1.42-20261031</c>, <c>nightly-42-20261031</c>, <c>local-20261031</c>).</summary>
    public string Channel { get; } = AppInfo.Version.StartsWith("nightly", StringComparison.Ordinal) ? "Nightly"
        : AppInfo.Version.StartsWith("local", StringComparison.Ordinal) ? "Local"
        : "Release";
}
