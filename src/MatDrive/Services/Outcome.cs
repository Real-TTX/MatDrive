namespace MatDrive.Services;

/// <summary>The answer of a change in the business layer: what came out of it, or what is wrong (already worded for the person).</summary>
public sealed record Outcome<T>(T? Value, string? Error, bool NotAllowed = false)
{
    public bool Ok => Error is null && !NotAllowed;

    public static Outcome<T> Success(T value) => new(value, null);

    public static Outcome<T> Fail(string error) => new(default, error);

    /// <summary>The record does not exist or the person may not do this: the page answers 404, exactly like for a record that is not there.</summary>
    public static Outcome<T> Denied() => new(default, null, true);
}
