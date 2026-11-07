using Microsoft.AspNetCore.StaticFiles;

namespace MatDrive.Services.Files;

/// <summary>What a file is for the interface: its category (icon, filter), its MIME type and how it can be previewed.</summary>
public static class FileTypes
{
    private static readonly FileExtensionContentTypeProvider Provider = CreateProvider();

    public const string Folder = "folder";

    /// <summary>The categories of the filter "Type", in their order.</summary>
    public static readonly string[] Categories = ["folder", "document", "spreadsheet", "presentation", "database", "pdf", "image", "video", "audio", "archive", "text", "code", "other"];

    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".doc"] = "document", [".docx"] = "document", [".odt"] = "document", [".rtf"] = "document", [".matdoc"] = "document", [".pages"] = "document",
        [".xls"] = "spreadsheet", [".xlsx"] = "spreadsheet", [".xlsm"] = "spreadsheet", [".ods"] = "spreadsheet", [".csv"] = "spreadsheet", [".tsv"] = "spreadsheet", [".matsheet"] = "spreadsheet", [".numbers"] = "spreadsheet",
        [".ppt"] = "presentation", [".pptx"] = "presentation", [".odp"] = "presentation", [".matslides"] = "presentation", [".key"] = "presentation",
        [".accdb"] = "database", [".mdb"] = "database", [".matbase"] = "database", [".sqlite"] = "database", [".db"] = "database",
        [".pdf"] = "pdf",
        [".png"] = "image", [".jpg"] = "image", [".jpeg"] = "image", [".gif"] = "image", [".webp"] = "image", [".bmp"] = "image", [".svg"] = "image", [".avif"] = "image", [".ico"] = "image", [".heic"] = "image", [".tif"] = "image", [".tiff"] = "image",
        [".mp4"] = "video", [".webm"] = "video", [".mov"] = "video", [".mkv"] = "video", [".avi"] = "video", [".m4v"] = "video",
        [".mp3"] = "audio", [".wav"] = "audio", [".ogg"] = "audio", [".flac"] = "audio", [".m4a"] = "audio", [".aac"] = "audio", [".opus"] = "audio",
        [".zip"] = "archive", [".7z"] = "archive", [".rar"] = "archive", [".tar"] = "archive", [".gz"] = "archive", [".tgz"] = "archive", [".bz2"] = "archive", [".xz"] = "archive",
        [".txt"] = "text", [".md"] = "text", [".log"] = "text", [".ini"] = "text", [".conf"] = "text", [".cfg"] = "text", [".env"] = "text",
        [".json"] = "code", [".xml"] = "code", [".yml"] = "code", [".yaml"] = "code", [".toml"] = "code", [".html"] = "code", [".htm"] = "code", [".css"] = "code", [".js"] = "code", [".mjs"] = "code",
        [".ts"] = "code", [".cs"] = "code", [".csproj"] = "code", [".java"] = "code", [".py"] = "code", [".go"] = "code", [".rs"] = "code", [".c"] = "code", [".h"] = "code", [".cpp"] = "code",
        [".php"] = "code", [".rb"] = "code", [".sh"] = "code", [".ps1"] = "code", [".sql"] = "code", [".cshtml"] = "code", [".razor"] = "code", [".kt"] = "code", [".swift"] = "code", [".dockerfile"] = "code",
    };

    /// <summary>The category of a file by its name (and MIME type as a fallback).</summary>
    public static string CategoryOf(string name, string? mime = null)
    {
        if (ByExtension.TryGetValue(Path.GetExtension(name), out var category))
        {
            return category;
        }

        if (string.Equals(name, "Dockerfile", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "Makefile", StringComparison.OrdinalIgnoreCase))
        {
            return "code";
        }

        return mime switch
        {
            null => "other",
            _ when mime.StartsWith("image/", StringComparison.Ordinal) => "image",
            _ when mime.StartsWith("video/", StringComparison.Ordinal) => "video",
            _ when mime.StartsWith("audio/", StringComparison.Ordinal) => "audio",
            _ when mime.StartsWith("text/", StringComparison.Ordinal) => "text",
            _ => "other",
        };
    }

    /// <summary>The extensions of a category (for the filter in the database).</summary>
    public static string[] ExtensionsOf(string category) => ByExtension.Where(p => p.Value == category).Select(p => p.Key).ToArray();

    /// <summary>The icon of a category (toolkit sprite + app sprite).</summary>
    public static string IconOf(string category) => category switch
    {
        "folder" => "folder",
        "document" => "file-doc",
        "spreadsheet" => "file-sheet",
        "presentation" => "file-slides",
        "database" => "database",
        "pdf" => "file-pdf",
        "image" => "image",
        "video" => "file-video",
        "audio" => "file-audio",
        "archive" => "archive",
        "text" => "file-text",
        "code" => "code",
        _ => "file",
    };

    public static string MimeOf(string name)
        => Provider.TryGetContentType(name, out var type) ? type : "application/octet-stream";

    /// <summary>How the browser can show a file: image, video, audio, pdf, text (also code), or none.</summary>
    public static string PreviewOf(string name, long size)
    {
        var category = CategoryOf(name);
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return category switch
        {
            "image" when ext is not (".heic" or ".tif" or ".tiff") => "image",
            "video" when ext is ".mp4" or ".webm" or ".m4v" or ".mov" => "video",
            "audio" when ext is not ".aac" => "audio",
            "pdf" => "pdf",
            "text" or "code" when size <= 5 * 1024 * 1024 => "text",
            "spreadsheet" when ext is ".csv" or ".tsv" && size <= 5 * 1024 * 1024 => "text",
            _ => "none",
        };
    }

    /// <summary>Whether a file is text whose start is kept for the search.</summary>
    public static bool IsIndexable(string name, long size) => size <= 2 * 1024 * 1024 && CategoryOf(name) is "text" or "code" || (Path.GetExtension(name).ToLowerInvariant() is ".csv" or ".tsv" && size <= 2 * 1024 * 1024);

    /// <summary>Whether a file can be edited as text in the browser (the editor of MatDrive).</summary>
    public static bool IsEditableText(string name, long size) => size <= 5 * 1024 * 1024 && (CategoryOf(name) is "text" or "code" || Path.GetExtension(name).ToLowerInvariant() is ".csv" or ".tsv" or ".svg");

    /// <summary>The language of the code editor for a file (for the colours of the syntax).</summary>
    public static string LanguageOf(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".json" => "json",
        ".xml" or ".html" or ".htm" or ".svg" or ".csproj" or ".cshtml" or ".razor" => "markup",
        ".css" => "css",
        ".js" or ".mjs" or ".ts" => "js",
        ".cs" or ".java" or ".c" or ".h" or ".cpp" or ".go" or ".rs" or ".kt" or ".swift" or ".php" => "c",
        ".py" or ".rb" or ".sh" or ".ps1" or ".yml" or ".yaml" or ".toml" or ".ini" or ".conf" or ".env" => "hash",
        ".sql" => "sql",
        ".md" => "markdown",
        _ => "plain",
    };

    private static FileExtensionContentTypeProvider CreateProvider()
    {
        var provider = new FileExtensionContentTypeProvider();
        provider.Mappings[".md"] = "text/markdown";
        provider.Mappings[".log"] = "text/plain";
        provider.Mappings[".yml"] = "text/yaml";
        provider.Mappings[".yaml"] = "text/yaml";
        provider.Mappings[".matdoc"] = "application/vnd.matoffice.doc+json";
        provider.Mappings[".matsheet"] = "application/vnd.matoffice.sheet+json";
        provider.Mappings[".matslides"] = "application/vnd.matoffice.slides+json";
        provider.Mappings[".matbase"] = "application/vnd.matoffice.base+json";
        provider.Mappings[".webp"] = "image/webp";
        provider.Mappings[".avif"] = "image/avif";
        provider.Mappings[".opus"] = "audio/opus";
        provider.Mappings[".mkv"] = "video/x-matroska";
        return provider;
    }
}
