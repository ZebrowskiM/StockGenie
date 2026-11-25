# CoinGlimpse

CoinGlimpse is an ASP.NET Core API for retrieving cryptocurrency data from
CoinGecko. The production integration uses a small in-house `HttpClient`
wrapper rather than a third-party CoinGecko SDK.

## Supported endpoints

| CoinGlimpse route | CoinGecko route | Description |
|---|---|---|
| `GET /api/coin/list` | `/coins/list` | Returns supported coin IDs, names, and symbols. |
| `GET /api/coin?ticker=btc&currency=eur` | `/coins/{id}` | Looks up a ticker or CoinGecko ID and returns price, market cap, and volume in the selected currency. |

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
| `CoinGecko:MaxRetryAttempts` | `2` | Retries for rate-limit and server-error responses (capped at five). |
| `CoinGecko:EnableRequestLogging` | `false` | Enables routine request lifecycle and cache diagnostics. |

Coin details and prices are not cached; each coin lookup requests current
CoinGecko market data. Only the coin list is cached, using the configured
`ListCacheDuration`; the list cache is per `CryptoManager` instance and does not
persist across separate API requests with the current typed `HttpClient`
registration.

Only HTTPS base URLs are accepted, except loopback URLs for local development.
The client uses a 30-second timeout, disables automatic redirects, and observes
request cancellation. Retry delays honor `Retry-After` when supplied and are
capped at 30 seconds. Responses larger than 10 MB are rejected.

## Logging

The wrapper uses the application-provided `ILogger`. Set
`CoinGecko:EnableRequestLogging` to `true` to log request starts/completions,
HTTP method, endpoint template, attempt/status, elapsed time, response content
length, list-cache hits, and list refresh counts/cache duration. It defaults to
`false`; with it disabled, routine request logs and timing measurements are
skipped. Warnings and errors (including retry details) are still logged.

The wrapper does not log API keys, full request URLs/query strings, or response
bodies. `Logging:LogLevel` continues to control the emitted levels; for example,
request lifecycle details are logged at Information and cache hits at Debug.

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
[Marek Zebrowski's CoinGlimpse](https://github.com/ZebrowskiM/StockGenie).
GitHub's **Cite this repository** button uses the metadata in
[CITATION.cff](CITATION.cff). If reproducibility matters, include the commit
you used.
