using System.Xml.Linq;

namespace MatDrive.Services.WebDav;

/// <summary>
/// A WebDAV write lock on a file or a folder. <see cref="Chain"/> is the chain of node ids from the top of the drive to the locked node (<c>Node.Path</c>), so a
/// lock is found for a node by its id and for everything below a folder locked with depth infinity, whatever path a person uses to reach it.
/// </summary>
public sealed record DavLock(
    string Token,
    long NodeId,
    long[] Chain,
    bool Exclusive,
    bool Deep,
    long UserId,
    string Root,
    XElement? Owner,
    TimeSpan Timeout,
    DateTimeOffset Expires)
{
    /// <summary>Whether the lock reaches the node of <paramref name="chain"/>: the node itself, or a folder above it locked with depth infinity.</summary>
    public bool Covers(IReadOnlyList<long> chain) => chain.Count > 0 && (chain[^1] == NodeId || (Deep && chain.Contains(NodeId)));

    /// <summary>Whether the lock is on something below the node of <paramref name="chain"/>.</summary>
    public bool IsBelow(IReadOnlyList<long> chain) => chain.Count > 0 && NodeId != chain[^1] && Chain.Contains(chain[^1]);
}

public enum DavLockOutcome
{
    Locked,

    /// <summary>Another lock is in the way (423).</summary>
    Conflict,

    /// <summary>Too many locks (of the person, or at all): the memory of the server is bounded.</summary>
    TooMany,
}

public enum DavUnlockOutcome
{
    Unlocked,

    /// <summary>The token is not a lock (any more).</summary>
    NotFound,

    /// <summary>The lock belongs to another person.</summary>
    Forbidden,

    /// <summary>The lock does not reach the resource of the request.</summary>
    NotCovered,
}

/// <summary>
/// The locks of the WebDAV clients, in memory (Office, the Windows Explorer and the Finder lock a file while it is open or being written). Exclusive and shared
/// write locks, depth 0 and infinity, with a timeout the client refreshes; an expired lock is gone. Locks only coordinate WebDAV clients: the explorer of the browser
/// and MatOffice do not see them. They are lost with a restart (the If header forgives a token it does not know, see <see cref="DavIfHeader.Evaluate"/>).
/// </summary>
public sealed class DavLockManager(TimeProvider time)
{
    public const string TokenPrefix = "opaquelocktoken:";

    /// <summary>The most locks at a time (all people) and of one person: the memory of the server is bounded.</summary>
    public const int MaxLocks = 10_000;

    public const int MaxLocksPerUser = 500;

    /// <summary>The timeout of a lock when the client asks for none.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    /// <summary>The longest timeout (also for "Infinite"): a client that went away does not keep a file locked for long; clients refresh their locks.</summary>
    public static readonly TimeSpan MaxTimeout = TimeSpan.FromHours(1);

    private readonly Lock _sync = new();
    private readonly Dictionary<string, DavLock> _locks = new(StringComparer.Ordinal);

    public DavLockManager()
        : this(TimeProvider.System)
    {
    }

    public DateTimeOffset Now => time.GetUtcNow();

    /// <summary>
    /// Locks the node of <paramref name="chain"/>, unless a lock is in the way: one that reaches the node, or (for depth infinity) one below it; an exclusive lock
    /// conflicts with every other, shared locks live side by side.
    /// </summary>
    public (DavLockOutcome Outcome, DavLock? Lock, DavLock? Conflict) Lock(long userId, long[] chain, bool exclusive, bool deep, string root, XElement? owner, TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfZero(chain.Length);
        lock (_sync)
        {
            Purge();
            var conflict = ConflictOf(chain, exclusive, deep);
            if (conflict is not null)
            {
                return (DavLockOutcome.Conflict, null, conflict);
            }

            if (_locks.Count >= MaxLocks || _locks.Values.Count(l => l.UserId == userId) >= MaxLocksPerUser)
            {
                return (DavLockOutcome.TooMany, null, null);
            }

            var created = new DavLock(TokenPrefix + Guid.NewGuid().ToString("D"), chain[^1], chain, exclusive, deep, userId, root, owner, timeout, time.GetUtcNow() + timeout);
            _locks[created.Token] = created;
            return (DavLockOutcome.Locked, created, null);
        }
    }

    /// <summary>
    /// The lock that would keep a new lock out, or null. For a node that does not exist yet, pass the chain of its folder followed by 0: only the locks with depth
    /// infinity above it count.
    /// </summary>
    public DavLock? FindConflict(long[] chain, bool exclusive, bool deep)
    {
        lock (_sync)
        {
            Purge();
            return ConflictOf(chain, exclusive, deep);
        }
    }

