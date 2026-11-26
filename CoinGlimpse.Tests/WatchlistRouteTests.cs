using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CoinGlimpse;
using CoinGlimpse.External.Crypto;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class WatchlistRouteTests
{
    [TestMethod]
    public async Task WatchlistBatchesTenIdsAndReusesEquivalentRequests()
    {
        using var upstream = new QuoteFixture();
        await using var app = await StartApp(upstream);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };

        var ids = Enumerable.Range(0, 10).Select(index => $"coin-{index}").ToArray();
        var route = "/api/watchlist?ids=";
        using var first = await client.GetAsync(route + string.Join(',', ids));
        Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
        var original = await first.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(original);
        Assert.AreEqual("USD", body.RootElement.GetProperty("currency").GetString());
        var quotes = body.RootElement.GetProperty("quotes");
        Assert.AreEqual(10, quotes.GetArrayLength());
        Assert.AreEqual(1.25m, quotes[0].GetProperty("price").GetDecimal());
        Assert.AreEqual(JsonValueKind.Null, quotes[9].GetProperty("price").ValueKind);

        using var equivalent = await client.GetAsync(route + string.Join(',', ids.Reverse()) + ",COIN-0");
        Assert.AreEqual(HttpStatusCode.OK, equivalent.StatusCode);
        Assert.AreEqual(original, await equivalent.Content.ReadAsStringAsync());
        Assert.AreEqual(1, upstream.Calls);
    }

    [TestMethod]
    public async Task WatchlistSeparatesInvalidInputRateLimitsAndProviderErrors()
    {
        using var upstream = new QuoteFixture { StatusCode = HttpStatusCode.TooManyRequests };
        await using var app = await StartApp(upstream);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };

        using var invalid = await client.GetAsync("/api/watchlist?ids=coin-0,,coin-1");
        Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.AreEqual(0, upstream.Calls);
        using var limited = await client.GetAsync("/api/watchlist?ids=coin-0");
        Assert.AreEqual(HttpStatusCode.TooManyRequests, limited.StatusCode);
        using var limitedBody = JsonDocument.Parse(await limited.Content.ReadAsStringAsync());
        Assert.AreEqual("provider_rate_limited", limitedBody.RootElement.GetProperty("code").GetString());

        upstream.StatusCode = HttpStatusCode.ServiceUnavailable;
        using var unavailable = await client.GetAsync("/api/watchlist?ids=coin-0");
        Assert.AreEqual(HttpStatusCode.BadGateway, unavailable.StatusCode);
        using var unavailableBody = JsonDocument.Parse(await unavailable.Content.ReadAsStringAsync());
        Assert.AreEqual("provider_unavailable", unavailableBody.RootElement.GetProperty("code").GetString());
        Assert.AreEqual(2, upstream.Calls);
    }

    private static async Task<WebApplication> StartApp(QuoteFixture upstream)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration["CoinGecko:ApiKey"] = "fixture";
        builder.Configuration["CoinGecko:MaxRetryAttempts"] = "0";
        new Startup(builder.Configuration).ConfigureServices(builder.Services);
        builder.Services.AddControllers().AddApplicationPart(typeof(Startup).Assembly);
        builder.Services.AddHttpClient(nameof(ICryptoManager)).ConfigurePrimaryHttpMessageHandler(() => upstream);
        var app = builder.Build();
        app.MapControllers();
        await app.StartAsync();
        return app;
    }

    private sealed class QuoteFixture : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            Assert.AreEqual("/api/v3/simple/price", request.RequestUri.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(StatusCode)
            {
                Content = new StringContent("""{"coin-0":{"usd":1.25,"last_updated_at":1700000000}}""")
            });
        }
    }
}
