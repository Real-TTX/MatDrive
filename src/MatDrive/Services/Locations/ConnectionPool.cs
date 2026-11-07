using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace MatDrive.Services.Locations;

/// <summary>
/// Open connections to servers, kept for the next request (signing in to SFTP costs a few hundred milliseconds; the explorer asks many times in a row). A
/// connection is lent to one request at a time (the clients of SSH.NET, FluentFTP and SMBLibrary do not take parallel calls) and comes back when the lease is
/// disposed – unless it was marked broken. Connections nobody used for <see cref="MaxIdle"/> are closed by <see cref="Sweep"/>.
/// </summary>
public sealed class ConnectionPool<T>(Func<T, bool> isAlive, Action<T> close, int perKey = 4) : IDisposable where T : class
{
    private sealed record Slot(T Client, DateTime LastUsed);

    private readonly ConcurrentDictionary<string, ConcurrentStack<Slot>> _idle = new(StringComparer.Ordinal);
    private bool _disposed;

    public TimeSpan MaxIdle { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>The number of idle connections (tests, the health of the pool).</summary>
    public int IdleCount => _idle.Values.Sum(s => s.Count);

    /// <summary>Lends a connection of <paramref name="key"/>: an idle one that is still alive, or a new one.</summary>
    public async Task<Lease> RentAsync(string key, Func<Task<T>> create)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_idle.TryGetValue(key, out var stack))
        {
            while (stack.TryPop(out var slot))
            {
                if (DateTime.UtcNow - slot.LastUsed > MaxIdle || !Alive(slot.Client))
                {
                    Close(slot.Client);
                    continue;
                }

                return new Lease(this, key, slot.Client);
            }
        }

        return new Lease(this, key, await create());
    }

    /// <summary>Closes what stood idle too long or died.</summary>
    public void Sweep()
    {
        foreach (var (key, stack) in _idle)
        {
            var keep = new List<Slot>();
            while (stack.TryPop(out var slot))
            {
                if (DateTime.UtcNow - slot.LastUsed > MaxIdle || !Alive(slot.Client))
                {
                    Close(slot.Client);
                }
                else
                {
                    keep.Add(slot);
                }
            }

            foreach (var slot in Enumerable.Reverse(keep))
            {
                stack.Push(slot);
            }

            if (stack.IsEmpty)
            {
                _idle.TryRemove(key, out _);
            }
        }
    }

    /// <summary>Closes every idle connection of a key (the settings of a location changed).</summary>
    public void Drop(string key)
    {
        if (_idle.TryRemove(key, out var stack))
        {
            while (stack.TryPop(out var slot))
            {
                Close(slot.Client);
            }
        }
    }

    private void Return(string key, T client, bool broken)
    {
        if (broken || _disposed || !Alive(client))
        {
            Close(client);
            return;
        }

        var stack = _idle.GetOrAdd(key, _ => new ConcurrentStack<Slot>());
        if (stack.Count >= perKey)
        {
            Close(client);
            return;
        }

        stack.Push(new Slot(client, DateTime.UtcNow));
    }

    private bool Alive(T client)
    {
        try
        {
            return isAlive(client);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void Close(T client)
    {
        try
        {
            close(client);
        }
        catch (Exception)
        {
            // a connection that cannot be closed is lost anyway
        }
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var stack in _idle.Values)
        {
            while (stack.TryPop(out var slot))
            {
                Close(slot.Client);
            }
        }

        _idle.Clear();
    }

    /// <summary>
    /// The key of a server and an account. The secret only goes in as a hash: two accounts never share a connection, and the password does not lie around in
    /// the keys of the pool.
    /// </summary>
    public static string KeyOf(params string?[] parts)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001f', parts.Select(p => p ?? string.Empty)))));

    /// <summary>A lent connection; disposing it gives it back.</summary>
    public sealed class Lease : IDisposable
    {
        private readonly ConnectionPool<T> _pool;
        private readonly string _key;
        private bool _returned;

        internal Lease(ConnectionPool<T> pool, string key, T client)
        {
            _pool = pool;
            _key = key;
            Client = client;
        }

        public T Client { get; }

        /// <summary>After an error of the connection: it is closed instead of going back.</summary>
        public bool Broken { get; set; }

        public void Dispose()
        {
            if (_returned)
            {
                return;
            }

            _returned = true;
            _pool.Return(_key, Client, Broken);
        }
    }
}

/// <summary>A stream that holds a lease (or anything else) until it is disposed: a download keeps its connection while it is read.</summary>
public sealed class HoldingStream(Stream inner, params IDisposable[] holds) : Stream
{
    private bool _disposed;

    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => inner.CanSeek;

    public override bool CanWrite => inner.CanWrite;

    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.ReadAsync(buffer, offset, count, cancellationToken);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override void SetLength(long value) => inner.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.WriteAsync(buffer, offset, count, cancellationToken);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => inner.WriteAsync(buffer, cancellationToken);

    public override async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            await inner.DisposeAsync();
        }
        finally
        {
            foreach (var hold in holds)
            {
                hold.Dispose();
            }
        }

        GC.SuppressFinalize(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            try
            {
                inner.Dispose();
            }
            finally
            {
                foreach (var hold in holds)
                {
                    hold.Dispose();
                }
            }
        }

        base.Dispose(disposing);
    }
}
