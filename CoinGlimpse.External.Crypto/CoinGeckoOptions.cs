using System;

namespace CoinGlimpse.External.Crypto
{
    public sealed class CoinGeckoOptions
    {
        public const string SectionName = "CoinGecko";

        public string ApiKey { get; set; }
        public string BaseUrl { get; set; } = "https://api.coingecko.com/api/v3/";
        public CoinGeckoPlan Plan { get; set; } = CoinGeckoPlan.Demo;
        public TimeSpan ListCacheDuration { get; set; } = TimeSpan.FromMinutes(10);
        public TimeSpan ListMaxStaleDuration { get; set; } = TimeSpan.FromMinutes(30);
        public TimeSpan ListRefreshBackoff { get; set; } = TimeSpan.FromMinutes(1);
        public TimeSpan QuoteCacheDuration { get; set; } = TimeSpan.FromSeconds(60);
        public TimeSpan QuoteMaxAge { get; set; } = TimeSpan.FromMinutes(5);
        public int QuoteCacheCapacity { get; set; } = 128;
        public int MaxRetryAttempts { get; set; } = 2;
        public bool EnableRequestLogging { get; set; }
    }

    public enum CoinGeckoPlan
    {
        Demo,
        Pro
    }
}
