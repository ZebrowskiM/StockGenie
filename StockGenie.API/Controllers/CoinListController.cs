using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
namespace StockGenie.Controllers
{

    using Microsoft.AspNetCore.Mvc;

    [Route("api/Coin/[controller]")]
    [ApiController]
    public class CoinList : ControllerBase
    {
        [HttpGet]
        public async Task<IActionResult> Get()
        {
            
            return Ok("Test1");
        }
    }
}