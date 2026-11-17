using Binance.Net.Enums;
using Binance.Net.Interfaces.Clients;
using Binance.Net.Objects.Models.Futures.Socket;
using CryptoExchange.Net.Objects.Sockets;
using CryptoOrderBookDashboard.Persistence;
using Microsoft.Extensions.Options;

namespace CryptoOrderBookDashboard.Liquidations;

// Subscribes to !forceOrder@arr (every USDT-M perpetual, one stream) and feeds LiquidationStore.
// Each liquidation updates its minute bucket in the database; liquidations at or above
// Liquidations:MinPersistUsd are also stored individually. Connected time is recorded as feed
// sessions (with a heartbeat), so gaps from disconnects and restarts are visible later.
//
// Binance sends at most one liquidation per symbol per second (the latest), so this is a sample.
// There is no public REST history to backfill from, so missed liquidations stay missed.
public sealed class LiquidationHostedService(
    IBinanceSocketClient socketClient,
    LiquidationStore store,
    LiquidationRepository repository,
    PersistenceWriter writer,
    IOptions<LiquidationOptions> options,
    ILogger<LiquidationHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RestoreAsync(stoppingToken);
            await SubscribeWithRetryAsync(stoppingToken);

            using var timer = new PeriodicTimer(Heartbeat);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                if (store.CurrentSession(DateTime.UtcNow) is { } session)
                {
                    writer.TryEnqueue(LiquidationRepository.UpsertSession(session));
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Close the session at the real stop time; the writer stops after this service and
        // drains its queue, so this last write is committed.
        if (store.EndSession(DateTime.UtcNow) is { } session)
        {
            writer.TryEnqueue(LiquidationRepository.UpsertSession(session));
        }

        await base.StopAsync(cancellationToken);
    }

    private async Task RestoreAsync(CancellationToken ct)
    {
        try
        {
            var (events, minutes, sessions) = await repository.LoadSinceAsync(DateTime.UtcNow - LiquidationStore.Retention, ct);
            store.Restore(events, minutes, sessions);
            logger.LogInformation(
                "Restored {Events} large liquidation(s), {Minutes} minute bucket(s) and {Sessions} feed session(s) from the last 24h",
                events.Count, minutes.Count, sessions.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Better to run with an empty history than not at all.
            logger.LogError(ex, "Could not restore liquidation history; starting empty");
        }
    }

    private async Task SubscribeWithRetryAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(2);
        while (true)
        {
            var result = await socketClient.UsdFuturesApi.ExchangeData.SubscribeToAllLiquidationUpdatesAsync(OnLiquidation, ct);
            if (result.Success)
            {
                var subscription = result.Data;
                subscription.ConnectionLost += () => EndSession();
                subscription.ConnectionRestored += downtime => store.BeginSession(DateTime.UtcNow);
                store.BeginSession(DateTime.UtcNow);
                logger.LogInformation("Subscribed to the liquidation stream");
                return;
            }

            logger.LogWarning("Liquidation stream subscription failed: {Error}; retrying in {Delay}", result.Error, delay);
            await Task.Delay(delay, ct);
            delay = TimeSpan.FromSeconds(Math.Min(60, delay.TotalSeconds * 2));
        }
    }

    private void EndSession()
    {
        if (store.EndSession(DateTime.UtcNow) is { } session)
        {
            writer.TryEnqueue(LiquidationRepository.UpsertSession(session));
        }
    }

    private void OnLiquidation(DataEvent<BinanceFuturesStreamLiquidation> update)
    {
        try
        {
            var data = update.Data;

            // Size what was actually filled, at the average fill price - not the order's limit.
            var quantity = data.QuantityFilled > 0 ? data.QuantityFilled : data.Quantity;
            var price = data.AveragePrice > 0 ? data.AveragePrice : data.Price;
            if (quantity <= 0 || price <= 0)
            {
                return;
            }

            var evt = new LiquidationEvent(
                data.Symbol,
                data.Side == OrderSide.Sell ? LiquidatedSide.Long : LiquidatedSide.Short,
                price,
                quantity,
                price * quantity,
                DateTime.SpecifyKind(data.Timestamp, DateTimeKind.Utc));

            var minute = store.Add(evt);
            writer.TryEnqueue(LiquidationRepository.UpsertMinute(minute));
            if (evt.UsdValue >= options.Value.MinPersistUsd)
            {
                writer.TryEnqueue(LiquidationRepository.InsertEvent(evt));
            }
        }
        catch (Exception ex)
        {
            // Never let one malformed message take down the socket handler.
            logger.LogError(ex, "Failed to process liquidation update");
        }
    }
}
