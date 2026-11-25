using System.Runtime.InteropServices;
using System.Linq;
using System;
using CoinGecko.Clients;
using CoinGecko.Interfaces;
using CoinGlimpse.DataObjects;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace CoinGlimpse.External.Crypto
{
    public class CryptoManager : ICryptoManager
    {
        private readonly ICoinGeckoClient _client;

        public CryptoManager(){
            _client = CoinGeckoClient.Instance;
        }

        public CryptoManager(string apiKey)
        {
            _client = string.IsNullOrWhiteSpace(apiKey)
                ? CoinGeckoClient.Instance
                : new CoinGeckoClient(new CoinGeckoApiKeyHandler(apiKey));
        }

        public async Task<List<CryptoListitem>> FetchList()
        {
            var list = new List<CryptoListitem>();

            var coins = await _client.CoinsClient.GetCoinList().ConfigureAwait(false);

            if (coins == null || !coins.Any())
            {
                throw new Exception("Error communicating to Coin API");
            }
            foreach (var item in coins)
            {
                list.Add(new CryptoListitem
                {
                    id = item.Id,
                    name = item.Name,
                    ticker = item.Symbol
                }) ;
            }

            return list;
        }
   

        public async Task<CoinGlimpse.DataObjects.Crypto> FetchCoin( string ticker)
        {
            var input = ticker?.Trim();
            if (string.IsNullOrEmpty(input))
            {
                return new DataObjects.Crypto { msg = "Enter a coin ID or ticker." };
            }

            try
            {
                var coins = await _client.CoinsClient.GetCoinList().ConfigureAwait(false);
                if (coins == null || !coins.Any())
                {
                    return new DataObjects.Crypto{
                        msg = "Error communicating to Coin API"
                    };
                }

                var result = coins.FirstOrDefault(
                    x => string.Equals(x.Symbol, input, StringComparison.OrdinalIgnoreCase))
                    ?? coins.FirstOrDefault(
                        x => string.Equals(x.Id, input, StringComparison.OrdinalIgnoreCase));

                if(result is null)
                {
                    return new DataObjects.Crypto{
                    msg = $" \"{ticker}\" is not a valid coin id or ticker, please try a differenct value "
                    };
                }

                return ConvertFromGecko(result);
            }
            catch(Exception ex)
            {
                return new DataObjects.Crypto{
                    msg = $"Err : {ex.Message} " 
                };
            }
            
        }

        private CoinGlimpse.DataObjects.Crypto ConvertFromGecko(CoinGecko.Entities.Response.Coins.CoinList geckoCoin)
        {
            var coin = new CoinGlimpse.DataObjects.Crypto(); 
            CoinGecko.Entities.Response.Coins.CoinFullDataById coinData = null;
            try{  
                 coinData = GetCoinData(geckoCoin.Id);
            }
            catch(Exception ex){
                if(ex.InnerException.ToString().Contains("Response status code does not indicate success: 404 (Not Found)"))
                {
                    return new DataObjects.Crypto(){
                        msg = "Could Not Get Coin Data for \"{geckoCoin.Id}\" because coin does not exist"
                    };
                }
                else
                throw;
            }
            if(coinData == null){
                return new DataObjects.Crypto{
                    msg = $"Could Not Get Coin Data for \"{geckoCoin.Id}\" "
                };
            }

            return Convert(coinData);
            
        }

        private CoinGlimpse.DataObjects.Crypto Convert(CoinGecko.Entities.Response.Coins.CoinFullDataById coinData){
 
            return new DataObjects.Crypto{
                Price = double.Parse(coinData.MarketData.CurrentPrice["usd"].ToString()),
                MarketCap = long.Parse(Math.Round(decimal.Parse(coinData.MarketData.MarketCap["usd"].ToString())).ToString()),
                ExchangeInfo = string.Join(",",coinData.Platforms.ToList()),
                

            };
        }
        

        private CoinGecko.Entities.Response.Coins.CoinFullDataById GetCoinData(string id)
        {
        
           var data =  _client.CoinsClient.GetAllCoinDataWithId(id);
           data.ConfigureAwait(false);
           return data.Result;
        }

        private sealed class CoinGeckoApiKeyHandler : HttpClientHandler
        {
            private readonly string _apiKey;

            public CoinGeckoApiKeyHandler(string apiKey)
            {
                _apiKey = apiKey;
            }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                request.Headers.Add("x-cg-demo-api-key", _apiKey);
                return base.SendAsync(request, cancellationToken);
            }
        }
    }
}
