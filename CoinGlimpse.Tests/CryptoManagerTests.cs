using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoinGlimpse.External.Crypto;
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
        CoinGeckoPlan plan)
    {
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.coingecko.com/api/v3/")
        };

        return new CryptoManager(
            httpClient,
            NullLogger<CryptoManager>.Instance,
            Options.Create(new CoinGeckoOptions
            {
                ApiKey = "test-key",
                BaseUrl = "https://api.coingecko.com/api/v3/",
                Plan = plan
            }));
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
}
