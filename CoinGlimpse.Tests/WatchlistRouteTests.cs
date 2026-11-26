using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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

    [TestMethod]
    public async Task WatchlistForwardsRetryAfterWhenTheDelayExceedsItsBudget()
    {
        var retryHints = new[]
        {
            new RetryConditionHeaderValue(TimeSpan.FromHours(1)),
            new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddHours(1))
        };

        foreach (var hint in retryHints)
        {
            using var upstream = new QuoteFixture
            {
                StatusCode = HttpStatusCode.TooManyRequests,
                RetryAfter = hint
            };
            await using var app = await StartApp(upstream, maxRetryAttempts: 2);
            using var client = new HttpClient
            {
                BaseAddress = new Uri(app.Urls.Single()),
                Timeout = TimeSpan.FromSeconds(5)
            };

            using var result = await client.GetAsync("/api/watchlist?ids=coin-0");
            Assert.AreEqual(HttpStatusCode.TooManyRequests, result.StatusCode);
            Assert.AreEqual(hint.ToString(), result.Headers.RetryAfter?.ToString());
            Assert.AreEqual(1, upstream.Calls);
        }
    }

    [TestMethod]
    public async Task WatchlistRetriesWhenRetryAfterFitsItsBudget()
    {
        using var upstream = new QuoteFixture
        {
            StatusCode = HttpStatusCode.TooManyRequests,
            RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1)),
            SucceedAfterFirstCall = true
        };
        await using var app = await StartApp(upstream, maxRetryAttempts: 1);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };

        using var result = await client.GetAsync("/api/watchlist?ids=coin-0");
        Assert.AreEqual(HttpStatusCode.OK, result.StatusCode);
        Assert.AreEqual(2, upstream.Calls);
        Assert.IsTrue(upstream.RetryDelay >= TimeSpan.FromSeconds(1));
    }

    private sealed class FixtureClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task WatchlistMeasuresProviderAgeAndCountsActualAttempts()
    {
        var counts = new System.Collections.Concurrent.ConcurrentDictionary<string, long>();
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, observer) =>
        {
            if (instrument.Meter.Name == "CoinGlimpse.Watchlist")
                observer.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, state) =>
            counts.AddOrUpdate(instrument.Name, value, (_, count) => count + value));
        listener.Start();

        var updated = DateTimeOffset.FromUnixTimeSeconds(1700000000);
        var clock = new FixtureClock { Now = updated.AddSeconds(299) };
        using var upstream = new QuoteFixture();
        await using var app = await StartApp(upstream, clock: clock);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };

        async Task<JsonDocument> Read(string ids) =>
            JsonDocument.Parse(await client.GetStringAsync("/api/watchlist?ids=" + ids));

        using var fresh = await Read("coin-0");
        Assert.IsFalse(fresh.RootElement.GetProperty("quotes")[0].GetProperty("isStale").GetBoolean());
        clock.Now = updated.AddMinutes(5);
        using var cached = await Read("coin-0");
        Assert.IsTrue(cached.RootElement.GetProperty("quotes")[0].GetProperty("isStale").GetBoolean());
        Assert.AreEqual(1, upstream.Calls);

        using var fetched = await Read("coin-0,missing");
        Assert.AreEqual(clock.Now, fetched.RootElement.GetProperty("retrievedAt").GetDateTimeOffset());
        var quotes = fetched.RootElement.GetProperty("quotes");
        Assert.IsTrue(quotes[0].GetProperty("isStale").GetBoolean());
        Assert.AreEqual(JsonValueKind.Null, quotes[1].GetProperty("isStale").ValueKind);

        upstream.StatusCode = HttpStatusCode.TooManyRequests;
        using var limited = await client.GetAsync("/api/watchlist?ids=another");
        Assert.AreEqual(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.AreEqual(3L, counts["watchlist.upstream.requests"]);
        Assert.AreEqual(1L, counts["watchlist.cache.hits"]);
        Assert.AreEqual(1L, counts["watchlist.upstream.rate_limits"]);
    }

    private static async Task<WebApplication> StartApp(QuoteFixture upstream, int maxRetryAttempts = 0, TimeProvider clock = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration["CoinGecko:ApiKey"] = "fixture";
        builder.Configuration["CoinGecko:MaxRetryAttempts"] = maxRetryAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture);
        new Startup(builder.Configuration).ConfigureServices(builder.Services);
        if (clock != null)
            builder.Services.AddSingleton(clock);
        builder.Services.AddControllers().AddApplicationPart(typeof(Startup).Assembly);
        builder.Services.AddHttpClient(nameof(ICryptoManager)).ConfigurePrimaryHttpMessageHandler(() => upstream);
        var app = builder.Build();
        app.MapControllers();
        await app.StartAsync();
        return app;
    }

    private sealed class QuoteFixture : HttpMessageHandler
    {
        private long _firstCall;
        public RetryConditionHeaderValue RetryAfter { get; set; }
        public bool SucceedAfterFirstCall { get; set; }
        public TimeSpan RetryDelay { get; private set; }
        public int Calls { get; private set; }
        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            Assert.AreEqual("/api/v3/simple/price", request.RequestUri.AbsolutePath);
            if (Calls == 1)
                _firstCall = System.Diagnostics.Stopwatch.GetTimestamp();
            else
                RetryDelay = System.Diagnostics.Stopwatch.GetElapsedTime(_firstCall);

            var status = Calls > 1 && SucceedAfterFirstCall ? HttpStatusCode.OK : StatusCode;
            var result = new HttpResponseMessage(status)
            {
                Content = new StringContent("""{"coin-0":{"usd":1.25,"last_updated_at":1700000000}}""")
            };
            if (status == HttpStatusCode.TooManyRequests)
                result.Headers.RetryAfter = RetryAfter;
            return Task.FromResult(result);
        }
    }
}
