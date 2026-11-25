using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using CoinGlimpse.External.Crypto;

namespace CoinGlimpse.Controllers
{

    [Route("api/[controller]")]
    [ApiController]
    public class CoinController : ControllerBase
    {
        private readonly ILogger<CoinController> _logger;
        private readonly ICryptoManager _cryptoManager;
        

        public CoinController(ILogger<CoinController> logger, ICryptoManager cryptoManager)
        {
            _logger  = logger;
            _cryptoManager = cryptoManager;
            
        }
        
        [HttpGet]
        public async Task<IActionResult> Get(string ticker, string currency = "usd")
        {
            if (string.IsNullOrWhiteSpace(ticker))
                return BadRequest($" \"{ticker}\" is not a valid crypto ticker symbol");


            var data = await _cryptoManager.FetchCoin(
                ticker,
                currency,
                ControllerContext.HttpContext?.RequestAborted ?? CancellationToken.None);
            if (data.CandidateIds != null)
                return Conflict(new { code = "ambiguous_symbol", candidates = data.CandidateIds });

            if(data.msg != null){
                return BadRequest(data.msg);
            }
            else 
                return Ok(data);
        }

        [HttpGet]
        [Route("list")]
        public async Task<IActionResult> Get()
        {
            try
            {
                return await Task.FromResult(
                    Ok(await _cryptoManager.FetchList(
                        ControllerContext.HttpContext?.RequestAborted ?? CancellationToken.None)));
            }
            catch (Exception ex)
            {
                return await Task.FromResult(StatusCode(500, ex.Message));
            }
        }
    }
}