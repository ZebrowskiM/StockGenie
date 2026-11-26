using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoinGlimpse.DataObjects;

namespace CoinGlimpse.External.Crypto
{
    public sealed record CoinCatalogRead(List<CryptoListitem> Coins, TimeSpan? Age, bool IsStale);

    public interface ICryptoManager
    {
        Task<List<CryptoListitem>> FetchList(CancellationToken cancellationToken = default);
        async Task<CoinCatalogRead> FetchCatalog(CancellationToken cancellationToken = default) =>
            new(await FetchList(cancellationToken).ConfigureAwait(false), null, false);

        Task<CoinGlimpse.DataObjects.Crypto> FetchCoin(
            string ticker,
            CancellationToken cancellationToken = default);
        Task<CoinGlimpse.DataObjects.Crypto> FetchCoin(
            string ticker,
            string currency,
            CancellationToken cancellationToken = default);
    }
}
