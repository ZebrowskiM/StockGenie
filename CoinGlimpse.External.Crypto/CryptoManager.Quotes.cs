using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CoinGlimpse.External.Crypto
{
    public sealed record CoinQuote(string CoinId, decimal? Price, DateTimeOffset? ProviderUpdatedAt);

    public sealed record CoinQuoteBatch(
        string Currency, DateTimeOffset RetrievedAt, IReadOnlyList<CoinQuote> Quotes);

    public partial class CryptoManager
    {
        private readonly CoinQuoteCache _quoteCache;

        public const int MaxQuoteIds = 50;

        public async Task<CoinQuoteBatch> GetQuotesAsync(
            IEnumerable<string> ids, string currency = "usd", CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(ids);
            cancellationToken.ThrowIfCancellationRequested();

            var requestedIds = ids.Take(MaxQuoteIds + 1)
                .Select(id => id?.Trim().ToLowerInvariant()).ToArray();
            if (requestedIds.Length is 0 or > MaxQuoteIds ||
                requestedIds.Any(id => string.IsNullOrEmpty(id) || id.Length > 100 ||
                    id.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not ('-' or '_' or '.'))))
            {
                throw new ArgumentException("Provide 1 to 50 canonical coin IDs of at most 100 characters each.", nameof(ids));
            }

            var normalizedCurrency = currency?.Trim().ToLowerInvariant() ?? "usd";
            if (normalizedCurrency.Length is < 2 or > 10 || !normalizedCurrency.All(char.IsAsciiLetterOrDigit))
                throw new ArgumentException("Provide a 2 to 10 character alphanumeric currency code.", nameof(currency));

            var normalizedIds = requestedIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var joinedIds = string.Join(",", normalizedIds);
            var plan = _options.Plan;
            var apiKey = _options.ApiKey;
            var duration = _options.QuoteCacheDuration;
            var capacity = _options.QuoteCacheCapacity;
            var cacheEnabled = duration > TimeSpan.Zero && capacity > 0;
            var key = cacheEnabled
                ? new CoinQuoteCache.Key(_httpClient.BaseAddress, plan,
                    ConvertCredentialToHash(apiKey), normalizedCurrency, joinedIds)
                : default;
            if (cacheEnabled && _quoteCache.Get(key) is { } cached)
                return cached;

            const string endpoint = "simple/price";
            var query = $"ids={Uri.EscapeDataString(joinedIds)}" +
                $"&vs_currencies={normalizedCurrency}&include_last_updated_at=true";
            using var response = await SendWithRetryAsync(endpoint, $"{endpoint}?{query}", cancellationToken, (plan, apiKey))
                .ConfigureAwait(false);
            var rows = await ReadJsonAsync<Dictionary<string, Dictionary<string, decimal?>>>(
                response, endpoint, cancellationToken).ConfigureAwait(false);
            var retrievedAt = _quoteCache.GetUtcNow();

            var quotes = normalizedIds.Select(id =>
            {
                rows.TryGetValue(id, out var row);
                var price = row?.GetValueOrDefault(normalizedCurrency);
                var timestamp = row?.GetValueOrDefault("last_updated_at");
                var updatedAt = timestamp is >= 0 and <= 253402300799m && timestamp == decimal.Truncate(timestamp.Value)
                    ? DateTimeOffset.FromUnixTimeSeconds((long)timestamp.Value)
                    : (DateTimeOffset?)null;
                return new CoinQuote(id, price, updatedAt);
            }).ToArray();
            var batch = new CoinQuoteBatch(normalizedCurrency.ToUpperInvariant(), retrievedAt, Array.AsReadOnly(quotes));
            if (cacheEnabled)
                _quoteCache.Store(key, batch, duration, capacity);

            return batch;
        }

        private static string ConvertCredentialToHash(string apiKey) =>
            string.IsNullOrWhiteSpace(apiKey) ? string.Empty :
                System.Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)));
    }
}
