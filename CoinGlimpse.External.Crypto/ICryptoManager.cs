using System.Collections.Generic;
using System.Threading.Tasks;
using CoinGlimpse.DataObjects;

namespace CoinGlimpse.External.Crypto
{
    public interface ICryptoManager
    {
        Task<List<CryptoListitem>> FetchList();
        Task<CoinGlimpse.DataObjects.Crypto> FetchCoin(string ticker);
    }
}
