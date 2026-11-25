using System;
using CoinGlimpse.Interfaces.DataObjects;

/*
Things to include:
Current Price 
Bid Ask 
52 high
52 low 
Average 
50 MA 
9 or 13 day moving average 
Market cap 
Description and Link to site ( maybe )
Coin description and maybe usecases  ( optional) 

##Figure out what exchange to base prices on or include multiple##
*/
namespace CoinGlimpse.DataObjects
{
    public class Crypto : ISecurity
    {
        public decimal Price { get; set; }
        public string Currency { get; set; }
        public double YrHigh { get; set; }
        public double YrLow { get; set; }
        public double YrAverage { get; set; }
        public double MovingAverageFifthy { get; set; }
        public string ExchangeInfo { get; set; }
        public long MarketCap { get; set; }
        public long Volume { get; set; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string[] CandidateIds { get; set; }

        public string msg {get;set;}
    }
}
