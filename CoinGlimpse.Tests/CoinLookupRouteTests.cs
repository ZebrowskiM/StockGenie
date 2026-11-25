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
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddControllers().AddApplicationPart(typeof(CoinController).Assembly);
        builder.Services.AddSingleton<ICryptoManager>(new CryptoManager(upstream,
            NullLogger<CryptoManager>.Instance, Options.Create(new CoinGeckoOptions { ApiKey = "fixture" })));
        await using var app = builder.Build();
        app.MapControllers();
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

    private sealed class FixtureHandler(string canonicalId) : HttpMessageHandler
    {
        public string DetailPath { get; private set; }
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            string body;
            if (request.RequestUri.AbsolutePath == "/api/v3/coins/list")
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
