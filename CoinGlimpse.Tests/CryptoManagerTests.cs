using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CoinGlimpse.External.Crypto;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class CryptoManagerTests
{
    [TestMethod]
    public async Task FetchList_ParsesResponseAndAddsDemoApiKey()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            Assert.AreEqual("/api/v3/coins/list", request.RequestUri.AbsolutePath);
            Assert.AreEqual("test-key", request.Headers.GetValues("x-cg-demo-api-key")
                .FirstOrDefault());

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "[{\"id\":\"bitcoin\",\"symbol\":\"btc\",\"name\":\"Bitcoin\"}]",
                    Encoding.UTF8,
                    "application/json")
            };
        });
        var manager = CreateManager(handler, CoinGeckoPlan.Demo);

        var result = await manager.FetchList();

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("bitcoin", result[0].id);
        Assert.AreEqual("btc", result[0].ticker);
    }

    [TestMethod]
    public async Task FetchCoin_ResolvesTickerAndParsesMarketData()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri.AbsolutePath.EndsWith("/coins/list",
                StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "[{\"id\":\"bitcoin\",\"symbol\":\"btc\",\"name\":\"Bitcoin\"}]",
                        Encoding.UTF8,
                        "application/json")
                };
            }

            Assert.AreEqual("/api/v3/coins/bitcoin", request.RequestUri.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"market_data\":{\"current_price\":{\"usd\":50000.25},\"market_cap\":{\"usd\":990000000000},\"total_volume\":{\"usd\":25000000000}},\"platforms\":{\"ethereum\":\"0x123\"}}",
                    Encoding.UTF8,
                    "application/json")
            };
        });
        var manager = CreateManager(handler, CoinGeckoPlan.Pro);

        var result = await manager.FetchCoin("BTC");

        Assert.IsNull(result.msg);
        Assert.AreEqual(50000.25m, result.Price);
        Assert.AreEqual("USD", result.Currency);
        Assert.AreEqual(990000000000L, result.MarketCap);
        Assert.AreEqual(25000000000L, result.Volume);
        Assert.AreEqual("ethereum", result.ExchangeInfo);
    }

    [TestMethod]
    public async Task FetchCoin_UsesRequestedCurrencyForAllMarketValues()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri.AbsolutePath.EndsWith("/coins/list",
                StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "[{\"id\":\"bitcoin\",\"symbol\":\"btc\",\"name\":\"Bitcoin\"}]",
                        Encoding.UTF8,
                        "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"market_data\":{\"current_price\":{\"usd\":50000.25,\"eur\":46000.5},\"market_cap\":{\"usd\":990000000000,\"eur\":910000000000},\"total_volume\":{\"usd\":25000000000,\"eur\":23000000000}},\"platforms\":{}}",
                    Encoding.UTF8,
                    "application/json")
            };
        });
        var manager = CreateManager(handler, CoinGeckoPlan.Demo);

        var result = await manager.FetchCoin("btc", "EUR");

        Assert.IsNull(result.msg);
        Assert.AreEqual("EUR", result.Currency);
        Assert.AreEqual(46000.5m, result.Price);
        Assert.AreEqual(910000000000L, result.MarketCap);
        Assert.AreEqual(23000000000L, result.Volume);
    }

    [TestMethod]
    public async Task FetchCoin_SerializesSmallPriceAsPlainDecimal()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri.AbsolutePath.EndsWith("/coins/list",
                StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "[{\"id\":\"small-coin\",\"symbol\":\"sml\",\"name\":\"Small Coin\"}]",
                        Encoding.UTF8,
                        "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"market_data\":{\"current_price\":{\"usd\":0.00001001}},\"platforms\":{}}",
                    Encoding.UTF8,
                    "application/json")
            };
        });
        var manager = CreateManager(handler, CoinGeckoPlan.Demo);

        var result = await manager.FetchCoin("sml");
        var json = JsonSerializer.Serialize(
            result,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.AreEqual(0.00001001m, result.Price);
        StringAssert.Contains(json, "\"price\":0.00001001");
        Assert.IsFalse(json.Contains("E-"));
    }

    [TestMethod]
    public async Task FetchCoin_ReturnsErrorWhenCurrencyIsUnavailable()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri.AbsolutePath.EndsWith("/coins/list",
                StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "[{\"id\":\"bitcoin\",\"symbol\":\"btc\",\"name\":\"Bitcoin\"}]",
                        Encoding.UTF8,
                        "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"market_data\":{\"current_price\":{\"usd\":50000.25}},\"platforms\":{}}",
                    Encoding.UTF8,
                    "application/json")
            };
        });
        var manager = CreateManager(handler, CoinGeckoPlan.Demo);

        var result = await manager.FetchCoin("btc", "eur");

        Assert.AreEqual("Price data is unavailable for currency 'EUR'.", result.msg);
    }

    [TestMethod]
    public async Task FetchCoin_RejectsMalformedCurrencyCode()
    {
        var requestCount = 0;
        var handler = new StubHttpMessageHandler(_ =>
        {
            requestCount++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json")
            };
        });
        var manager = CreateManager(handler, CoinGeckoPlan.Demo);

        var result = await manager.FetchCoin("btc", "usd&apiKey=secret");

        Assert.AreEqual("Enter a valid currency code.", result.msg);
        Assert.AreEqual(0, requestCount);
    }

    [TestMethod]
    public async Task FetchCoin_RequestsFreshCoinDataForEachLookup()
    {
        var coinDetailRequests = 0;
        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri.AbsolutePath.EndsWith("/coins/list",
                StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "[{\"id\":\"bitcoin\",\"symbol\":\"btc\",\"name\":\"Bitcoin\"}]",
                        Encoding.UTF8,
                        "application/json")
                };
            }

            coinDetailRequests++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"market_data\":{\"current_price\":{\"usd\":50000.25},\"market_cap\":{\"usd\":990000000000},\"total_volume\":{\"usd\":25000000000}},\"platforms\":{}}",
                    Encoding.UTF8,
                    "application/json")
            };
        });
        var manager = CreateManager(handler, CoinGeckoPlan.Demo);

        await manager.FetchCoin("btc");
        await manager.FetchCoin("btc");

        Assert.AreEqual(2, coinDetailRequests);
    }

    [TestMethod]
    public async Task FetchList_RoutineLoggingCanBeEnabledOrDisabled()
    {
        var disabledLogger = new CapturingLogger<CryptoManager>();
        var disabledManager = CreateManager(
            CreateCoinListHandler(),
            CoinGeckoPlan.Demo,
            disabledLogger);

        await disabledManager.FetchList();

        Assert.AreEqual(0, disabledLogger.Entries.Count);

        var enabledLogger = new CapturingLogger<CryptoManager>();
        var enabledManager = CreateManager(
            CreateCoinListHandler(),
            CoinGeckoPlan.Demo,
            enabledLogger,
            enableRequestLogging: true);

        await enabledManager.FetchList();

        Assert.IsTrue(enabledLogger.Entries.Any(
            entry => entry.Message.Contains("request started")));
        Assert.IsTrue(enabledLogger.Entries.Any(
            entry => entry.Message.Contains("request completed")));
        Assert.IsTrue(enabledLogger.Entries.Any(
            entry => entry.Message.Contains("coin list loaded successfully")));
    }

    [TestMethod]
    public async Task FetchCoin_LogsErrorsWhenRoutineLoggingIsDisabled()
    {
        var logger = new CapturingLogger<CryptoManager>();
        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri.AbsolutePath.EndsWith("/coins/list",
                StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "[{\"id\":\"bitcoin\",\"symbol\":\"btc\",\"name\":\"Bitcoin\"}]",
                        Encoding.UTF8,
                        "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var manager = CreateManager(handler, CoinGeckoPlan.Demo, logger);

        await manager.FetchCoin("btc");

        Assert.IsTrue(logger.Entries.Any(entry => entry.Level == LogLevel.Error));
    }

    [TestMethod]
    public void Constructor_RejectsPlainHttpBaseUrl()
    {
        var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK));

        Assert.ThrowsException<InvalidOperationException>(() => new CryptoManager(
            new HttpClient(handler),
            NullLogger<CryptoManager>.Instance,
            Options.Create(new CoinGeckoOptions
            {
                BaseUrl = "http://example.com/api/v3/",
                ApiKey = "test-key"
            })));
    }

    [TestMethod]
    public async Task GetQuotesAsync_BatchesTenIdsAndPreservesMissingPrices()
    {
        var calls = 0;
        using var handler = new StubHttpMessageHandler(request =>
        {
            calls++;
            Assert.AreEqual("/api/v3/simple/price", request.RequestUri.AbsolutePath);
            var query = Uri.UnescapeDataString(request.RequestUri.Query);
            StringAssert.Contains(query, "ids=coin-0,coin-1,coin-2,coin-3,coin-4,coin-5,coin-6,coin-7,coin-8,coin-9&");
            StringAssert.Contains(query, "vs_currencies=eur&include_last_updated_at=true");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"coin-0":{"eur":0.00001001,"last_updated_at":1700000000},"coin-1":{"eur":null}}""")
            };
        });
        var manager = CreateManager(handler, CoinGeckoPlan.Demo);
        var ids = Enumerable.Range(0, 10).Reverse().Select(i => $"coin-{i}").Append(" COIN-0 ");

        var result = await manager.GetQuotesAsync(ids, " EUR ");

        Assert.AreEqual(1, calls);
        Assert.AreEqual("EUR", result.Currency);
        Assert.AreEqual(10, result.Quotes.Count);
        Assert.AreEqual(0.00001001m, result.Quotes[0].Price);
        Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(1700000000), result.Quotes[0].ProviderUpdatedAt);
        Assert.IsTrue(result.RetrievedAt > result.Quotes[0].ProviderUpdatedAt);
        Assert.IsNull(result.Quotes[1].Price);
        Assert.IsNull(result.Quotes[9].ProviderUpdatedAt);
        Assert.AreEqual("coin-9", result.Quotes[9].CoinId);
    }

    [TestMethod]
    public async Task GetQuotesAsync_RejectsMalformedOrOversizedInputBeforeCallingUpstream()
    {
        using var handler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("Unexpected request."));
        var manager = CreateManager(handler, CoinGeckoPlan.Demo);

        await Assert.ThrowsExceptionAsync<ArgumentException>(() => manager.GetQuotesAsync(new[] { "bitcoin,ethereum" }));
        await Assert.ThrowsExceptionAsync<ArgumentException>(() => manager.GetQuotesAsync(new[] { "bitcoin" }, "usd,eur"));
        await Assert.ThrowsExceptionAsync<ArgumentException>(() =>
            manager.GetQuotesAsync(Enumerable.Repeat("bitcoin", CryptoManager.MaxQuoteIds + 1)));
    }

    [TestMethod]
    public async Task GetQuotesAsync_SharesEquivalentReadsAndExpiresAtConfiguredDuration()
    {
        var clock = new QuoteClock();
        var cache = new CoinQuoteCache(clock);
        var calls = 0;
        using var handler = new StubHttpMessageHandler(request =>
        {
            calls++;
            var json = request.RequestUri.Query.Contains("vs_currencies=eur")
                ? """{"bitcoin":{"eur":2}}""" : """{"bitcoin":{"usd":1}}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        });
        var options = new CoinGeckoOptions { ApiKey = "fixture", QuoteCacheDuration = TimeSpan.FromSeconds(10) };
        CryptoManager Manager() => new(new HttpClient(handler, disposeHandler: false),
            NullLogger<CryptoManager>.Instance, Options.Create(options), quoteCache: cache);

        var first = await Manager().GetQuotesAsync(new[] { "ethereum", " BITCOIN " });
        clock.Now += TimeSpan.FromSeconds(9);
        var repeat = await Manager().GetQuotesAsync(new[] { "bitcoin", "ethereum", "bitcoin" }, " USD ");
        Assert.AreEqual(1, calls);
        Assert.AreEqual(first.RetrievedAt, repeat.RetrievedAt);
        var euros = await Manager().GetQuotesAsync(new[] { "bitcoin", "ethereum" }, "eur");
        Assert.AreEqual(2m, euros.Quotes[0].Price);
        Assert.AreEqual(2, calls);
        clock.Now += TimeSpan.FromSeconds(1);
        var refreshed = await Manager().GetQuotesAsync(new[] { "bitcoin", "ethereum" });
        Assert.AreEqual(3, calls);
        Assert.AreEqual(clock.Now, refreshed.RetrievedAt);
    }

    [TestMethod]
    public async Task GetQuotesAsync_IsolatesCredentialsAndProvidersAndBoundsCapacity()
    {
        var cache = new CoinQuoteCache(TimeProvider.System);
        var calls = 0;
        using var handler = new StubHttpMessageHandler(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"bitcoin":{"usd":1}}""")
            };
        });
        using var firstClient = new HttpClient(handler, disposeHandler: false);
        using var secondClient = new HttpClient(handler, disposeHandler: false);
        using var thirdClient = new HttpClient(handler, disposeHandler: false);
        CryptoManager Manager(HttpClient client, string apiKey, string baseUrl) =>
            new(client, NullLogger<CryptoManager>.Instance, Options.Create(new CoinGeckoOptions
            {
                ApiKey = apiKey,
                BaseUrl = baseUrl,
                QuoteCacheCapacity = 1
            }), quoteCache: cache);

        const string baseUrl = "https://api.coingecko.com/api/v3/";
        var first = Manager(firstClient, "first", baseUrl);
        var second = Manager(secondClient, "second", baseUrl);
        var third = Manager(thirdClient, "second", "https://example.com/api/v3/");
        var ids = new[] { "bitcoin" };
        await first.GetQuotesAsync(ids);
        await second.GetQuotesAsync(ids);
        await third.GetQuotesAsync(ids);
        await third.GetQuotesAsync(ids);
        Assert.AreEqual(3, calls);
        await first.GetQuotesAsync(ids);
        Assert.AreEqual(4, calls);
    }

    private sealed class QuoteClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static CryptoManager CreateManager(
        HttpMessageHandler handler,
        CoinGeckoPlan plan,
        ILogger<CryptoManager> logger = null,
        bool enableRequestLogging = false)
    {
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.coingecko.com/api/v3/")
        };

        return new CryptoManager(
            httpClient,
            logger ?? NullLogger<CryptoManager>.Instance,
            Options.Create(new CoinGeckoOptions
            {
                ApiKey = "test-key",
                BaseUrl = "https://api.coingecko.com/api/v3/",
                Plan = plan,
                EnableRequestLogging = enableRequestLogging
            }));
    }

    private static StubHttpMessageHandler CreateCoinListHandler()
    {
        return new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "[{\"id\":\"bitcoin\",\"symbol\":\"btc\",\"name\":\"Bitcoin\"}]",
                Encoding.UTF8,
                "application/json")
        });
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHttpMessageHandler(
            Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = new List<LogEntry>();

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception exception,
            Func<TState, Exception, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
        }
    }

    private sealed class LogEntry
    {
        public LogEntry(LogLevel level, string message)
        {
            Level = level;
            Message = message;
        }

        public LogLevel Level { get; }
        public string Message { get; }
    }
}
