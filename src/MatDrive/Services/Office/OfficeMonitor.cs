using MatDrive.Data;
using MatDrive.Localization;
using MatPlatform;

namespace MatDrive.Services.Office;

/// <summary>
/// Keeps the formats of the bound office editor fresh in the background: shortly after the start, then every minute it looks whether they are due
/// (<see cref="OfficeService.NeedsRefresh"/>: a new binding, older than six hours, or a failure five minutes ago) and fetches them only then.
/// So a binding that Mattiverse set before a restart, or one an administrator entered by hand, is used without a click.
/// </summary>
public sealed class OfficeMonitor(IServiceScopeFactory scopes, MatBindings bindings, IHttpClientFactory clients, ILogger<OfficeMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "The formats of the office editor could not be fetched; it tries again later.");
                }

                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // the app stops
        }
    }

    /// <summary>Fetches the formats when they are due; nothing while <c>office.edit</c> is not bound.</summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        var office = await bindings.GetAsync(OfficeService.Capability, ct);
        if (!office.IsBound)
        {
            return;
        }

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (!OfficeService.NeedsRefresh(await SettingsStore.LoadAsync(db, ct), office, DateTime.UtcNow))
        {
            return;
        }

        await OfficeService.RefreshAsync(db, clients.CreateClient("office"), office, force: true, ct);
        var status = await OfficeService.StatusAsync(db, office, ct);
        if (status.Problem is { } problem)
        {
            logger.LogWarning("The office editor ({Source}) did not answer: {Problem}", office.Source, Languages.In(Languages.Default, () => OfficeService.ProblemText(problem)));
        }
        else
        {
            logger.LogInformation("The office editor ({Source}) opens {Kinds} kinds of documents.", office.Source, status.Kinds.Count);
        }
    }
}
