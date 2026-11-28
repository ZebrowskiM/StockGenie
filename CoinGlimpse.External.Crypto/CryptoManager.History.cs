using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CoinGlimpse.External.Crypto
{
    public sealed record PriceObservation(DateTimeOffset Timestamp, decimal Price);

    public sealed record DailyPriceHistory(string CoinId, string Currency, DateTimeOffset RetrievedAt,
        IReadOnlyList<PriceObservation> Prices);

    public partial class CryptoManager
    {
        public async Task<DailyPriceHistory> GetDailyPriceHistoryAsync(string coinId, int days,
            string currency = "usd", CancellationToken cancellationToken = default)
        {
            if (days is < 1 or > 365)
                throw new ArgumentOutOfRangeException(nameof(days), "Request between 1 and 365 days.");
            var id = coinId?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(id) || id.Length > 100 ||
                id.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not ('-' or '_' or '.')) ||
                id is "." or "..")
                throw new ArgumentException("Provide a canonical coin ID.", nameof(coinId));
            var unit = currency?.Trim().ToLowerInvariant() ?? "usd";
            if (unit.Length is < 2 or > 10 || !unit.All(char.IsAsciiLetterOrDigit))
                throw new ArgumentException("Provide a 2 to 10 character alphanumeric currency code.", nameof(currency));

            const string endpoint = "coins/{id}/market_chart";
            var uri = $"coins/{Uri.EscapeDataString(id)}/market_chart?vs_currency={unit}" +
                $"&days={days.ToString(CultureInfo.InvariantCulture)}&interval=daily";
            using var response = await SendWithRetryAsync(endpoint, uri, cancellationToken).ConfigureAwait(false);
            var chart = await ReadJsonAsync<MarketChart>(response, endpoint, cancellationToken).ConfigureAwait(false);
            if (chart.Prices == null)
                throw new JsonException("Historical response is missing prices.");

            var observations = new List<PriceObservation>(chart.Prices.Length);
            foreach (var pair in chart.Prices)
            {
                if (pair is not { Length: 2 } || pair[0] != decimal.Truncate(pair[0]) ||
                    pair[0] is < -62135596800000m or > 253402300799999m)
                    throw new JsonException("Invalid historical price observation.");
                observations.Add(new PriceObservation(DateTimeOffset.FromUnixTimeMilliseconds((long)pair[0]), pair[1]));
            }
            observations.Sort((left, right) => left.Timestamp.CompareTo(right.Timestamp));
            return new DailyPriceHistory(id, unit.ToUpperInvariant(), _clock.GetUtcNow(), observations.AsReadOnly());
        }

        private sealed class MarketChart
        {
            public decimal[][] Prices { get; set; }
        }
    }
}
