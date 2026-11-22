namespace DeckApi.Tournaments;

/// <summary>Pulls GameApi's tournament decks every <c>GameApi:SyncMinutes</c> (default 10) when <c>GameApi:BaseUrl</c> is set.</summary>
public sealed class TournamentDeckSyncWorker(IServiceScopeFactory scopes, IConfiguration config, TimeProvider clock, ILogger<TournamentDeckSyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(config.GetValue("GameApi:SyncMinutes", 10));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<TournamentDeckSync>().SyncAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(e, "Tournament deck sync failed, retrying in {Interval}", interval);
            }

            try
            {
                await Task.Delay(interval, clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
