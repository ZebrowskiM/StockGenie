using System.Net;
using System.Text;
using BenchmarkDotNet.Attributes;
using CoinGecko.Clients;
using CoinGlimpse.External.Crypto;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoinGlimpse.Benchmarks;

public abstract class CoinGeckoBenchmarkBase
{
    protected CryptoManager NewManager(out HttpClient httpClient)
    {
        httpClient = new HttpClient(new CoinGeckoStubHandler());
        return new CryptoManager(
            httpClient,
            NullLogger<CryptoManager>.Instance,
            Options.Create(new CoinGeckoOptions
            {
                BaseUrl = "https://api.coingecko.com/api/v3/",
                ListCacheDuration = TimeSpan.FromMinutes(10),
                CoinCacheDuration = TimeSpan.FromMinutes(10)
            }));
    }

    protected static CoinGeckoClient NewLegacyClient()
    {
        return new CoinGeckoClient(new CoinGeckoStubHandler());
    }

    protected sealed class CoinGeckoStubHandler : HttpClientHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            string json;

            if (path.EndsWith("/coins/list", StringComparison.Ordinal))
            {
                json = CoinGeckoBenchmarkFixtures.CoinListJson;
            }
            else if (path.EndsWith("/coins/bitcoin", StringComparison.Ordinal))
            {
                json = request.RequestUri?.Query.Contains("tickers=false", StringComparison.Ordinal) == true
                    ? CoinGeckoBenchmarkFixtures.CompactCoinJson
                    : CoinGeckoBenchmarkFixtures.FullCoinJson;
            }
            else
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}

[MemoryDiagnoser]
public class CoinListBenchmarks : CoinGeckoBenchmarkBase
{
    private CryptoManager _currentManager = null!;
    private HttpClient _currentHttpClient = null!;
    private CoinGeckoClient _legacyClient = null!;
    private CryptoManager _cachedManager = null!;
    private HttpClient _cachedHttpClient = null!;

    [GlobalSetup]
    public async Task SetupCacheBenchmark()
    {
        _cachedManager = NewManager(out _cachedHttpClient);
        VerifyList(await _cachedManager.FetchList());
    }

    [IterationSetup]
    public void SetupColdBenchmarks()
    {
        _currentManager = NewManager(out _currentHttpClient);
        _legacyClient = NewLegacyClient();
    }

    [IterationCleanup]
    public void CleanupColdBenchmarks()
    {
        _currentHttpClient.Dispose();
        _legacyClient.Dispose();
    }

    [GlobalCleanup]
    public void CleanupCacheBenchmark()
    {
        _cachedHttpClient.Dispose();
    }

    [Benchmark(Baseline = true, Description = "CoinGeckoAsyncApi 1.5.0")]
    public async Task<int> LegacyFetchList()
    {
        var coins = await _legacyClient.CoinsClient.GetCoinList();
        var result = coins.Select(coin => new CoinGlimpse.DataObjects.CryptoListitem
        {
            id = coin.Id,
            name = coin.Name,
            ticker = coin.Symbol
        }).ToList().Count;
        VerifyListCount(result);
        return result;
    }

    [Benchmark(Description = "Custom wrapper, cold list")]
    public async Task<int> CurrentFetchList()
    {
        var coins = await _currentManager.FetchList();
        VerifyList(coins);
        return coins.Count;
    }

    [Benchmark(Description = "Custom wrapper, cached list")]
    public async Task<int> CachedFetchList()
    {
        var coins = await _cachedManager.FetchList();
        VerifyList(coins);
        return coins.Count;
    }

    private static void VerifyList(
        IReadOnlyCollection<CoinGlimpse.DataObjects.CryptoListitem> coins)
    {
        VerifyListCount(coins.Count);
    }

    private static void VerifyListCount(int count)
    {
        if (count != CoinGeckoBenchmarkFixtures.ListSize)
        {
            throw new InvalidOperationException(
                $"Expected {CoinGeckoBenchmarkFixtures.ListSize} coins, received {count}.");
        }
    }
}

