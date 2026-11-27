using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CoinGlimpse.External.Crypto;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoinGlimpse.Controllers
{
    [ApiController]
    [Route("api/watchlist")]
    public sealed class WatchlistController(IQuoteService manager) : ControllerBase
    {
        [HttpGet]
        public async Task<ActionResult<CoinQuoteBatch>> Get(
            [FromQuery] string ids, [FromQuery] string currency = "usd",
            CancellationToken cancellationToken = default)
        {
            try
            {
                return Ok(await manager.GetQuotesAsync(
                    (ids ?? string.Empty).Split(',', CryptoManager.MaxQuoteIds + 1),
                    currency, cancellationToken));
            }
            catch (ArgumentException ex) when (ex.ParamName is nameof(ids) or nameof(currency))
            {
                return BadRequest(new { code = "invalid_watchlist", message = ex.Message });
            }
            catch (CoinGeckoApiException ex) when (ex.StatusCode == StatusCodes.Status429TooManyRequests)
            {
                if (ex.RetryAfter is { } retryAfter)
                    Response.Headers["Retry-After"] = retryAfter.ToString();

                return StatusCode(StatusCodes.Status429TooManyRequests, new
                {
                    code = "provider_rate_limited",
                    message = "CoinGecko is rate limiting watchlist requests."
                });
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
                ex is CoinGeckoApiException or HttpRequestException or IOException or JsonException
                    or InvalidOperationException or OperationCanceledException)
            {
                return StatusCode(StatusCodes.Status502BadGateway, new
                {
                    code = "provider_unavailable",
                    message = "CoinGecko quotes are temporarily unavailable."
                });
            }
        }
    }
}
