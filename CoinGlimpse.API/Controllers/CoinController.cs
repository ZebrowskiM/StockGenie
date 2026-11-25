using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using CoinGlimpse.External.Crypto;
using CoinGlimpse.DataObjects;
using Microsoft.AspNetCore.Http;

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

            return data.LookupError switch
            {
                CoinLookupError.UnknownCoin => NotFound(new { code = "unknown_coin", message = data.msg }),
                CoinLookupError.ProviderUnavailable => ProviderUnavailable(),
                _ => data.msg != null ? BadRequest(data.msg) : Ok(data)
            };
        }

        [HttpGet]
        [Route("list")]
        public async Task<IActionResult> Get()
        {
            var cancellationToken = ControllerContext.HttpContext?.RequestAborted ?? CancellationToken.None;
            try
            {
                var catalog = await _cryptoManager.FetchCatalog(cancellationToken);
                if (catalog.Age is TimeSpan age && ControllerContext.HttpContext is { } context)
                {
                    context.Response.Headers["CoinGlimpse-Catalog-Age-Seconds"] =
                        ((long)age.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                    context.Response.Headers["CoinGlimpse-Catalog-Stale"] =
                        catalog.IsStale ? "true" : "false";
                }

                return Ok(catalog.Coins);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError("Coin catalog could not be loaded. ErrorType={ErrorType}", ex.GetType().Name);
                return ProviderUnavailable();
            }
        }

        private ObjectResult ProviderUnavailable() =>
            StatusCode(StatusCodes.Status502BadGateway, new
            {
                code = "provider_unavailable",
                message = "CoinGecko data is temporarily unavailable."
            });
    }
}
