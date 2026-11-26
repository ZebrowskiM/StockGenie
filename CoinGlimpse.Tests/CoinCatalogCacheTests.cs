using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CoinGlimpse;
using CoinGlimpse.Controllers;
using CoinGlimpse.DataObjects;
using CoinGlimpse.External.Crypto;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class CoinCatalogCacheTests
{
    [TestMethod]
    public async Task ConcurrentListAndDetailRequestsShareOneCatalogRefresh()
    {
        var handler = new CatalogHandler();
        using var provider = CreateProvider(handler);

        var list = CreateController(provider).Get();
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var detail = CreateController(provider).Get("btc");
        handler.Release.SetResult();

        var listResult = (OkObjectResult)await list;
        var coins = (List<CryptoListitem>)listResult.Value;
        Assert.AreEqual("bitcoin", coins[0].id);
        coins[0].id = "edited-by-caller";
        Assert.IsInstanceOfType(await detail, typeof(OkObjectResult));
        var cached = (OkObjectResult)await CreateController(provider).Get();
        Assert.AreEqual("bitcoin", ((List<CryptoListitem>)cached.Value)[0].id);
        Assert.AreEqual(1, handler.ListRequests);
    }

    [TestMethod]
    public async Task CancelingFirstCallerLeavesSharedRefreshAvailableToListRequest()
    {
        using var handler = new CatalogHandler();
        using var provider = CreateProvider(handler);
        using var cancellation = new CancellationTokenSource();
        var detailController = CreateController(provider);
        detailController.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { RequestAborted = cancellation.Token }
        };

        var timeout = TimeSpan.FromSeconds(5);
        var detail = detailController.Get("btc");
        try
        {
            await handler.Started.Task.WaitAsync(timeout);
            var list = CreateController(provider).Get();
            cancellation.Cancel();

            try
            {
                await detail.WaitAsync(timeout);
                Assert.Fail("The detail request should observe caller cancellation.");
            }
            catch (OperationCanceledException ex)
            {
                Assert.AreEqual(cancellation.Token, ex.CancellationToken);
            }

            Assert.AreEqual(false, list.IsCompleted);
            handler.Release.SetResult();
            var response = (OkObjectResult)await list.WaitAsync(timeout);
            var coins = (List<CryptoListitem>)response.Value;
            Assert.AreEqual("bitcoin", coins[0].id);
            Assert.AreEqual(1, handler.ListRequests);
        }
        finally
        {
            handler.Release.TrySetResult();
        }
    }

    private static ServiceProvider CreateProvider(CatalogHandler handler)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string> { ["CoinGecko:ApiKey"] = "fixture" }).Build();
        new Startup(config).ConfigureServices(services);
        services.AddHttpClient(nameof(ICryptoManager)).ConfigurePrimaryHttpMessageHandler(() => handler);
        return services.BuildServiceProvider();
    }

    private static CoinController CreateController(ServiceProvider provider) =>
        new CoinController(NullLogger<CoinController>.Instance,
            provider.GetRequiredService<ICryptoManager>());

    private sealed class CatalogHandler : HttpMessageHandler
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ListRequests;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string json;
            if (request.RequestUri.AbsolutePath.EndsWith("/coins/list", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref ListRequests);
                Started.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
                json = """[{"id":"bitcoin","symbol":"btc","name":"Bitcoin"}]""";
            }
            else
            {
                Assert.AreEqual("/api/v3/coins/bitcoin", request.RequestUri.AbsolutePath);
                json = """{"market_data":{"current_price":{"usd":1},"market_cap":{"usd":1},"total_volume":{"usd":1}},"platforms":{}}""";
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        }
    }
}
