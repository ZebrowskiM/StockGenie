using System;
namespace StockGenie.Interfaces.DataObjects
{
    

public interface ISecurity
{
    Double Price {get;set;}
    Double YrHigh {get;set;}
    Double YrLow {get;set;}
    Double YrAverage {get;set;}
    Double MovingAverageFifthy {get;set;}
    string ExchangeInfo {get;set;}
    long MarketCap {get;set;}
    long Volume{get;set;}



}
}