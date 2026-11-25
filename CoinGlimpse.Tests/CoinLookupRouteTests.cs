using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CoinGlimpse.Controllers;
using CoinGlimpse.External.Crypto;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class CoinLookupRouteTests
{
    [TestMethod]
    public async Task LookupPrefersCanonicalIdAndReturnsOrderedChoicesForSharedSymbols()
    {
        var canonicalId = new string('a', 100);
        using var handler = new FixtureHandler(canonicalId);
        using var upstream = new HttpClient(handler);
        await using var app = CreateApp(upstream);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };

        using var success = await client.GetAsync("/api/coin?ticker=%20" + canonicalId.ToUpperInvariant() + "%20");
        Assert.AreEqual(HttpStatusCode.OK, success.StatusCode);
        Assert.AreEqual("/api/v3/coins/" + canonicalId, handler.DetailPath);
        using var coin = JsonDocument.Parse(await success.Content.ReadAsStringAsync());
        Assert.AreEqual("USD", coin.RootElement.GetProperty("currency").GetString());

        using var conflict = await client.GetAsync("/api/coin?ticker=%20DuP%20");
        Assert.AreEqual(HttpStatusCode.Conflict, conflict.StatusCode);
        using var error = JsonDocument.Parse(await conflict.Content.ReadAsStringAsync());
        Assert.AreEqual("ambiguous_symbol", error.RootElement.GetProperty("code").GetString());
        var ids = error.RootElement.GetProperty("candidates").EnumerateArray().Select(x => x.GetString());
        Assert.AreEqual("alpha,zeta", string.Join(",", ids));

        using var invalid = await client.GetAsync("/api/coin?ticker=" + canonicalId + "b");
        Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.AreEqual(2, handler.Requests);
    }

    [TestMethod]
    public async Task ProviderFailuresAreSanitizedAndUnknownCoinsReturnNotFound()
    {
        using var handler = new FixtureHandler("bitcoin") { FailCatalog = true };
        using var upstream = new HttpClient(handler);
        await using var app = CreateApp(upstream);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };

        await AssertProviderFailure("/api/coin/list");
        await AssertProviderFailure("/api/coin?ticker=bitcoin");

        // Restart the host with a good catalog to exercise the detail upstream.
        using var detailHandler = new FixtureHandler("bitcoin") { FailDetail = true };
        using var detailUpstream = new HttpClient(detailHandler);
        await using var detailApp = CreateApp(detailUpstream);
        await detailApp.StartAsync();
        using var detailClient = new HttpClient { BaseAddress = new Uri(detailApp.Urls.Single()) };
        using var unknown = await detailClient.GetAsync("/api/coin?ticker=unknown");
        Assert.AreEqual(HttpStatusCode.NotFound, unknown.StatusCode);
        using var unknownBody = JsonDocument.Parse(await unknown.Content.ReadAsStringAsync());
        Assert.AreEqual("unknown_coin", unknownBody.RootElement.GetProperty("code").GetString());
        using var failedDetail = await detailClient.GetAsync("/api/coin?ticker=bitcoin");
        await CheckProviderFailure(failedDetail);

        async Task AssertProviderFailure(string route)
        {
            using var response = await client.GetAsync(route);
            await CheckProviderFailure(response);
        }
    }

    [TestMethod]
    public async Task ListReportsCatalogAgeDuringFallbackAndStopsServingAfterExpiry()
    {
        var clock = new CatalogClock();
        using var handler = new FixtureHandler("bitcoin");
        using var upstream = new HttpClient(handler);
        await using var app = CreateApp(upstream, new CoinCatalogCache(clock));
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };

        using var fresh = await client.GetAsync("/api/coin/list");
        Assert.AreEqual(HttpStatusCode.OK, fresh.StatusCode);
        Assert.AreEqual("0", fresh.Headers.GetValues("CoinGlimpse-Catalog-Age-Seconds").Single());
        Assert.AreEqual("false", fresh.Headers.GetValues("CoinGlimpse-Catalog-Stale").Single());

        clock.Now += TimeSpan.FromMinutes(10);
        handler.FailCatalog = true;
        using var stale = await client.GetAsync("/api/coin/list");
        Assert.AreEqual(HttpStatusCode.OK, stale.StatusCode);
        Assert.AreEqual("600", stale.Headers.GetValues("CoinGlimpse-Catalog-Age-Seconds").Single());
        Assert.AreEqual("true", stale.Headers.GetValues("CoinGlimpse-Catalog-Stale").Single());
        Assert.AreEqual(await fresh.Content.ReadAsStringAsync(), await stale.Content.ReadAsStringAsync());

        clock.Now += TimeSpan.FromMinutes(30);
        using var expired = await client.GetAsync("/api/coin/list");
        await CheckProviderFailure(expired);
        Assert.AreEqual(false, expired.Headers.Contains("CoinGlimpse-Catalog-Age-Seconds"));
    }

    private sealed class CatalogClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static async Task CheckProviderFailure(HttpResponseMessage response)
    {
        Assert.AreEqual(HttpStatusCode.BadGateway, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual("provider_unavailable", body.RootElement.GetProperty("code").GetString());
        Assert.AreEqual("CoinGecko data is temporarily unavailable.",
            body.RootElement.GetProperty("message").GetString());
    }

    private static WebApplication CreateApp(HttpClient upstream, CoinCatalogCache cache = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddControllers().AddApplicationPart(typeof(CoinController).Assembly);
        builder.Services.AddSingleton<ICryptoManager>(new CryptoManager(upstream,
            NullLogger<CryptoManager>.Instance, Options.Create(new CoinGeckoOptions { ApiKey = "fixture", MaxRetryAttempts = 0 }), cache));
        var app = builder.Build();
        app.MapControllers();
        return app;
    }

    private sealed class FixtureHandler(string canonicalId) : HttpMessageHandler
    {
        public bool FailCatalog { get; set; }
        public bool FailDetail { get; set; }
        public string DetailPath { get; private set; }
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            string body;
            var isCatalog = request.RequestUri.AbsolutePath == "/api/v3/coins/list";
            if ((isCatalog && FailCatalog) || (!isCatalog && FailDetail))
            {
                throw new HttpRequestException("Private upstream diagnostics");
            }

            if (isCatalog)
            {
                body = JsonSerializer.Serialize(new[]
                {
                    new { id = "collision", symbol = canonicalId },
                    new { id = "zeta", symbol = "dup" },
                    new { id = canonicalId, symbol = "single" },
                    new { id = "alpha", symbol = "DUP" }
                });
            }
            else
            {
                DetailPath = request.RequestUri.AbsolutePath;
                body = """{"market_data":{"current_price":{"usd":1},"market_cap":{"usd":1},"total_volume":{"usd":1}},"platforms":{}}""";
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
