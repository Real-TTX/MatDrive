using MatDrive.Data;
using Microsoft.EntityFrameworkCore;

namespace MatDrive.Services.Locations;

/// <summary>The outcome of checking every active location: how many there are and how many answered.</summary>
public sealed record LocationCheckSummary(int Total, int Reachable)
{
    public int Unreachable => Total - Reachable;
}

/// <summary>
/// Checks the reachability (and the space) of every active location in the background: shortly after the start, then every five minutes. The results live in
/// memory (<see cref="LocationConnections"/>); the menu, the list of the administration and the health of the platform read them. Every minute it closes
/// connections of the pools that nobody used for a while.
/// </summary>
public sealed class LocationMonitor(IServiceScopeFactory scopes, LocationConnections connections, ILogger<LocationMonitor> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _running = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            var next = DateTime.MinValue;
            while (!stoppingToken.IsCancellationRequested)
            {
                if (DateTime.UtcNow >= next)
                {
                    try
                    {
                        await CheckAllAsync(stoppingToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogWarning(ex, "The check of the storage locations failed; it tries again later.");
                    }

                    next = DateTime.UtcNow + Interval;
                }

                connections.Sweep();
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // the app stops
        }
    }

    /// <summary>Checks every active location now (four at a time); a check that is already running is waited for and counted.</summary>
    public async Task<LocationCheckSummary> CheckAllAsync(CancellationToken ct)
    {
        await _running.WaitAsync(ct);
        try
        {
            List<StorageLocation> locations;
            using (var scope = scopes.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                locations = await db.StorageLocations.AsNoTracking().Where(l => l.IsActive).ToListAsync(ct);
            }

            var reachable = 0;
            await Parallel.ForEachAsync(locations, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (location, token) =>
            {
                var status = await CheckAsync(connections, location, token);
                if (status.Reachable)
                {
                    Interlocked.Increment(ref reachable);
                }
            });

            logger.LogInformation("Storage locations checked: {Reachable} of {Total} reachable.", reachable, locations.Count);
            return new LocationCheckSummary(locations.Count, reachable);
        }
        finally
        {
            _running.Release();
        }
    }

    /// <summary>The active locations and the names of those the last check could not reach (the health of the platform; not checked yet counts as fine).</summary>
    public async Task<(int Active, IReadOnlyList<string> Unreachable)> HealthAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var active = await db.StorageLocations.AsNoTracking().Where(l => l.IsActive).Select(l => new { l.Id, l.Name }).ToListAsync(ct);
        var down = active.Where(l => connections.StatusOf(l.Id) is { Reachable: false }).Select(l => l.Name).Order(StringComparer.CurrentCultureIgnoreCase).ToList();
        return (active.Count, down);
    }

    /// <summary>Checks one location (with a timeout) and remembers the outcome.</summary>
    public static async Task<LocationStatus> CheckAsync(LocationConnections connections, StorageLocation location, CancellationToken ct)
    {
        var status = await ProbeAsync(connections, connections.SettingsOf(location), ct);
        connections.Report(location.Id, status);
        return status;
    }

    /// <summary>Connects with the settings (saved or not yet saved: the test of the edit page) and asks for the space.</summary>
    public static async Task<LocationStatus> ProbeAsync(LocationConnections connections, LocationSettings settings, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CheckTimeout);
        try
        {
            var provider = connections.Create(settings);
            await provider.CheckAsync(timeout.Token);
            LocationSpace? space = null;
            try
            {
                space = await provider.SpaceAsync(timeout.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // the space is a bonus: a server that does not tell it is still reachable
            }

            return new LocationStatus(true, DateTime.UtcNow, Space: space);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new LocationStatus(false, DateTime.UtcNow, LocationError.Timeout);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var (code, detail) = LocationErrors.Classify(ex);
            return new LocationStatus(false, DateTime.UtcNow, code is LocationError.NotFound ? LocationError.FolderMissing : code, detail);
        }
    }
}
