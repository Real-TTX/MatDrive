using System.Security.Cryptography;
using System.Text;
using MatDrive.Data;
using MatDrive.Services.Accounts;
using Microsoft.Extensions.Caching.Memory;

namespace MatDrive.Services.WebDav;

/// <summary>The person of a WebDAV request.</summary>
public sealed record DavUser(long Id, string Email, DateTime Created);

public enum DavSignIn
{
    Ok,

    /// <summary>No credentials (the first request of most clients): 401 with the challenge.</summary>
    Missing,

    /// <summary>Wrong credentials: 401.</summary>
    Failed,

    /// <summary>Too many failed attempts from this address: 429.</summary>
    Throttled,
}

/// <summary>
/// HTTP Basic sign-in for WebDAV: the address and an app password (or the password of a local account), checked by
/// <see cref="AppPasswords.SignInAsync(AppDbContext, PasswordService, string, string, CancellationToken)"/>.
/// Clients send the credentials with every request, so a successful sign-in is remembered for <see cref="CacheTime"/> (keyed by a hash of the credentials,
/// never the password itself); failed attempts are limited per IP address (<see cref="FailuresPerMinute"/>).
/// </summary>
public sealed class DavAuthenticator(IMemoryCache cache, PasswordService passwords)
{
    public const int FailuresPerMinute = 10;

    /// <summary>How long a sign-in is remembered: a deleted app password or a deactivated account stops working within this time.</summary>
    public static readonly TimeSpan CacheTime = TimeSpan.FromMinutes(2);

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>The address and the password of a Basic <c>Authorization</c> header (UTF-8; Latin-1 from old clients); false when it is not one.</summary>
    public static bool TryParseBasic(string? header, out string email, out string password)
    {
        email = password = string.Empty;
        if (string.IsNullOrWhiteSpace(header) || !header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(header[6..].Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            text = Encoding.Latin1.GetString(bytes);
        }

        var colon = text.IndexOf(':');
        if (colon <= 0)
        {
            return false;
        }

        email = text[..colon].Trim();
        password = text[(colon + 1)..];
        return email.Length > 0;
    }

    public async Task<(DavSignIn Result, DavUser? User)> SignInAsync(AppDbContext db, string? authorization, string? address, CancellationToken ct = default)
    {
        if (!TryParseBasic(authorization, out var email, out var password))
        {
            return (DavSignIn.Missing, null);
        }

        var key = "webdav:signin:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(email.ToLowerInvariant() + "\n" + password)));
        if (cache.TryGetValue(key, out DavUser? known) && known is not null)
        {
            return (DavSignIn.Ok, known);
        }

        var failuresKey = "webdav:failures:" + (address ?? "unknown");
        if (cache.TryGetValue(failuresKey, out Failures? failures) && failures is not null && failures.Count >= FailuresPerMinute)
        {
            return (DavSignIn.Throttled, null);
        }

        var user = await AppPasswords.SignInAsync(db, passwords, email, password, ct);
        if (user is null)
        {
            var counter = cache.GetOrCreate(failuresKey, entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1);
                return new Failures();
            })!;
            Interlocked.Increment(ref counter.Count);
            return (DavSignIn.Failed, null);
        }

        var signedIn = new DavUser(user.Id, user.Email, user.CreateDate);
        cache.Set(key, signedIn, CacheTime);
        return (DavSignIn.Ok, signedIn);
    }

    private sealed class Failures
    {
        public int Count;
    }
}
