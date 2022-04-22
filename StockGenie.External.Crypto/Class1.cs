using System.Linq;
using System;

using CoinGecko.Clients;
using CoinGecko.Interfaces;

namespace StockGenie.External.Crypto
{
    public class Class1
    {
        private readonly ICoinGeckoClient _client;
        public Class1(){
        
            _client = CoinGeckoClient.Instance;
            
        }
        public void method(){
           var x =   _client.CoinsClient.GetAllCoinDataWithId("bitcoin");
           x.ConfigureAwait(false);
           var y = x.Result ;
           var q = y.MarketData.CurrentPrice["usd"];
           var zzx = _client.CoinsClient.GetAllCoinDataWithId("ethereum");
           zzx.ConfigureAwait(false);
           var xx = zzx.Result;
           var qqq = xx.MarketData.CurrentPrice["usd"];
           var z = _client.SimpleClient.GetSimplePrice(new []{"BTC"},new[]{"USDC"},true,true,true,true);
           z.ConfigureAwait(false);
           var zz = z.Result;

           var i = _client.CoinsClient.GetCoinList();
           i.ConfigureAwait(false);
           var ii = i.Result;
           var u = i.Result.ToList().Find(x => x.Symbol.Equals("btc",StringComparison.InvariantCultureIgnoreCase));

        }
    }
}
