using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Headers;
using System.Threading.Tasks;

namespace CoinGlimpse.External.Crypto
{
    public sealed class CoinQuoteCache(TimeProvider clock)
    {
        private readonly object _gate = new();
        private readonly Dictionary<Key, Entry> _entries = new();

        private readonly Dictionary<Key, Lazy<Task<CoinQuoteBatch>>> _pending = new();
        private readonly Dictionary<Provider, DateTimeOffset> _cooldowns = new();

        internal Task<CoinQuoteBatch> GetOrFetch(Key key, TimeSpan duration, int capacity,
            Func<Task<CoinQuoteBatch>> fetch, out bool hit)
        {
            Lazy<Task<CoinQuoteBatch>> pending;
            lock (_gate)
            {
                if (duration > TimeSpan.Zero && capacity > 0 && _entries.TryGetValue(key, out var entry)
                    && clock.GetUtcNow() < entry.ExpiresAt)
                {
                    hit = true;
                    return Task.FromResult(entry.Batch);
                }
                hit = false;
                if (!_pending.TryGetValue(key, out pending))
                {
                    pending = new(() => FetchAndStoreAsync(key, duration, capacity, fetch));
                    _pending.Add(key, pending);
                }
            }
            return pending.Value;
        }

        private async Task<CoinQuoteBatch> FetchAndStoreAsync(Key key, TimeSpan duration, int capacity,
            Func<Task<CoinQuoteBatch>> fetch)
        {
            try
            {
                var batch = await fetch().ConfigureAwait(false);
                if (duration > TimeSpan.Zero && capacity > 0)
                {
                    lock (_gate)
                    {
                        while (!_entries.ContainsKey(key) && _entries.Count >= capacity)
                            _entries.Remove(_entries.MinBy(pair => pair.Value.ExpiresAt).Key);
                        _entries[key] = new Entry(batch, batch.RetrievedAt + duration);
                    }
                }
                return batch;
            }
            finally
            {
                lock (_gate)
                    _pending.Remove(key);
            }
        }

        internal void CheckCooldown(Provider provider)
        {
            lock (_gate)
            {
                if (!_cooldowns.TryGetValue(provider, out var until))
                    return;
                var remaining = until - clock.GetUtcNow();
                if (remaining > TimeSpan.Zero)
                    throw new CoinGeckoApiException(429, "CoinGecko is rate limiting requests.",
                        new RetryConditionHeaderValue(TimeSpan.FromSeconds(Math.Ceiling(remaining.TotalSeconds))));
                _cooldowns.Remove(provider);
            }
        }

        internal void RecordCooldown(Provider provider, TimeSpan delay)
        {
            lock (_gate)
            {
                var now = clock.GetUtcNow();
                foreach (var expired in _cooldowns.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToArray())
                    _cooldowns.Remove(expired);
                if (delay <= TimeSpan.Zero)
                    return;
                var until = delay >= DateTimeOffset.MaxValue - now ? DateTimeOffset.MaxValue : now + delay;
                if (until > _cooldowns.GetValueOrDefault(provider))
                    _cooldowns[provider] = until;
            }
        }

        internal TimeProvider Clock => clock;
        internal readonly record struct Provider(Uri BaseAddress, CoinGeckoPlan Plan, string CredentialHash);
        internal readonly record struct Key(Provider Provider, string Currency, string Ids);

        private sealed record Entry(CoinQuoteBatch Batch, DateTimeOffset ExpiresAt);
    }
}
