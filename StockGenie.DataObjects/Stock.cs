using System;
using StockGenie.Interfaces.DataObjects;
/*
Things to include:
Current Price 
Bid Ask 
52 high
52 low 
Average 
50 MA 
Div yield (Maybe)
Market cap 
Description and Link to site ( maybe )
Sector / other relational data  ( optional) 
Exhange info
*/
namespace StockGenie.DataObjects
{
    public class Stock : ISecurity
    {
        public double Price {get;set;}
        public double YrHigh { get; set; }
        public double YrLow { get; set; }
        public double YrAverage { get; set; }
        public double MovingAverageFifthy { get; set; }
        public string ExchangeInfo { get; set; }
        public long MarketCap { get; set; }
        public long Volume { get; set; }
    }


}
