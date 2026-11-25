using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoinGlimpse.Controllers;
using CoinGlimpse.DataObjects;
using CoinGlimpse.External.Crypto;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class CoinControllerTests
{
    [TestMethod]
    public async Task Get_ReturnsOk_WhenCoinIsFound()
    {
        var expected = new Crypto { Price = 42.5 };
        var manager = new FakeCryptoManager { CoinResult = expected };
        var controller = CreateController(manager);

        var result = await controller.Get("btc");

        var okResult = result as OkObjectResult;
        Assert.IsNotNull(okResult);
        Assert.AreSame(expected, okResult.Value);
        Assert.AreEqual("btc", manager.LastTicker);
    }

    [TestMethod]
    public async Task Get_ReturnsBadRequest_WhenTickerIsInvalid()
    {
        var manager = new FakeCryptoManager();
        var controller = CreateController(manager);

        var result = await controller.Get(" ");

        Assert.IsInstanceOfType(result, typeof(BadRequestObjectResult));
        Assert.AreEqual(0, manager.FetchCoinCalls);
    }

    [TestMethod]
    public async Task Get_ReturnsBadRequest_WhenManagerReturnsErrorMessage()
    {
        var manager = new FakeCryptoManager
        {
            CoinResult = new Crypto { msg = "Unknown coin" }
        };
        var controller = CreateController(manager);

        var result = await controller.Get("unknown");

        var badRequest = result as BadRequestObjectResult;
        Assert.IsNotNull(badRequest);
        Assert.AreEqual("Unknown coin", badRequest.Value);
    }

    [TestMethod]
    public async Task GetList_ReturnsOk_WhenManagerReturnsCoins()
    {
        var expected = new List<CryptoListitem>
        {
            new CryptoListitem { id = "bitcoin", name = "Bitcoin", ticker = "btc" }
        };
        var controller = CreateController(new FakeCryptoManager { ListResult = expected });

        var result = await controller.Get();

        var okResult = result as OkObjectResult;
        Assert.IsNotNull(okResult);
        Assert.AreSame(expected, okResult.Value);
    }

    [TestMethod]
    public async Task GetList_ReturnsInternalServerError_WhenManagerThrows()
    {
        var controller = CreateController(new FakeCryptoManager
        {
            ListException = new InvalidOperationException("Coin API unavailable")
        });

        var result = await controller.Get();

        var errorResult = result as ObjectResult;
        Assert.IsNotNull(errorResult);
        Assert.AreEqual(500, errorResult.StatusCode);
        Assert.AreEqual("Coin API unavailable", errorResult.Value);
    }

    private static CoinController CreateController(ICryptoManager manager)
    {
        return new CoinController(NullLogger<CoinController>.Instance, manager);
    }

    private sealed class FakeCryptoManager : ICryptoManager
    {
        public Crypto CoinResult { get; set; }
        public List<CryptoListitem> ListResult { get; set; } = new List<CryptoListitem>();
        public Exception ListException { get; set; }
        public int FetchCoinCalls { get; private set; }
        public string LastTicker { get; private set; }

        public Task<List<CryptoListitem>> FetchList(
            CancellationToken cancellationToken = default)
        {
            if (ListException != null)
            {
                throw ListException;
            }

            return Task.FromResult(ListResult);
        }

        public Task<Crypto> FetchCoin(
            string ticker,
            CancellationToken cancellationToken = default)
        {
            FetchCoinCalls++;
            LastTicker = ticker;
            return Task.FromResult(CoinResult);
        }
    }
}
