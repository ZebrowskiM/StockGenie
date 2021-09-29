using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
namespace StockGenie.Controllers
{

    [Route("api/[controller]")]
    [ApiController]
    public class CoinController : ControllerBase
    {
        private readonly ILogger<CoinController> _logger;

        public CoinController(ILogger<CoinController> logger)
        {
            _logger  = logger;
        }
        
        [HttpGet]
        public async Task<IActionResult> Get()
        {
            
            return Ok("Please Specify a Coin by providing a coin id or Refer to the coin list");
        }
    }
}