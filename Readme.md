# CoinGlimpse

CoinGlimpse is an ASP.NET Core API for retrieving cryptocurrency data from
CoinGecko. The production integration uses a small in-house `HttpClient`
wrapper rather than a third-party CoinGecko SDK.

## Supported endpoints

| CoinGlimpse route | CoinGecko route | Description |
|---|---|---|
| `GET /api/coin/list` | `/coins/list` | Returns supported coin IDs, names, and symbols. |
| `GET /api/coin?ticker=btc&currency=eur` | `/coins/{id}` | Looks up a ticker or CoinGecko ID and returns price, market cap, and volume in the selected currency. |

The `ticker` input accepts a coin ID or symbol up to 100 characters after trimming.
Matching ignores case and gives a canonical ID priority over a symbol.
If several IDs share a symbol, the route returns HTTP 409 with a body such as
`{"code":"ambiguous_symbol","candidates":["alpha-coin","beta-coin"]}`.
Retry with one of the returned IDs. C# callers receive the choices in `Crypto.CandidateIds`.
Invalid input returns HTTP 400. An unknown coin returns HTTP 404 with
`unknown_coin`. If CoinGecko fails and usable data is unavailable, both routes
return HTTP 502 with `provider_unavailable` and a generic message.

The `currency` query parameter defaults to `usd`. For example,
`GET /api/coin?ticker=btc` returns USD values, while adding
`&currency=eur` requests EUR values. The response includes a `Currency` field
(`USD`, `EUR`, etc.) so clients can label the price and related market values.
Prices are returned as decimal JSON numbers without scientific notation (for
example, `0.00001001`).
Use a 2-10 character alphanumeric CoinGecko-supported currency code. If a
currency is malformed or unavailable for that coin, the API returns a
`BadRequest` response instead of silently substituting USD.
See CoinGecko's [live list of supported currencies](https://api.coingecko.com/api/v3/simple/supported_vs_currencies).

## Build and run

The projects target .NET 11. Build and test from the repository root:

```powershell
dotnet build .\CoinGlimpse.sln
dotnet test .\CoinGlimpse.Tests\CoinGlimpse.Tests.csproj
dotnet run --project .\CoinGlimpse.API
```

Configure the API port through the usual ASP.NET Core launch settings or
environment configuration.

## CoinGecko configuration

The API uses the Demo API by default. Keep the API key out of source control;
configure it with User Secrets for local development:

```powershell
dotnet user-secrets init --project .\CoinGlimpse.API
dotnet user-secrets set "CoinGecko:ApiKey" "YOUR_KEY" --project .\CoinGlimpse.API
```

`CoinGecko:Plan` selects the authentication header:

- `Demo` uses `x-cg-demo-api-key`.
- `Pro` uses `x-cg-pro-api-key`; also set `CoinGecko:BaseUrl` to `https://pro-api.coingecko.com/api/v3/`.

Relevant settings and defaults in `CoinGlimpse.API\appsettings.json`:

| Setting | Default | Purpose |
|---|---|---|
| `CoinGecko:BaseUrl` | `https://api.coingecko.com/api/v3/` | CoinGecko API base URL. |
| `CoinGecko:Plan` | `Demo` | Selects Demo or Pro key header. |
| `CoinGecko:ListCacheDuration` | `00:10:00` | Coin-list cache lifetime. |
| `CoinGecko:ListMaxStaleDuration` | `00:30:00` | Additional time an expired catalog can be served after refresh failure. Zero disables fallback. |
| `CoinGecko:ListRefreshBackoff` | `00:01:00` | Minimum time between a failed catalog refresh and the next attempt. |
| `CoinGecko:MaxRetryAttempts` | `2` | Retries for rate-limit and server-error responses (capped at five). |
| `CoinGecko:EnableRequestLogging` | `false` | Enables routine request lifecycle and cache diagnostics. |

Coin detail requests fetch current market data. The host shares the catalog across
requests and refreshes it on demand after its lifetime expires. If a refresh
fails or returns an empty list, callers can use the previous catalog during
the configured stale window. Once that window ends, callers receive the error.
The next request after the backoff can retry, including after a failed first load.

Successful `GET /api/coin/list` responses include `CoinGlimpse-Catalog-Age-Seconds`
with whole seconds since the last successful fetch and `CoinGlimpse-Catalog-Stale`
with `true` when the catalog has passed its configured lifetime. The body is
the array of coins. These headers describe the catalog.

Only HTTPS base URLs are accepted, except loopback URLs for local development.
The client uses a 30-second timeout, disables automatic redirects, and observes
request cancellation. Retry delays honor `Retry-After` when supplied and are
capped at 30 seconds. Responses larger than 10 MB are rejected.

## Daily price history

