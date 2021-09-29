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
    public class StockController : ControllerBase
    {
        private readonly ILogger<StockController> _logger;

        public StockController(ILogger<StockController> logger)
        {
            _logger  = logger;
        }

        [HttpGet]
        public async Task<IActionResult> Get()
        {
            
            return Ok("Please Specify a stock by providing a stock id or Refer to the stock list");
        }
    }
}