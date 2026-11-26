using System;
using System.Collections.Generic;
using System.Linq;

namespace CoinGlimpse.External.Crypto
{
    public sealed class CoinQuoteCache(TimeProvider clock)
    {
        private readonly object _gate = new();
        private readonly Dictionary<Key, Entry> _entries = new();

        internal CoinQuoteBatch Get(Key key)
        {
            lock (_gate)
            {
                if (!_entries.TryGetValue(key, out var entry))
                    return null;

                if (clock.GetUtcNow() < entry.ExpiresAt)
                    return entry.Batch;

                _entries.Remove(key);
                return null;
            }
        }

        internal void Store(Key key, CoinQuoteBatch batch, TimeSpan duration, int capacity)
        {
            lock (_gate)
            {
                while (!_entries.ContainsKey(key) && _entries.Count >= capacity)
                    _entries.Remove(_entries.MinBy(pair => pair.Value.ExpiresAt).Key);

                _entries[key] = new Entry(batch, batch.RetrievedAt + duration);
            }
        }

        internal DateTimeOffset GetUtcNow() => clock.GetUtcNow();

        internal readonly record struct Key(
            Uri BaseAddress, CoinGeckoPlan Plan, string CredentialHash, string Currency, string Ids);

        private sealed record Entry(CoinQuoteBatch Batch, DateTimeOffset ExpiresAt);
    }
}
