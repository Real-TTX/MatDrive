using MatDrive.Localization;

namespace MatDrive.Services.Locations;

/// <summary>
/// Errors of the locations as words for people (<c>Locations.Error.{Code}</c>). What a remote server said is added in brackets (it helps the administrator);
/// a local error never shows a path of the container. An error the providers did not expect is logged with its stack and shown as "the server answered with an error".
/// </summary>
public static class LocationErrors
{
    /// <summary>Set at the start of the app (the static business layer has no logger of its own).</summary>
    public static ILogger? Log { get; set; }

    public static (LocationError Code, string? Detail) Classify(Exception ex)
    {
        switch (ex)
        {
            case LocationException le:
                return (le.Code, le.Detail);
            case OperationCanceledException or TimeoutException:
                return (LocationError.Timeout, null);
            case UnauthorizedAccessException:
                return (LocationError.AccessDenied, null);
            case IOException:
                return (LocationError.Remote, null);
            default:
                Log?.LogWarning(ex, "A storage location failed in an unexpected way.");
                return (LocationError.Remote, ex.Message);
        }
    }

    public static string Message(Exception ex)
    {
        var (code, detail) = Classify(ex);
        return Message(code, detail);
    }

    public static string Message(LocationError code, string? detail)
    {
        if (code == LocationError.Worded && !string.IsNullOrWhiteSpace(detail))
        {
            return detail;
        }

        var text = Text.T("Locations.Error." + code);
        return string.IsNullOrWhiteSpace(detail) || code is not (LocationError.Remote or LocationError.Unreachable or LocationError.AccessDenied or LocationError.TooLarge or LocationError.FolderMissing)
            ? text
            : Text.T("Locations.Error.WithDetail", text, Short(detail));
    }

    private static string Short(string detail)
    {
        var line = detail.ReplaceLineEndings(" ").Trim();
        return line.Length > 200 ? line[..200] + "…" : line;
    }
}