Call `await manager.GetDailyPriceHistoryAsync("bitcoin", days: 90)` to fetch
CoinGecko aggregate prices in USD, or pass another quote currency. Each call
makes an on-demand market-chart request with `interval=daily` and accepts
1–365 days. The result contains decimal prices in chronological order, the
provider’s original UTC observation timestamps and a separate retrieval time.
These are sampled prices. The latest observation can represent a partial day.
[CoinGecko Demo market chart documentation](https://docs.coingecko.com/demo/reference/coins-id-market-chart)

## Watchlist quotes

Call `GET /api/watchlist?ids=bitcoin,ethereum&currency=usd` with one to 50
comma-separated CoinGecko IDs from `/api/coin/list`. Currency defaults to USD.
The response contains `currency`, `retrievedAt`, and an array of `quotes` with
`coinId`, `price`, and `providerUpdatedAt`. Missing prices and provider timestamps
are returned as null, and cached responses retain their original retrieval time.
Invalid input returns HTTP 400. Provider rate limits return HTTP 429 and other
provider failures return HTTP 502. Watchlist upstream calls have a ten-second
budget, including retry delays. CoinGlimpse honors both forms of CoinGecko's
`Retry-After` header. When a rate-limit delay does not fit the remaining
budget, the API returns HTTP 429 promptly and forwards the retry guidance.

Each quote includes `isStale`. It becomes true when the provider price reaches
`CoinGecko:QuoteMaxAge` (five minutes by default), including on cache hits.
A missing price or a missing or future provider timestamp yields null.
The .NET meter `CoinGlimpse.Watchlist` exposes `watchlist.upstream.requests`
(including retries), `watchlist.cache.hits`, and `watchlist.upstream.rate_limits`
(one per upstream HTTP 429). These counters have no credential or coin ID tags.

### Watchlist quote cache

`CryptoManager.GetQuotesAsync` caches equivalent ID sets per currency, CoinGecko
base URL, plan, and key. The ASP.NET Core host shares the cache across requests.
Directly constructed managers use their own cache unless given a shared one.
`CoinGecko:QuoteCacheDuration` defaults to `00:01:00` and
`CoinGecko:QuoteCacheCapacity` defaults to 128 batches. Set either to zero to
disable caching. Reads refresh on demand after expiry and keep the original
retrieval timestamp on cache hits. Concurrent equivalent misses share one fetch.
Cancelling a caller ends its wait. A provider 429 pauses new watchlist fetches
across batches using the same provider, plan and credential in this process.
Cached quotes remain available. Without retry guidance the pause is one second.
Retries reserve at least one second of the budget for the next attempt.

The sample host has no per-client rate limit. Different ID sets can spend
credits from the same key. Before sharing a deployment, authenticate callers
and enforce per-client quotas at the host or gateway. A successful empty
provider result uses the normal short cache lifetime and returns null prices.

## Logging

The wrapper uses the application-provided `ILogger`. Set
`CoinGecko:EnableRequestLogging` to `true` to log request starts/completions,
HTTP method, endpoint template, attempt/status, elapsed time, response content
length, and the number of coins loaded on refresh. It defaults to
`false`; with it disabled, routine request logs and timing measurements are
skipped. Warnings and errors (including retry details) are still logged.

The wrapper does not log API keys, full request URLs/query strings, or response
bodies. `Logging:LogLevel` continues to control the emitted levels; for example,
request lifecycle details are logged at Information.

## Performance benchmarks

`CoinGlimpse.Benchmarks` compares cold list and coin lookups with the former
`CoinGeckoAsyncApi` 1.5.0 client. The old package is referenced only by the
benchmark project; it is not a production dependency. The benchmarks also
measure cached coin-list calls; coin-detail lookups are always cold because
prices are not cached. A deterministic local HTTP handler avoids network
variation and API rate limits.

Run the benchmark suite in Release mode:

```powershell
dotnet run -c Release --project .\CoinGlimpse.Benchmarks -- --filter "*CoinListBenchmarks*" "*CoinDetailBenchmarks*"
```

BenchmarkDotNet writes reports to `BenchmarkDotNet.Artifacts\results` at the
repository root. Generated benchmark reports and benchmark build outputs are
ignored by Git. Compare measurements on the same machine, runtime, and build
configuration. The local handler measures client-side request setup, JSON
parsing, mapping, and caching; it does not measure real network latency.

## License and citation

The original project code and documentation are licensed under the [Apache
License 2.0](LICENSE). See [NOTICE](NOTICE) for attribution. Third-party
dependencies and CoinGecko data are not covered by this grant and remain
subject to their own terms.

If you reference this project in a paper, post, or other work, please cite
[Marek Zebrowski's CoinGlimpse](https://github.com/ZebrowskiM/CoinGlimpse).
GitHub's **Cite this repository** button uses the metadata in
[CITATION.cff](CITATION.cff). If reproducibility matters, include the commit
you used.