    /// <summary>Gives a lock of the person that reaches the node a new timeout; null when there is none.</summary>
    public DavLock? Refresh(string token, long userId, long[] chain, TimeSpan timeout)
    {
        lock (_sync)
        {
            Purge();
            if (!_locks.TryGetValue(token, out var found) || found.UserId != userId || !found.Covers(chain))
            {
                return null;
            }

            var refreshed = found with { Timeout = timeout, Expires = time.GetUtcNow() + timeout };
            _locks[token] = refreshed;
            return refreshed;
        }
    }

    /// <summary>Ends a lock of the person; <paramref name="chain"/> (when the resource exists) must be reached by it.</summary>
    public DavUnlockOutcome Unlock(string token, long userId, long[]? chain)
    {
        lock (_sync)
        {
            Purge();
            if (!_locks.TryGetValue(token, out var found))
            {
                return DavUnlockOutcome.NotFound;
            }

            if (found.UserId != userId)
            {
                return DavUnlockOutcome.Forbidden;
            }

            if (chain is not null && !found.Covers(chain))
            {
                return DavUnlockOutcome.NotCovered;
            }

            _locks.Remove(token);
            return DavUnlockOutcome.Unlocked;
        }
    }

    /// <summary>The locks that reach the node of <paramref name="chain"/>.</summary>
    public IReadOnlyList<DavLock> Covering(IReadOnlyList<long> chain)
    {
        lock (_sync)
        {
            Purge();
            return _locks.Count == 0 ? [] : _locks.Values.Where(l => l.Covers(chain)).ToList();
        }
    }

    /// <summary>The locks on nodes below the node of <paramref name="chain"/>.</summary>
    public IReadOnlyList<DavLock> Below(IReadOnlyList<long> chain)
    {
        lock (_sync)
        {
            Purge();
            return _locks.Count == 0 ? [] : _locks.Values.Where(l => l.IsBelow(chain)).ToList();
        }
    }

    /// <summary>Every lock now (for the properties of a whole folder at once).</summary>
    public IReadOnlyList<DavLock> All()
    {
        lock (_sync)
        {
            Purge();
            return _locks.Count == 0 ? [] : _locks.Values.ToList();
        }
    }

    /// <summary>Whether a token is a lock of this server now.</summary>
    public bool IsKnown(string token)
    {
        lock (_sync)
        {
            Purge();
            return _locks.ContainsKey(token);
        }
    }

    /// <summary>Removes the locks of a node and of everything below it (it was moved or deleted: a lock does not follow its resource).</summary>
    public void RemoveSubtree(IReadOnlyList<long> chain)
    {
        lock (_sync)
        {
            foreach (var token in _locks.Values.Where(l => l.NodeId == chain[^1] || l.IsBelow(chain)).Select(l => l.Token).ToList())
            {
                _locks.Remove(token);
            }
        }
    }

    /// <summary>
    /// The lock that keeps a change out, or null when the request may go on. Every group (the locks that reach one node the request changes) needs one of its
    /// locks submitted (in the If header) by the person who holds it.
    /// </summary>
    public static DavLock? Unsubmitted(IEnumerable<IReadOnlyCollection<DavLock>> groups, IReadOnlySet<string> tokens, long userId)
    {
        foreach (var group in groups)
        {
            if (group.Count > 0 && !group.Any(l => l.UserId == userId && tokens.Contains(l.Token)))
            {
                return group.First();
            }
        }

        return null;
    }

    /// <summary>The timeout a client asks for (<c>Timeout: Second-3600, Infinite</c>), within the bounds of the server.</summary>
    public static TimeSpan ParseTimeout(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return DefaultTimeout;
        }

        foreach (var part in header.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Equals("Infinite", StringComparison.OrdinalIgnoreCase))
            {
                return MaxTimeout;
            }

            if (part.StartsWith("Second-", StringComparison.OrdinalIgnoreCase)
                && long.TryParse(part.AsSpan(7), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
            {
                return TimeSpan.FromSeconds(Math.Clamp(seconds, 1, (long)MaxTimeout.TotalSeconds));
            }
        }

        return DefaultTimeout;
    }

    private DavLock? ConflictOf(long[] chain, bool exclusive, bool deep)
        => _locks.Values.FirstOrDefault(l => (l.Covers(chain) || (deep && l.IsBelow(chain))) && (exclusive || l.Exclusive));

    private void Purge()
    {
        if (_locks.Count == 0)
        {
            return;
        }

        var now = time.GetUtcNow();
        foreach (var token in _locks.Values.Where(l => l.Expires <= now).Select(l => l.Token).ToList())
        {
            _locks.Remove(token);
        }
    }
}
