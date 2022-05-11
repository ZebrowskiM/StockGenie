using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using StockGenie.External.Crypto;

namespace StockGenie.Controllers
{

    [Route("api/[controller]")]
    [ApiController]
    public class CoinController : ControllerBase
    {
        private readonly ILogger<CoinController> _logger;
        private readonly CryptoManager _cryptoManager;
        

        public CoinController(ILogger<CoinController> logger)
        {
            _logger  = logger;
            _cryptoManager = new CryptoManager();
            
        }
        
        [HttpGet]
        public async Task<IActionResult> Get(string ticker)
        {
            if(string.IsNullOrWhiteSpace(ticker) || ticker.Length > 10)
                return BadRequest($" \"{ticker}\" is not a valid crypto ticker symbol");


            var data = _cryptoManager.FetchCoin(ticker);
            if(data.msg != null){
                return BadRequest(data.msg);
            }
            else 
                return Ok(data);
        }
    }
}