using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    public partial class CryptoManager : ICryptoManager
    {
        private const long MaxResponseSize = 10 * 1024 * 1024;
        private static readonly Random Jitter = new Random();

        private readonly HttpClient _httpClient;
        private readonly ILogger<CryptoManager> _logger;
        private readonly CoinGeckoOptions _options;
        private readonly TimeProvider _clock;
        private readonly CoinCatalogCache _catalog;

        public CryptoManager(
            HttpClient httpClient,
            ILogger<CryptoManager> logger,
            IOptions<CoinGeckoOptions> options,
            CoinCatalogCache catalog = null,
            CoinQuoteCache quoteCache = null,
            TimeProvider clock = null)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

            _clock = clock ?? quoteCache?.Clock ?? TimeProvider.System;
            _catalog = catalog ?? new CoinCatalogCache(_clock);
            _quoteCache = quoteCache ?? new CoinQuoteCache(_clock);
            ValidateBaseUrl();
            ConfigureHttpClient();
        }

        public async Task<List<CryptoListitem>> FetchList(CancellationToken cancellationToken = default) =>
            (await FetchCatalog(cancellationToken).ConfigureAwait(false)).Coins;

        public Task<CoinCatalogRead> FetchCatalog(CancellationToken cancellationToken = default) =>
            _catalog.GetAsync(LoadCatalogAsync, _options, cancellationToken);

        private async Task<List<CryptoListitem>> LoadCatalogAsync(CancellationToken cancellationToken)
        {
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

            if (_options.EnableRequestLogging && _logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "CoinGecko coin list loaded successfully. Count={Count}",
                    result.Count);
            }

            return result;
        }

        public Task<DataObjects.Crypto> FetchCoin(
            string ticker,
            CancellationToken cancellationToken = default)
        {
            return FetchCoin(ticker, "usd", cancellationToken);
        }

        public async Task<DataObjects.Crypto> FetchCoin(
            string ticker,
            string currency,
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

            var requestedCurrency = string.IsNullOrWhiteSpace(currency)
                ? "usd"
                : currency.Trim().ToLowerInvariant();
            if (requestedCurrency.Length < 2 ||
                requestedCurrency.Length > 10 ||
                requestedCurrency.Any(character =>
                    (character < 'a' || character > 'z') &&
                    (character < '0' || character > '9')))
            {
                return new DataObjects.Crypto
                {
                    msg = "Enter a valid currency code."
                };
            }

            try
            {
                var coins = await FetchList(cancellationToken).ConfigureAwait(false);
                var result = coins.FirstOrDefault(
                    x => string.Equals(x.id, input, StringComparison.OrdinalIgnoreCase));
                if (result is null)
                {
                    var matches = coins
                        .Where(x => string.Equals(x.ticker, input, StringComparison.OrdinalIgnoreCase))
                        .OrderBy(x => x.id, StringComparer.Ordinal)
                        .DistinctBy(x => x.id, StringComparer.OrdinalIgnoreCase)
                        .ToArray();

                    if (matches.Length > 1)
                    {
                        return new DataObjects.Crypto
                        {
                            msg = "Multiple coins use this ticker. Select a coin ID.",
                            CandidateIds = matches.Select(x => x.id).ToArray()
                        };
                    }

                    result = matches.FirstOrDefault();
                }

                if (result is null)
                {
                    return new DataObjects.Crypto
                    {
                        LookupError = CoinLookupError.UnknownCoin,
                        msg = $"\"{ticker}\" is not a valid coin id or ticker."
                    };
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

                return Convert(coinData, requestedCurrency);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(
                    ex,
                    "CoinGecko request failed. Endpoint={Endpoint} ErrorType={ErrorType}",
                    "coins/{id}",
                    ex.GetType().Name);
                return new DataObjects.Crypto
                {
                    LookupError = CoinLookupError.ProviderUnavailable,
                    msg = "Unable to communicate with the CoinGecko API."
                };
            }
            catch (JsonException ex)
            {
                _logger.LogError(
                    ex,
                    "CoinGecko returned invalid JSON. Endpoint={Endpoint} ErrorType={ErrorType}",
                    "coins/{id}",
                    ex.GetType().Name);
                return new DataObjects.Crypto
                {
                    LookupError = CoinLookupError.ProviderUnavailable,
                    msg = "CoinGecko returned an invalid response."
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Unexpected error while fetching coin detail. Endpoint={Endpoint} ErrorType={ErrorType}",
                    "coins/{id}",
                    ex.GetType().Name);
                return new DataObjects.Crypto
                {
                    LookupError = CoinLookupError.ProviderUnavailable,
                    msg = "Unable to retrieve coin data."
                };
            }
        }

        private async Task<HttpResponseMessage> SendWithRetryAsync(
            string endpoint,
            string relativeUrl,
            CancellationToken cancellationToken,
            (CoinGeckoPlan Plan, string ApiKey)? credentials = null,
            (long StartedAt, TimeSpan Duration)? requestBudget = null,
            CoinQuoteCache.Provider? quoteProvider = null)
        {
            var (plan, apiKey) = credentials ?? (_options.Plan, _options.ApiKey);
            var retryAttempts = Math.Clamp(_options.MaxRetryAttempts, 0, 5);

            for (var attempt = 0; ; attempt++)
            {
                if (quoteProvider is { } provider)
                    _quoteCache.CheckCooldown(provider);
                using var request = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    var headerName = plan == CoinGeckoPlan.Pro
                        ? "x-cg-pro-api-key"
                        : "x-cg-demo-api-key";
                    request.Headers.TryAddWithoutValidation(headerName, apiKey);
                }

                var requestLoggingEnabled = _options.EnableRequestLogging;
                var requestStartedAt = requestLoggingEnabled
                    ? System.Diagnostics.Stopwatch.GetTimestamp()
                    : 0;
                if (requestLoggingEnabled && _logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation(
                        "CoinGecko request started. Method={Method} Endpoint={Endpoint} Attempt={Attempt}",
                        request.Method,
                        endpoint,
                        attempt + 1);
                }

                HttpResponseMessage response;
                try
                {
                    if (endpoint == QuoteEndpoint)
                        UpstreamRequests.Add(1);
                    response = await _httpClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken).ConfigureAwait(false);
                    if (endpoint == QuoteEndpoint && (int)response.StatusCode == 429)
                        RateLimits.Add(1);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (HttpRequestException ex)
                {
                    if (attempt >= retryAttempts)
                    {
                        if (requestLoggingEnabled)
                        {
                            _logger.LogError(
                                ex,
                                "CoinGecko request failed. Endpoint={Endpoint} Status=TransportError ElapsedMs={ElapsedMs}",
                                endpoint,
                                System.Diagnostics.Stopwatch.GetElapsedTime(requestStartedAt).TotalMilliseconds);
                        }
                        else
                        {
                            _logger.LogError(
                                ex,
                                "CoinGecko request failed. Endpoint={Endpoint} Status=TransportError",
                                endpoint);
                        }

                        throw;
                    }

                    await DelayBeforeRetryAsync(
                            attempt,
                            endpoint,
                            null,
                            cancellationToken,
                            null,
                            "TransportError")
                        .ConfigureAwait(false);
                    continue;
                }

                var statusCode = (int)response.StatusCode;

                if (response.IsSuccessStatusCode)
                {
                    if (requestLoggingEnabled && _logger.IsEnabled(LogLevel.Information))
                    {
                        _logger.LogInformation(
                            "CoinGecko request completed. Endpoint={Endpoint} Status={StatusCode} ElapsedMs={ElapsedMs} ContentLengthBytes={ContentLengthBytes}",
                            endpoint,
                            statusCode,
                            System.Diagnostics.Stopwatch.GetElapsedTime(requestStartedAt).TotalMilliseconds,
                            response.Content.Headers.ContentLength);
                    }

                    return response;
                }

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    if (requestLoggingEnabled)
                    {
                        _logger.LogWarning(
                            "CoinGecko resource was not found. Endpoint={Endpoint} Status={StatusCode} ElapsedMs={ElapsedMs}",
                            endpoint,
                            statusCode,
                            System.Diagnostics.Stopwatch.GetElapsedTime(requestStartedAt).TotalMilliseconds);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "CoinGecko resource was not found. Endpoint={Endpoint} Status={StatusCode}",
                            endpoint,
                            statusCode);
                    }

                    response.Dispose();
                    throw new CoinGeckoApiException(statusCode, "The requested CoinGecko resource was not found.");
                }

                var retryable =
                    response.StatusCode == (HttpStatusCode)429 ||
                    statusCode >= 500;

                var retryAfter = response.Headers.RetryAfter;
                TimeSpan? providerDelay = retryAfter?.Delta;
                if (quoteProvider.HasValue && retryAfter?.Date is DateTimeOffset retryDate)
                    providerDelay = retryDate - _clock.GetUtcNow();
                if (statusCode == 429 && quoteProvider is { } limitedProvider)
                {
                    providerDelay ??= TimeSpan.FromSeconds(1);
                    _quoteCache.RecordCooldown(limitedProvider, providerDelay.Value);
                    retryAfter ??= new RetryConditionHeaderValue(providerDelay.Value);
                }
                if (retryable && attempt < retryAttempts)
                {
                    var delay = GetRetryDelay(attempt, providerDelay, capDelay: !requestBudget.HasValue);
                    if (requestBudget is not { } budget ||
                        delay < budget.Duration - TimeSpan.FromSeconds(1) - Stopwatch.GetElapsedTime(budget.StartedAt))
                    {
                        response.Dispose();
                        await WaitBeforeRetryAsync(attempt, endpoint, delay, cancellationToken,
                            statusCode, "RetryableHttpStatus").ConfigureAwait(false);
                        continue;
                    }
                }

                if (requestLoggingEnabled)
                {
                    _logger.LogWarning(
                        "CoinGecko request returned an error. Endpoint={Endpoint} Status={StatusCode} ElapsedMs={ElapsedMs}",
                        endpoint,
                        statusCode,
                        System.Diagnostics.Stopwatch.GetElapsedTime(requestStartedAt).TotalMilliseconds);
                }
                else
                {
                    _logger.LogWarning(
                        "CoinGecko request returned an error. Endpoint={Endpoint} Status={StatusCode}",
                        endpoint,
                        statusCode);
                }

                response.Dispose();
                throw new CoinGeckoApiException(statusCode, "CoinGecko rejected the request.", retryAfter);
            }
        }

        private Task DelayBeforeRetryAsync(
            int attempt,
            string endpoint,
            TimeSpan? retryAfter,
            CancellationToken cancellationToken,
            int? statusCode,
            string reason) =>
            WaitBeforeRetryAsync(attempt, endpoint, GetRetryDelay(attempt, retryAfter, capDelay: true),
                cancellationToken, statusCode, reason);

        private static TimeSpan GetRetryDelay(int attempt, TimeSpan? retryAfter, bool capDelay)
        {
            var delay = retryAfter ?? TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt));
            if (delay < TimeSpan.Zero)
                delay = TimeSpan.Zero;
            if (capDelay && delay > TimeSpan.FromSeconds(30))
                delay = TimeSpan.FromSeconds(30);

            return delay + TimeSpan.FromMilliseconds(Jitter.Next(0, 100));
        }

        private async Task WaitBeforeRetryAsync(
            int attempt, string endpoint, TimeSpan delay, CancellationToken cancellationToken,
            int? statusCode, string reason)
        {
            _logger.LogWarning(
                "Retrying CoinGecko request. Endpoint={Endpoint} Attempt={Attempt} Status={StatusCode} Reason={Reason} DelayMs={DelayMs}",
                endpoint,
                attempt + 2,
                statusCode,
                reason,
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

        private static DataObjects.Crypto Convert(
            CoinFullDataById coinData,
            string currency)
        {
            decimal? marketCap = null;
            decimal? volume = null;

            if (coinData.MarketData?.CurrentPrice?.TryGetValue(currency, out var currentPrice) != true)
            {
                return new DataObjects.Crypto
                {
                    msg = $"Price data is unavailable for currency '{currency.ToUpperInvariant()}'."
                };
            }

            if (coinData.MarketData?.MarketCap?.TryGetValue(currency, out var currentMarketCap) == true)
            {
                marketCap = currentMarketCap;
            }

            if (coinData.MarketData?.TotalVolume?.TryGetValue(currency, out var currentVolume) == true)
            {
                volume = currentVolume;
            }

            var platformNames = coinData.Platforms == null
                ? string.Empty
                : string.Join(", ", coinData.Platforms.Keys);

            return new DataObjects.Crypto
            {
                Price = currentPrice,
                Currency = currency.ToUpperInvariant(),
                MarketCap = (long)Math.Round(marketCap ?? 0, MidpointRounding.AwayFromZero),
                Volume = (long)Math.Round(volume ?? 0, MidpointRounding.AwayFromZero),
                ExchangeInfo = platformNames
            };
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

        internal CoinGeckoApiException(int statusCode, string message, RetryConditionHeaderValue retryAfter)
            : this(statusCode, message)
        {
            RetryAfter = retryAfter;
        }

        public int StatusCode { get; }
        public RetryConditionHeaderValue RetryAfter { get; }
    }
}
