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
        public TimeSpan CoinCacheDuration { get; set; } = TimeSpan.FromSeconds(60);
        public int MaxRetryAttempts { get; set; } = 2;
    }

    public enum CoinGeckoPlan
    {
        Demo,
        Pro
    }
}
