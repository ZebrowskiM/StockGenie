using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CoinGlimpse.DataObjects;

namespace CoinGlimpse.External.Crypto
{
    public sealed class CoinCatalogCache(TimeProvider clock)
    {
        private readonly object _gate = new();
        private Task<Snapshot> _refresh;
        private Snapshot _lastGood;
        private DateTimeOffset _nextAttempt;

        internal async Task<CoinCatalogRead> GetAsync(
            Func<CancellationToken, Task<List<CryptoListitem>>> load,
            CoinGeckoOptions options,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task<Snapshot> refresh;
            lock (_gate)
            {
                if (_refresh == null || (_refresh.IsCompleted && clock.GetUtcNow() >= _nextAttempt))
                    _refresh = RefreshAsync(load, options);

                refresh = _refresh;
            }

            Snapshot snapshot;
            try
            {
                snapshot = await refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsRefreshFailure(ex))
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    snapshot = _lastGood;
                    if (snapshot == null || clock.GetUtcNow() >= snapshot.StaleUntil)
                        throw;
                }
            }

            var coins = snapshot.Coins.Select(c => new CryptoListitem
            {
                id = c.id, name = c.name, ticker = c.ticker
            }).ToList();
            var now = clock.GetUtcNow();
            var age = now >= snapshot.FetchedAt ? now - snapshot.FetchedAt : TimeSpan.Zero;
            return new CoinCatalogRead(coins, age, now >= snapshot.FreshUntil);
        }

        private async Task<Snapshot> RefreshAsync(
            Func<CancellationToken, Task<List<CryptoListitem>>> load, CoinGeckoOptions options)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60), clock);
            try
            {
                var coins = await load(timeout.Token).ConfigureAwait(false);
                var ttl = options.ListCacheDuration > TimeSpan.Zero
                    ? options.ListCacheDuration : TimeSpan.FromMinutes(10);
                lock (_gate)
                {
                    var fetchedAt = clock.GetUtcNow();
                    _nextAttempt = fetchedAt + ttl;
                    _lastGood = new Snapshot(coins, fetchedAt, _nextAttempt, _nextAttempt + (options.ListMaxStaleDuration > TimeSpan.Zero ? options.ListMaxStaleDuration : TimeSpan.Zero));
                    return _lastGood;
                }
            }
            catch (Exception ex) when (IsRefreshFailure(ex))
            {
                lock (_gate)
                    _nextAttempt = clock.GetUtcNow() + (options.ListRefreshBackoff > TimeSpan.Zero ? options.ListRefreshBackoff : TimeSpan.Zero);
                throw;
            }
        }

        private static bool IsRefreshFailure(Exception ex) =>
            ex is HttpRequestException or IOException or JsonException or InvalidOperationException
                or OperationCanceledException or CoinGeckoApiException;

        private sealed record Snapshot(List<CryptoListitem> Coins, DateTimeOffset FetchedAt,
            DateTimeOffset FreshUntil, DateTimeOffset StaleUntil);
    }
}