[MemoryDiagnoser]
public class CoinDetailBenchmarks : CoinGeckoBenchmarkBase
{
    private CryptoManager _currentManager = null!;
    private HttpClient _currentHttpClient = null!;
    private CoinGeckoClient _legacyClient = null!;
    private CryptoManager _cachedManager = null!;
    private HttpClient _cachedHttpClient = null!;

    [GlobalSetup]
    public async Task SetupCacheBenchmark()
    {
        _cachedManager = NewManager(out _cachedHttpClient);
        VerifyCoin(await _cachedManager.FetchCoin("btc"));
    }

    [IterationSetup]
    public void SetupColdBenchmarks()
    {
        _currentManager = NewManager(out _currentHttpClient);
        _legacyClient = NewLegacyClient();
    }

    [IterationCleanup]
    public void CleanupColdBenchmarks()
    {
        _currentHttpClient.Dispose();
        _legacyClient.Dispose();
    }

    [GlobalCleanup]
    public void CleanupCacheBenchmark()
    {
        _cachedHttpClient.Dispose();
    }

    [Benchmark(Baseline = true, Description = "CoinGeckoAsyncApi 1.5.0")]
    public async Task<double> LegacyFetchCoin()
    {
        var coins = await _legacyClient.CoinsClient.GetCoinList();
        var coin = coins.First(item =>
            string.Equals(item.Symbol, "btc", StringComparison.OrdinalIgnoreCase));
        var data = await _legacyClient.CoinsClient.GetAllCoinDataWithId(coin.Id);
        var currentPrice = data.MarketData?.CurrentPrice;
        var marketCap = data.MarketData?.MarketCap;
        if (currentPrice == null || currentPrice.Count == 0 ||
            marketCap == null || marketCap.Count == 0)
        {
            throw new InvalidOperationException("The legacy client returned incomplete market data.");
        }

        return currentPrice.Count + marketCap.Count;
    }

    [Benchmark(Description = "Custom wrapper, cold coin")]
    public async Task<double> CurrentFetchCoin()
    {
        var coin = await _currentManager.FetchCoin("btc");
        VerifyCoin(coin);
        return coin.Price + coin.MarketCap;
    }

    [Benchmark(Description = "Custom wrapper, cached coin")]
    public async Task<double> CachedFetchCoin()
    {
        var coin = await _cachedManager.FetchCoin("btc");
        VerifyCoin(coin);
        return coin.Price + coin.MarketCap;
    }

    private static void VerifyCoin(CoinGlimpse.DataObjects.Crypto coin)
    {
        if (coin.msg != null || coin.Price <= 0 || coin.MarketCap <= 0)
        {
            throw new InvalidOperationException(
                "The custom wrapper returned incomplete coin market data.");
        }
    }
}

internal static class CoinGeckoBenchmarkFixtures
{
    public const int ListSize = 2500;

    public static readonly string CoinListJson = CreateCoinListJson();

    public const string CompactCoinJson =
        "{\"market_data\":{\"current_price\":{\"usd\":50000.25},\"market_cap\":{\"usd\":990000000000},\"total_volume\":{\"usd\":25000000000}},\"platforms\":{\"ethereum\":\"0x123\"}}";

    public static readonly string FullCoinJson = CreateFullCoinJson();

    private static string CreateCoinListJson()
    {
        var coins = new string[ListSize];
        coins[0] = "{\"id\":\"bitcoin\",\"symbol\":\"btc\",\"name\":\"Bitcoin\"}";

        for (var index = 1; index < coins.Length; index++)
        {
            coins[index] =
                $"{{\"id\":\"benchmark-coin-{index}\",\"symbol\":\"bc{index}\",\"name\":\"Benchmark Coin {index}\"}}";
        }

        return "[" + string.Join(",", coins) + "]";
    }

