using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CoinGlimpse.External.Crypto
{
    public sealed record CoinQuote(string CoinId, decimal? Price, DateTimeOffset? ProviderUpdatedAt)
    {
        public bool? IsStale { get; init; }
    }

    public sealed record CoinQuoteBatch(
        string Currency, DateTimeOffset RetrievedAt, IReadOnlyList<CoinQuote> Quotes);

    public interface IQuoteService
    {
        Task<CoinQuoteBatch> GetQuotesAsync(IEnumerable<string> ids, string currency = "usd",
            CancellationToken cancellationToken = default);
    }

    public partial class CryptoManager : IQuoteService
    {
        private const string QuoteEndpoint = "simple/price";
        private static readonly Meter WatchlistMeter = new("CoinGlimpse.Watchlist");
        private static readonly Counter<long> UpstreamRequests = WatchlistMeter.CreateCounter<long>("watchlist.upstream.requests");
        private static readonly Counter<long> CacheHits = WatchlistMeter.CreateCounter<long>("watchlist.cache.hits");
        private static readonly Counter<long> RateLimits = WatchlistMeter.CreateCounter<long>("watchlist.upstream.rate_limits");
        private static readonly TimeSpan QuoteRequestBudget = TimeSpan.FromSeconds(10);
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
            var provider = new CoinQuoteCache.Provider(_httpClient.BaseAddress, plan, ConvertCredentialToHash(apiKey));
            var key = new CoinQuoteCache.Key(provider, normalizedCurrency, joinedIds);
            var pending = _quoteCache.GetOrFetch(key, _options.QuoteCacheDuration, _options.QuoteCacheCapacity,
                () => FetchQuotesAsync(key, normalizedIds, apiKey), out var hit);
            if (hit)
                CacheHits.Add(1);
            var batch = await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
            return WithFreshness(batch, _clock.GetUtcNow());
        }

        private async Task<CoinQuoteBatch> FetchQuotesAsync(CoinQuoteCache.Key key, string[] normalizedIds, string apiKey)
        {
            var normalizedCurrency = key.Currency;
            var joinedIds = key.Ids;
            var query = $"ids={Uri.EscapeDataString(joinedIds)}" +
                $"&vs_currencies={normalizedCurrency}&include_last_updated_at=true";
            using var requestTimeout = new CancellationTokenSource(QuoteRequestBudget, _clock);
            var startedAt = Stopwatch.GetTimestamp();
            using var response = await SendWithRetryAsync(QuoteEndpoint, $"{QuoteEndpoint}?{query}",
                requestTimeout.Token, (key.Provider.Plan, apiKey), (startedAt, QuoteRequestBudget), key.Provider).ConfigureAwait(false);
            var rows = await ReadJsonAsync<Dictionary<string, Dictionary<string, decimal?>>>(
                response, QuoteEndpoint, requestTimeout.Token).ConfigureAwait(false);
            var retrievedAt = _clock.GetUtcNow();

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
            return new CoinQuoteBatch(normalizedCurrency.ToUpperInvariant(), retrievedAt, Array.AsReadOnly(quotes));
        }

        private CoinQuoteBatch WithFreshness(CoinQuoteBatch batch, DateTimeOffset now)
        {
            var quotes = batch.Quotes.Select(quote => quote with
            {
                IsStale = quote.Price.HasValue && quote.ProviderUpdatedAt is { } updated && updated <= now
                    ? now - updated >= _options.QuoteMaxAge
                    : null
            }).ToArray();
            return batch with { Quotes = Array.AsReadOnly(quotes) };
        }

        private static string ConvertCredentialToHash(string apiKey) =>
            string.IsNullOrWhiteSpace(apiKey) ? string.Empty :
                System.Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)));
    }
}
