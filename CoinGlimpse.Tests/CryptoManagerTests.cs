using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
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
        Assert.AreEqual(50000.25, result.Price, 0.001);
        Assert.AreEqual(990000000000L, result.MarketCap);
        Assert.AreEqual(25000000000L, result.Volume);
        Assert.AreEqual("ethereum", result.ExchangeInfo);
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
