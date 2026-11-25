using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using CoinGlimpse.DataObjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoinGlimpse.External.Crypto
{
    public class CryptoManager : ICryptoManager
    {
        private const long MaxResponseSize = 10 * 1024 * 1024;
        private static readonly Random Jitter = new Random();

        private readonly HttpClient _httpClient;
        private readonly ILogger<CryptoManager> _logger;
        private readonly CoinGeckoOptions _options;
        private readonly SemaphoreSlim _listLock = new SemaphoreSlim(1, 1);
        private readonly ConcurrentDictionary<string, CacheEntry<List<CryptoListitem>>> _listCache =
            new ConcurrentDictionary<string, CacheEntry<List<CryptoListitem>>>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, CacheEntry<DataObjects.Crypto>> _coinCache =
            new ConcurrentDictionary<string, CacheEntry<DataObjects.Crypto>>(StringComparer.OrdinalIgnoreCase);

        public CryptoManager(
            HttpClient httpClient,
            ILogger<CryptoManager> logger,
            IOptions<CoinGeckoOptions> options)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

            ValidateBaseUrl();
            ConfigureHttpClient();
        }

        public async Task<List<CryptoListitem>> FetchList(
            CancellationToken cancellationToken = default)
        {
            var cacheKey = "coin-list";
            if (_listCache.TryGetValue(cacheKey, out var cacheEntry) &&
                cacheEntry.ExpiresAtUtc > DateTimeOffset.UtcNow)
            {
                _logger.LogDebug("Coin list returned from cache");
                return Copy(cacheEntry.Value);
            }

            await _listLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_listCache.TryGetValue(cacheKey, out cacheEntry) &&
                    cacheEntry.ExpiresAtUtc > DateTimeOffset.UtcNow)
                {
                    return Copy(cacheEntry.Value);
                }

                using var response = await SendWithRetryAsync(
                    "coins/list",
                    "coins/list",
                    cancellationToken).ConfigureAwait(false);

                var coins = await ReadJsonAsync<List<CoinListItem>>(
                    response,
                    "coins/list",
                    cancellationToken).ConfigureAwait(false);

                var result = coins
                    .Where(x => !string.IsNullOrWhiteSpace(x.Id))
                    .Select(x => new CryptoListitem
                    {
                        id = x.Id,
                        name = x.Name,
                        ticker = x.Symbol
                    })
                    .ToList();

                if (result.Count == 0)
                {
                    throw new InvalidOperationException("CoinGecko returned an empty coin list.");
                }

                var duration = _options.ListCacheDuration > TimeSpan.Zero
                    ? _options.ListCacheDuration
                    : TimeSpan.FromMinutes(10);
                _listCache[cacheKey] = new CacheEntry<List<CryptoListitem>>(
                    result,
                    DateTimeOffset.UtcNow.Add(duration));

                _logger.LogInformation(
                    "CoinGecko coin list loaded successfully. Count={Count}",
                    result.Count);

                return Copy(result);
            }
            finally
            {
                _listLock.Release();
            }
        }

        public async Task<DataObjects.Crypto> FetchCoin(
            string ticker,
            CancellationToken cancellationToken = default)
        {
            var input = ticker?.Trim();
            if (string.IsNullOrEmpty(input))
            {
                return new DataObjects.Crypto { msg = "Enter a coin ID or ticker." };
            }

            if (input.Length > 100)
            {
                return new DataObjects.Crypto
                {
                    msg = "Coin ID or ticker is too long."
                };
            }

            if (_coinCache.TryGetValue(input, out var cacheEntry) &&
                cacheEntry.ExpiresAtUtc > DateTimeOffset.UtcNow)
            {
                _logger.LogDebug("Coin detail returned from cache. Lookup={Lookup}", input);
                return Copy(cacheEntry.Value);
            }

            try
            {
                var coins = await FetchList(cancellationToken).ConfigureAwait(false);
                var result = coins.FirstOrDefault(
                    x => string.Equals(x.ticker, input, StringComparison.OrdinalIgnoreCase))
                    ?? coins.FirstOrDefault(
                        x => string.Equals(x.id, input, StringComparison.OrdinalIgnoreCase));

                if (result is null)
                {
                    return new DataObjects.Crypto
                    {
                        msg = $"\"{ticker}\" is not a valid coin id or ticker."
                    };
                }

                if (_coinCache.TryGetValue(result.id, out cacheEntry) &&
                    cacheEntry.ExpiresAtUtc > DateTimeOffset.UtcNow)
                {
                    return Copy(cacheEntry.Value);
                }

                var escapedId = Uri.EscapeDataString(result.id);
                var relativeUrl =
                    $"coins/{escapedId}?localization=false&tickers=false&market_data=true&community_data=false&developer_data=false&sparkline=false";

                using var response = await SendWithRetryAsync(
                    "coins/{id}",
                    relativeUrl,
                    cancellationToken).ConfigureAwait(false);

                var coinData = await ReadJsonAsync<CoinFullDataById>(
                    response,
                    "coins/{id}",
                    cancellationToken).ConfigureAwait(false);

                var coin = Convert(coinData);
                var duration = _options.CoinCacheDuration > TimeSpan.Zero
                    ? _options.CoinCacheDuration
                    : TimeSpan.FromSeconds(60);
                _coinCache[result.id] = new CacheEntry<DataObjects.Crypto>(
                    coin,
                    DateTimeOffset.UtcNow.Add(duration));

                return Copy(coin);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(
                    ex,
                    "CoinGecko request failed for a coin lookup");
                return new DataObjects.Crypto
                {
                    msg = "Unable to communicate with the CoinGecko API."
                };
            }
            catch (JsonException ex)
            {
                _logger.LogError(
                    ex,
                    "CoinGecko returned invalid JSON for a coin lookup");
                return new DataObjects.Crypto
                {
                    msg = "CoinGecko returned an invalid response."
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Unexpected error while fetching coin detail");
                return new DataObjects.Crypto
                {
                    msg = "Unable to retrieve coin data."
                };
            }
        }

        private async Task<HttpResponseMessage> SendWithRetryAsync(
            string endpoint,
            string relativeUrl,
            CancellationToken cancellationToken)
        {
            var retryAttempts = Math.Clamp(_options.MaxRetryAttempts, 0, 5);

            for (var attempt = 0; ; attempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                if (!string.IsNullOrWhiteSpace(_options.ApiKey))
                {
                    var headerName = _options.Plan == CoinGeckoPlan.Pro
                        ? "x-cg-pro-api-key"
                        : "x-cg-demo-api-key";
                    request.Headers.TryAddWithoutValidation(headerName, _options.ApiKey);
                }

                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                _logger.LogInformation(
                    "CoinGecko request started. Endpoint={Endpoint} Attempt={Attempt}",
                    endpoint,
                    attempt + 1);

                HttpResponseMessage response;
                try
                {
                    response = await _httpClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (HttpRequestException ex)
                {
                    stopwatch.Stop();
                    if (attempt >= retryAttempts)
                    {
                        _logger.LogError(
                            ex,
                            "CoinGecko request failed. Endpoint={Endpoint} Status=TransportError ElapsedMs={ElapsedMs}",
                            endpoint,
                            stopwatch.ElapsedMilliseconds);
                        throw;
                    }

                    await DelayBeforeRetryAsync(attempt, endpoint, null, cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                stopwatch.Stop();
                var statusCode = (int)response.StatusCode;

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation(
                        "CoinGecko request completed. Endpoint={Endpoint} Status={StatusCode} ElapsedMs={ElapsedMs}",
                        endpoint,
                        statusCode,
                        stopwatch.ElapsedMilliseconds);
                    return response;
                }

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    _logger.LogWarning(
                        "CoinGecko resource was not found. Endpoint={Endpoint} Status={StatusCode} ElapsedMs={ElapsedMs}",
                        endpoint,
                        statusCode,
                        stopwatch.ElapsedMilliseconds);
                    response.Dispose();
                    throw new CoinGeckoApiException(statusCode, "The requested CoinGecko resource was not found.");
                }

                var retryable =
                    response.StatusCode == (HttpStatusCode)429 ||
                    statusCode >= 500;

                if (retryable && attempt < retryAttempts)
                {
                    var retryAfter = response.Headers.RetryAfter?.Delta;
                    response.Dispose();
                    await DelayBeforeRetryAsync(
                        attempt,
                        endpoint,
                        retryAfter,
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }

                _logger.LogWarning(
                    "CoinGecko request returned an error. Endpoint={Endpoint} Status={StatusCode} ElapsedMs={ElapsedMs}",
                    endpoint,
                    statusCode,
                    stopwatch.ElapsedMilliseconds);
                response.Dispose();
                throw new CoinGeckoApiException(statusCode, "CoinGecko rejected the request.");
            }
        }

        private async Task DelayBeforeRetryAsync(
            int attempt,
            string endpoint,
            TimeSpan? retryAfter,
            CancellationToken cancellationToken)
        {
            var delay = retryAfter.HasValue
                ? retryAfter.Value
                : TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt));

            if (delay > TimeSpan.FromSeconds(30))
            {
                delay = TimeSpan.FromSeconds(30);
            }

            delay += TimeSpan.FromMilliseconds(Jitter.Next(0, 100));

            _logger.LogWarning(
                "Retrying CoinGecko request. Endpoint={Endpoint} Attempt={Attempt} DelayMs={DelayMs}",
                endpoint,
                attempt + 2,
                (int)delay.TotalMilliseconds);

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        private async Task<T> ReadJsonAsync<T>(
            HttpResponseMessage response,
            string endpoint,
            CancellationToken cancellationToken)
        {
            if (response.Content.Headers.ContentLength > MaxResponseSize)
            {
                _logger.LogError(
                    "CoinGecko response exceeded the configured size limit. Endpoint={Endpoint}",
                    endpoint);
                throw new InvalidOperationException("CoinGecko response was too large.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
            var value = await JsonSerializer.DeserializeAsync<T>(
                stream,
                options,
                cancellationToken).ConfigureAwait(false);

            if (value is null)
            {
                throw new JsonException("CoinGecko returned a null JSON value.");
            }

            return value;
        }

        private void ValidateBaseUrl()
        {
            if (!Uri.TryCreate(_options.BaseUrl, UriKind.Absolute, out var baseUri))
            {
                throw new InvalidOperationException("CoinGecko BaseUrl is invalid.");
            }

            var loopback =
                baseUri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                (IPAddress.TryParse(baseUri.Host, out var address) &&
                 IPAddress.IsLoopback(address));

            if (baseUri.Scheme != Uri.UriSchemeHttps && !loopback)
            {
                throw new InvalidOperationException(
                    "CoinGecko BaseUrl must use HTTPS unless it is a loopback address.");
            }
        }

        private void ConfigureHttpClient()
        {
            if (!Uri.TryCreate(_options.BaseUrl, UriKind.Absolute, out var baseUri))
            {
                throw new InvalidOperationException("CoinGecko BaseUrl is invalid.");
            }

            _httpClient.BaseAddress = baseUri;
            _httpClient.Timeout = TimeSpan.FromSeconds(30);
            _httpClient.DefaultRequestHeaders.Accept.Clear();
            _httpClient.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
            _httpClient.DefaultRequestHeaders.UserAgent.Clear();
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("CoinGlimpse/1.0");
        }

        private static DataObjects.Crypto Convert(CoinFullDataById coinData)
        {
            decimal? price = null;
            decimal? marketCap = null;
            decimal? volume = null;

            if (coinData.MarketData?.CurrentPrice?.TryGetValue("usd", out var currentPrice) == true)
            {
                price = currentPrice;
            }

            if (coinData.MarketData?.MarketCap?.TryGetValue("usd", out var currentMarketCap) == true)
            {
                marketCap = currentMarketCap;
            }

            if (coinData.MarketData?.TotalVolume?.TryGetValue("usd", out var currentVolume) == true)
            {
                volume = currentVolume;
            }

            var platformNames = coinData.Platforms == null
                ? string.Empty
                : string.Join(", ", coinData.Platforms.Keys);

            return new DataObjects.Crypto
            {
                Price = (double)(price ?? 0),
                MarketCap = (long)Math.Round(marketCap ?? 0, MidpointRounding.AwayFromZero),
                Volume = (long)Math.Round(volume ?? 0, MidpointRounding.AwayFromZero),
                ExchangeInfo = platformNames
            };
        }

        private static List<CryptoListitem> Copy(List<CryptoListitem> source)
        {
            return source.Select(x => new CryptoListitem
            {
                id = x.id,
                name = x.name,
                ticker = x.ticker
            }).ToList();
        }

        private static DataObjects.Crypto Copy(DataObjects.Crypto source)
        {
            return new DataObjects.Crypto
            {
                Price = source.Price,
                YrHigh = source.YrHigh,
                YrLow = source.YrLow,
                YrAverage = source.YrAverage,
                MovingAverageFifthy = source.MovingAverageFifthy,
                ExchangeInfo = source.ExchangeInfo,
                MarketCap = source.MarketCap,
                Volume = source.Volume,
                msg = source.msg
            };
        }

        private sealed class CacheEntry<T>
        {
            public CacheEntry(T value, DateTimeOffset expiresAtUtc)
            {
                Value = value;
                ExpiresAtUtc = expiresAtUtc;
            }

            public T Value { get; }
            public DateTimeOffset ExpiresAtUtc { get; }
        }

        private sealed class CoinListItem
        {
            [JsonPropertyName("id")]
            public string Id { get; set; }

            [JsonPropertyName("symbol")]
            public string Symbol { get; set; }

            [JsonPropertyName("name")]
            public string Name { get; set; }
        }

        private sealed class CoinFullDataById
        {
            [JsonPropertyName("market_data")]
            public CoinMarketData MarketData { get; set; }

            [JsonPropertyName("platforms")]
            public Dictionary<string, string> Platforms { get; set; }
        }

        private sealed class CoinMarketData
        {
            [JsonPropertyName("current_price")]
            public Dictionary<string, decimal> CurrentPrice { get; set; }

            [JsonPropertyName("market_cap")]
            public Dictionary<string, decimal> MarketCap { get; set; }

            [JsonPropertyName("total_volume")]
            public Dictionary<string, decimal> TotalVolume { get; set; }
        }
    }

    public sealed class CoinGeckoApiException : Exception
    {
        public CoinGeckoApiException(int statusCode, string message)
            : base(message)
        {
            StatusCode = statusCode;
        }

        public int StatusCode { get; }
    }
}
