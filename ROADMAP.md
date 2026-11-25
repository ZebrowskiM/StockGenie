# CoinGlimpse roadmap

A developer should be able to clone CoinGlimpse, configure one free CoinGecko Demo key on the server, and draw a chart from their own frontend with one request. The .NET client should also work directly from C#. Fetch data on demand, share cached reads, and calculate indicators locally.

## Delivery order

### 1. [Verify shared catalog caching and deterministic coin lookup](https://github.com/ZebrowskiM/CoinGlimpse/issues/12)

First PR scope. The cache and a concurrent controller test landed in 45d00a9. Their build and test results still need verification.

- [ ] Run the .NET 11 restore, build and fixture-backed suite. Record failures. Preserve existing routes and the USD default.
- [ ] Test shared refresh, configurable TTL, caller cancellation, bounded stale fallback, refresh backoff, empty responses and cold-start recovery with fake HTTP and time.
- [ ] Trim and validate consistently, allow long IDs within a documented limit, prefer canonical IDs, and return candidates for duplicate symbols. Cover 400, 404, ambiguity and sanitized provider errors.

### 2. [Fetch ten watchlist quotes in one upstream request](https://github.com/ZebrowskiM/CoinGlimpse/issues/13)

First user-facing milestone. Expose a typed GetQuotesAsync method and a sample endpoint backed by Demo /simple/price.

- [ ] Normalize, deduplicate and cap canonical IDs. Cache equivalent batches by IDs and currency, bound cache size and isolate credentials.
- [ ] Return decimal values, individual missing results and separate provider and retrieval timestamps. Honor Retry-After within a bounded request budget.
- [ ] Fixture test ten coins in one call, zero calls for an equivalent cached read, independent currencies, stale or partial results, 429 and 5xx. Refresh on demand.

### 3. [Return chart-ready price history with SMA and EMA](https://github.com/ZebrowskiM/CoinGlimpse/issues/14)

Let a frontend plot a price series and two overlays with one request. Proposed route: GET /api/coins/bitcoin/analysis?currency=usd&days=90&interval=daily&sma=20&ema=50.

- [ ] Use Demo market_chart with an explicit interval and a 365-day lookback limit. Identify the CoinGecko aggregate series, currency, UTC timestamps and freshness.
- [ ] Compute SMA and EMA locally. Document EMA seeding, warm-up and gaps. Return aligned timestamps and nulls where an indicator is unavailable.
- [ ] Test calculations against hand-checkable fixtures. Changing periods on a cached series must use zero additional upstream calls. Consider RSI after both indicators pass.

### 4. [Expose exchange selection for current coin markets](https://github.com/ZebrowskiM/CoinGlimpse/issues/15)

Give frontends a venue picker using the developer’s existing CoinGecko Demo key.

- [ ] List exchange IDs from /exchanges/list and filter /coins/{id}/tickers using exchange_ids. Handle pagination and cache reads on demand.
- [ ] Return venue, base and quote assets, last price, timestamp and stale flag. Preserve the legacy ExchangeInfo platform field.
- [ ] Fixture-test exchange filtering and distinct cache entries. Label aggregate history separately from venue-specific tickers.

### 5. [Ship a clone-and-run frontend quickstart and reusable C# client](https://github.com/ZebrowskiM/CoinGlimpse/issues/16)

Make the first chart easy to render from a developer’s own frontend.

- [ ] Document a fixture-backed start and an opt-in Demo key via server-side environment or user secrets. Include a frontend fetch example, response shape, limits and attribution.
- [ ] Provide plain C# and DI examples, target .NET 10 LTS for the library and .NET 11 for the sample host, and verify both in CI.
- [ ] Pack locally, install into a clean sample and run the quickstart. Review data rights and defer publication or deployment until Marek chooses a release.