    private static string CreateFullCoinJson()
    {
        var tickers = new string[50];
        for (var index = 0; index < tickers.Length; index++)
        {
            tickers[index] =
                $"{{\"base\":\"BTC\",\"target\":\"USD\",\"market\":{{\"name\":\"Benchmark Exchange {index}\",\"identifier\":\"exchange-{index}\",\"has_trading_incentive\":false}},\"last\":50000.25,\"volume\":1000,\"converted_last\":{{\"usd\":50000.25}},\"converted_volume\":{{\"usd\":50000000}},\"trust_score\":\"green\",\"trade_url\":\"https://example.com/trade/{index}\",\"timestamp\":\"2025-01-01T00:00:00Z\",\"last_traded_at\":\"2025-01-01T00:00:00Z\",\"last_fetch_at\":\"2025-01-01T00:00:00Z\",\"is_anomaly\":false,\"is_stale\":false,\"coin_id\":\"bitcoin\",\"target_coin_id\":\"usd\"}}";
        }

        return
            "{\"id\":\"bitcoin\",\"symbol\":\"btc\",\"name\":\"Bitcoin\",\"localization\":{\"en\":\"Bitcoin\"},\"description\":{\"en\":\"Benchmark response fixture\"},\"links\":{\"homepage\":[\"https://example.com\"],\"blockchain_site\":[],\"official_forum_url\":[],\"chat_url\":[],\"announcement_url\":[],\"twitter_screen_name\":\"bitcoin\",\"facebook_username\":\"bitcoin\",\"telegram_channel_identifier\":\"\",\"subreddit_url\":\"https://reddit.com/r/bitcoin\",\"repos_url\":{\"github\":[\"https://github.com/bitcoin/bitcoin\"],\"bitbucket\":[]}},\"image\":{\"thumb\":\"https://example.com/thumb.png\",\"small\":\"https://example.com/small.png\",\"large\":\"https://example.com/large.png\"},\"country_origin\":\"\",\"genesis_date\":\"2009-01-03\",\"market_cap_rank\":1,\"coingecko_rank\":1,\"coingecko_score\":90,\"developer_score\":80,\"community_score\":90,\"liquidity_score\":80,\"public_interest_score\":80,\"market_data\":{\"current_price\":{\"usd\":50000.25},\"market_cap\":{\"usd\":990000000000},\"total_volume\":{\"usd\":25000000000},\"ath\":{\"usd\":69000},\"ath_change_percentage\":{\"usd\":-27.5},\"ath_date\":{\"usd\":\"2021-11-10T00:00:00Z\"},\"atl\":{\"usd\":67.81},\"atl_change_percentage\":{\"usd\":73700},\"atl_date\":{\"usd\":\"2013-07-06T00:00:00Z\"},\"market_cap_rank\":1,\"fully_diluted_valuation\":{\"usd\":1000000000000},\"total_supply\":21000000,\"max_supply\":21000000,\"circulating_supply\":19000000,\"last_updated\":\"2025-01-01T00:00:00Z\"},\"community_data\":{\"facebook_likes\":null,\"twitter_followers\":100000,\"reddit_average_posts_48h\":1,\"reddit_average_comments_48h\":1,\"reddit_subscribers\":1000000,\"reddit_accounts_active_48h\":1000,\"telegram_channel_user_count\":null},\"developer_data\":{\"forks\":10000,\"stars\":50000,\"subscribers\":1000,\"total_issues\":100,\"closed_issues\":90,\"pull_requests_merged\":1000,\"pull_request_contributors\":100,\"commit_count_4_weeks\":100},\"public_interest_stats\":{\"alexa_rank\":1000,\"bing_matches\":100},\"status_updates\":[],\"tickers\":[" +
            string.Join(",", tickers) +
            "],\"platforms\":{\"ethereum\":\"0x123\"}}";
    }
}
