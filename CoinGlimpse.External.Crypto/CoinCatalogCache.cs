using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoinGlimpse.DataObjects;

namespace CoinGlimpse.External.Crypto
{
    public sealed class CoinCatalogCache(TimeProvider clock)
    {
        private readonly object _gate = new();
        private Task<Snapshot> _refresh;

        internal async Task<List<CryptoListitem>> GetAsync(
            Func<CancellationToken, Task<List<CryptoListitem>>> load,
            TimeSpan duration,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task<Snapshot> refresh;
            lock (_gate)
            {
                if (_refresh == null || _refresh.IsFaulted || _refresh.IsCanceled ||
                    (_refresh.IsCompletedSuccessfully && clock.GetUtcNow() >= _refresh.Result.ExpiresAt))
                {
                    _refresh = RefreshAsync(load, duration);
                }
                refresh = _refresh;
            }

            var snapshot = await refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
            return snapshot.Coins.Select(c => new CryptoListitem
            {
                id = c.id, name = c.name, ticker = c.ticker
            }).ToList();
        }

        private async Task<Snapshot> RefreshAsync(
            Func<CancellationToken, Task<List<CryptoListitem>>> load, TimeSpan duration)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60), clock);
            var coins = await load(timeout.Token).ConfigureAwait(false);
            var ttl = duration > TimeSpan.Zero ? duration : TimeSpan.FromMinutes(10);
            return new Snapshot(coins, clock.GetUtcNow() + ttl);
        }

        private sealed record Snapshot(List<CryptoListitem> Coins, DateTimeOffset ExpiresAt);
    }
}
