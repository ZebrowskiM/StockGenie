using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CoinGlimpse.External.Crypto;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class CatalogRecoveryTests
{
    [TestMethod]
    public async Task FailedRefreshServesLastGoodOnlyWithinStaleWindow()
    {
        var clock = new Clock();
        using var handler = new CatalogFixture();
        using var http = new HttpClient(handler);
        var manager = Manager(clock, http);

        Assert.AreEqual("first", (await manager.FetchList())[0].id);
        clock.Advance(9);
        await manager.FetchList();
        Assert.AreEqual(1, handler.Calls);
        clock.Advance(1);
        handler.Body = "[]";
        Assert.AreEqual("first", (await manager.FetchList())[0].id);
        Assert.AreEqual(2, handler.Calls);
        clock.Advance(2);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => manager.FetchList());
        Assert.AreEqual(2, handler.Calls);
        clock.Advance(3);
        handler.Body = """[{"id":"second"}]""";
        Assert.AreEqual("second", (await manager.FetchList())[0].id);
        Assert.AreEqual(3, handler.Calls);
    }

    [TestMethod]
    public async Task ColdFailureBacksOffAndRecoversOnDemand()
    {
        var clock = new Clock();
        using var handler = new CatalogFixture { Body = "[]" };
        using var http = new HttpClient(handler);
        var manager = Manager(clock, http);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => manager.FetchList());
        handler.Body = """[{"id":"recovered"}]""";
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => manager.FetchList());
        Assert.AreEqual(1, handler.Calls);
        clock.Advance(5);
        Assert.AreEqual("recovered", (await manager.FetchList())[0].id);
        Assert.AreEqual(2, handler.Calls);
    }

    private static CryptoManager Manager(Clock clock, HttpClient http) =>
        new(http, NullLogger<CryptoManager>.Instance, Options.Create(new CoinGeckoOptions
        {
            ApiKey = "fixture",
            ListCacheDuration = TimeSpan.FromMinutes(10),
            ListMaxStaleDuration = TimeSpan.FromMinutes(2),
            ListRefreshBackoff = TimeSpan.FromMinutes(5)
        }), new CoinCatalogCache(clock));

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(int minutes) => _now += TimeSpan.FromMinutes(minutes);
    }

    private sealed class CatalogFixture : HttpMessageHandler
    {
        public int Calls;
        public string Body = """[{"id":"first"}]""";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Body)
            });
        }
    }
}
