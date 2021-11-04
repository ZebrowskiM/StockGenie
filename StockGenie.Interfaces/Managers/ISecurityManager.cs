using System.Collections;
using System.Collections.Generic;
using StockGenie.Interfaces.DataObjects;
public interface ISecurityManager
{
    //list of all securties 
    IAsyncEnumerable<ISecurity> GetSecurities();
    //Used to look up the security via api
    ISecurity GetSecurity(string securityName);
    //returns a list of exchanges 
    List<string> GetExchanges();
}