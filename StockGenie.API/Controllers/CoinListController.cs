using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
namespace StockGenie.Controllers
{

    using Microsoft.AspNetCore.Mvc;
    using StockGenie.External.Crypto;


    [Route("api/Coin/[controller]")]
    [ApiController]
    public class CoinListController : ControllerBase
    {
        private readonly CryptoManager _cryptoManager;
        private readonly ILogger<CoinListController> _logger;
        public CoinListController(ILogger<CoinListController> logger)
        {
            _logger = logger;
            _cryptoManager = new CryptoManager();

        }
       
        [HttpGet]
        public async Task<IActionResult> Get()
        {
            try
            {
                return await Task.FromResult(Ok(_cryptoManager.FetchList()));
            }
            catch (Exception ex)
            {
                return await Task.FromResult(StatusCode(500, ex.Message));
            }
        }
    }
}