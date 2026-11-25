using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoinGlimpse.DataObjects;

namespace CoinGlimpse.External.Crypto
{
    public interface ICryptoManager
    {
        Task<List<CryptoListitem>> FetchList(CancellationToken cancellationToken = default);
        Task<CoinGlimpse.DataObjects.Crypto> FetchCoin(
            string ticker,
            CancellationToken cancellationToken = default);
    }
}
