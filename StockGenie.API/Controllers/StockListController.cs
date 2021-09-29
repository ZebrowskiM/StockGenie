using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
namespace StockGenie.Controllers
{

    [Route("api/Stock/[controller]")]
    [ApiController]
    public class StockListController : ControllerBase
    {
        [HttpGet]
        public async Task<IActionResult> Get()
        {
            
            return Ok("Test2");
        }
    }
}