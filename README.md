# Hacker News Best Stories API

A small ASP.NET Core 10 microservice that returns the best `n` stories from
[Hacker News](https://github.com/HackerNews/API), ordered by score, without ever
letting client traffic reach Hacker News directly.

```
GET /api/stories/best?n=3
```

```json
[
  {
    "title": "A uBlock Origin update was rejected from the Chrome Web Store",
    "uri": "https://github.com/uBlockOrigin/uBlock-issues/issues/745",
    "postedBy": "ismaildonmez",
    "time": "2019-10-12T13:43:01+00:00",
    "score": 1716,
    "commentCount": 572
  },
  { "...": "..." },
  { "...": "..." }
]
```

## Contents

- [How it works in one paragraph](#how-it-works-in-one-paragraph)
- [Running the application](#running-the-application)
- [API contract](#api-contract)
- [Configuration](#configuration)
- [Design](#design)
- [Assumptions](#assumptions)
- [Tests](#tests)
- [What I would do with more time](#what-i-would-do-with-more-time)
- [Project layout](#project-layout)

## How it works in one paragraph

A single background service is the only thing that talks to Hacker News. Once
per refresh interval (60 s by default) it fetches `/v0/beststories.json`,
fetches every listed item with bounded parallelism (8 requests in flight), sorts
them by score and publishes an immutable snapshot. Client requests validate `n`,
read the current snapshot with a lock-free volatile read and return the first
`n` elements. Upstream load is therefore a constant that depends only on
configuration (at most `1 + 500` requests per minute per instance), no matter
how many clients call the API. If Hacker News is slow or down, the last good
snapshot keeps being served and its age is reported in the response headers.

## Running the application

### Prerequisites

- [.NET SDK 10.0](https://dotnet.microsoft.com/download/dotnet/10.0) (built and tested with 10.0.400)
- Docker (optional, for the container image)
- Outbound HTTPS access to `hacker-news.firebaseio.com`

### With the .NET CLI

```bash
dotnet run --project RestfulAPIDemo
```

The `http` launch profile listens on `http://localhost:5230` and opens
`/api/stories/best?n=10` in the browser. Use `--launch-profile https` for
`https://localhost:7157` (requires the ASP.NET Core developer certificate,
`dotnet dev-certs https --trust`).

```bash
curl -i "http://localhost:5230/api/stories/best?n=3"
```

Other useful URLs:

| URL | Purpose |
| --- | --- |
| `/api/stories/best?n=10` | The endpoint |
| `/openapi/v1.json` | OpenAPI 3 document |
| `/health` | Liveness: always `Healthy` while the process runs |
| `/health/ready` | Readiness: `503 Unhealthy` until the first snapshot exists, `200 Degraded` when the snapshot is older than `BestStories:StaleAfter` |

The file `RestfulAPIDemo/RestfulAPIDemo.http` contains ready-made requests for
Visual Studio / VS Code REST Client, including the error cases.

The first request after start-up waits for the initial refresh cycle (a few
seconds for the ~200 stories Hacker News currently lists). Every request after
that is served from memory.

### With Docker

```bash
docker build -t hn-best-stories .
docker run --rm -p 8080:8080 hn-best-stories
curl "http://localhost:8080/api/stories/best?n=3"
```

The image is a multi-stage build on `mcr.microsoft.com/dotnet/aspnet:10.0`,
runs as the non-root `app` user and listens on plain HTTP port 8080 (TLS is
expected to terminate at the ingress). Any setting can be overridden with
environment variables using the `Section__Key` convention:

```bash
docker run --rm -p 8080:8080 -e BestStories__RefreshInterval=00:02:00 -e BestStories__MaxConcurrentItemFetches=4 hn-best-stories
```

Container orchestrators should use `/health` as the liveness probe and
`/health/ready` as the readiness probe.

## API contract

### `GET /api/stories/best?n={n}`

| Aspect | Behaviour |
| --- | --- |
| `n` | Required query parameter. An integer between 1 and `BestStories:MaxStories` (default 500, Hacker News' documented list cap). Digits only: `010` is accepted as 10; `+10`, `1.5`, `1e2`, ` 10` are rejected. Repeating the parameter is rejected. |
| Success | `200 OK`, `Content-Type: application/json; charset=utf-8`, a bare JSON array of story objects in descending score order. Ties keep Hacker News' own list order. |
| Fewer than `n` stories | Returns all that are available (Hacker News lists about 200 today). This is not an error. |
| Story object | Exactly `title`, `uri`, `postedBy`, `time`, `score`, `commentCount`, in that order. `uri`, `postedBy` and `title` are strings or `null` (never omitted); `score` and `commentCount` are integers. `time` is ISO-8601 UTC with an explicit `+00:00` offset, e.g. `2019-10-12T13:43:01+00:00`. |
| Caching headers | `Cache-Control: public, max-age=<seconds until the next refresh>` and `Age: <seconds since the snapshot was taken>`. When the snapshot is stale because Hacker News is unreachable, `max-age` is `0` and `Age` keeps growing. |
| `400 Bad Request` | Invalid or missing `n`. RFC 9457 `application/problem+json` with an `errors.n` message and a `traceId`. |
| `503 Service Unavailable` | No snapshot has ever been loaded: Hacker News was unreachable at start-up, or the first cycle did not finish within `BestStories:ColdStartWait`. Carries `Retry-After` (seconds until the next refresh attempt) and `Cache-Control: no-store`. Once a snapshot exists the service never answers 503 again; it serves the last good data instead. |
| `404` / `405` | Unknown routes and non-GET methods also answer with `application/problem+json`. `HEAD` is supported. |

Example error:

```http
HTTP/1.1 400 Bad Request
Content-Type: application/problem+json

{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "One or more validation errors occurred.",
  "status": 400,
  "errors": { "n": ["The query parameter 'n' must be an integer between 1 and 500."] },
  "traceId": "00-5fa8dd6f73dcef328875fb9be67fab6f-4ab26fdea1a67f10-00"
}
```

## Configuration

All settings live in `RestfulAPIDemo/appsettings.json` and can be overridden
through any configuration source (environment variables use `__` as the section
separator, e.g. `BestStories__RefreshInterval=00:00:30`). Invalid values fail
fast at start-up with an `OptionsValidationException`.

### `BestStories`

| Key | Default | Meaning |
| --- | --- | --- |
| `RefreshInterval` | `00:01:00` | Pause between the end of one refresh cycle and the start of the next. Also the `max-age` of responses. Range 1 s to 1 day. |
| `RefreshTimeout` | `00:00:50` | Upper bound on a cycle's duration; must be less than `RefreshInterval` so cycles can never overlap. Items not fetched in time fall back to the previous snapshot. |
| `MaxStories` | `500` | Maximum ids fetched per cycle and the upper bound for `n`. Range 1 to 500. |
| `MaxConcurrentItemFetches` | `8` | Maximum item requests in flight to Hacker News at any moment. Range 1 to 64. |
| `ColdStartWait` | `00:00:10` | How long a request waits for the very first snapshot before answering 503. |
| `MinCoveragePercent` | `90` | A cycle is published only if at least this percentage of listed ids was resolved (fetched, legitimately excluded, or reused from the previous snapshot). Prevents a silently truncated "best" list when Hacker News is flaky. |
| `StaleAfter` | `00:05:00` | Snapshot age after which `/health/ready` reports `Degraded`. |

### `HackerNews`

| Key | Default | Meaning |
| --- | --- | --- |
| `BaseAddress` | `https://hacker-news.firebaseio.com/v0/` | Must end with `/`. |
| `AttemptTimeout` | `00:00:10` | Timeout for one HTTP attempt. |
| `TotalTimeout` | `00:00:30` | Timeout for a request including all retries; must exceed `AttemptTimeout`. |
| `MaxRetryAttempts` | `2` | Retries after the first attempt for transient failures (5xx, 408, 429, transport errors, attempt timeouts). 404 and `null` bodies are answers, not failures, and are never retried. |
| `RetryBaseDelay` | `00:00:00.300` | Base of the exponential back-off between retries; jitter is always applied. |
| `BreakDuration` | `00:00:30` | How long the circuit breaker stays open after Hacker News is judged unhealthy (50 % failures over at least 20 calls in a 30 s window). |

Logging defaults keep `System.Net.Http.HttpClient` and `Polly` at `Warning` so
the ~200 upstream calls per minute do not flood the log; each refresh cycle
writes a single `Information` line with its counts.

## Design

```
                     +--------------------------- RestfulAPIDemo process ----------------------------+
GET /api/stories/    |  BestStoriesEndpoints                                                          |
  best?n=10          |    validate n  -> 400 ValidationProblem (errors.n)                             |
-------------------->|    snapshot = store.Current            (volatile read, no lock, no I/O)        |
<--------------------|    if null: await the cycle in progress (bounded by ColdStartWait) -> 503      |
 200 [ ...n ]        |    set Cache-Control / Age; return snapshot.Top(n)                             |
 Cache-Control, Age  |                          ^                                                     |
                     |                          | Publish / FailCycle                                 |
                     |  SnapshotStore  { Current, WaitForCycleAsync(), NextRefreshAt }                |
                     |                          ^                                                     |
                     |  BestStoriesRefresher (BackgroundService -- the ONLY Hacker News caller)       |
                     |    loop: RunCycleAsync  ->  Task.Delay(RefreshInterval)                        |
                     |      ids     = client.GetBestStoryIdsAsync()   (null => cycle failed)          |
                     |      ids     = Dedupe(ids).Take(MaxStories)                                    |
                     |      results = Parallel.ForAsync(DOP = MaxConcurrentItemFetches) GetItemAsync  |
                     |      rank    = BestStoriesRanker.Build(ids, results, previousSnapshot)         |
                     |      publish, or keep the previous snapshot                                    |
                     |                                                                                |
                     |  HackerNewsClient  (IHttpClientFactory, named client "HackerNews")             |
                     |    + standard resilience handler: total timeout -> retry -> breaker -> attempt |
                     +--------------------------------------------------------------------------------+
                                                   |  <= 8 in flight, <= 1 cycle per RefreshInterval
                                                   v
                              https://hacker-news.firebaseio.com/v0/{beststories,item/{id}}.json
```

### Why a background refresher instead of a request-driven cache?

The requirement is to "efficiently service large numbers of requests without
risking overloading of the Hacker News API". A request-driven cache (fetch on
miss, cache for a TTL) does that too, but its upstream load is still a function
of request timing, it needs stampede protection at every TTL boundary, and cold
callers pay the full fan-out latency. With a time-driven refresher:

- Upstream load is a **constant** decided by configuration, not by clients.
  Whether 0 or 100 000 requests arrive per minute, Hacker News sees the same
  `1 + |ids|` calls.
- The request path has **no lock, no cache lookup and no I/O**: a volatile read
  and an array slice. It scales with CPU, not with Hacker News.
- Stampede protection is structural: there is exactly one writer. Cold-start
  callers share one `TaskCompletionSource`, so ten thousand of them wait on one
  task and resume on the thread pool when it completes.
- Serving stale data on failure is trivial: the old snapshot simply stays in
  place until a cycle succeeds.

The price is that the service polls Hacker News while idle. At the documented
cap that is at most 501 requests per minute per instance (201 today), which is
well within what a public Firebase endpoint is designed for. Idle gating is
listed under enhancements.

### Upstream protection, worst case

| Scenario | Arithmetic | Hacker News calls / minute |
| --- | --- | --- |
| Healthy, 200 ids listed (today) | 1 list + 200 items | 201 |
| Healthy, 500 ids (Hacker News' cap, enforced in code) | 1 + 500 | 501 |
| Instantaneous concurrency | list first, then `MaxConcurrentItemFetches` | <= 8 requests in flight |
| Hacker News completely down | list fails 1 + 2 retries, cycle aborts | 3, then ~1 probe per 30 s once the breaker opens |
| List works, items all fail | ~20-30 attempts until the breaker opens, then fail-fast in memory | ~30-40 |
| Hacker News slow (5 s per response) | `RefreshTimeout` truncates the cycle; unfetched items reuse the previous snapshot | ~80 |
| Client load of any size | requests never call Hacker News | no change |

Cycles cannot overlap because the delay is measured from the end of a cycle and
`RefreshTimeout < RefreshInterval` is enforced at start-up. Duplicate ids are
removed and the list is capped at `MaxStories` before fan-out, so the bound is
a property of the code rather than of Hacker News' behaviour.

### Failure handling

| Situation | Behaviour |
| --- | --- |
| Item is `null`, `deleted`, `dead`, or has no `time` | Excluded from the snapshot. Does not count toward `n`. |
| Item lacks `url` / `by` / `title` | Returned with `null` in the corresponding property (Ask HN posts have no URL). |
| Item lacks `descendants` / `score` | `commentCount: 0` / `score: 0`. |
| Item fetch fails after retries (5xx, timeout, malformed body) | The copy from the previous snapshot is reused ("last known good"). If there is no previous copy the id is unresolved. |
| More than `100 - MinCoveragePercent` % of ids unresolved | The cycle is rejected and the previous snapshot is kept, so callers never see a quietly truncated list. |
| `beststories.json` fails or is malformed | Cycle fails; previous snapshot kept; one `Warning` log line. |
| No snapshot yet and Hacker News is down | `503` with `Retry-After` until a cycle succeeds; `/health/ready` is `503` so orchestrators do not route traffic. |
| Unexpected exception in a cycle | Caught and logged at `Error`; the loop continues after the interval. The host never stops. |
| Shutdown during a cycle | The stopping token cancels in-flight requests; the loop exits promptly. |

### Snapshot semantics

A snapshot is "as of" the start of the cycle that produced it, so the reported
`Age` is never under-stated. Items within one snapshot are fetched over a few
seconds, so it is not a strict point-in-time view (one story's score may be a
few seconds newer than its neighbour's); at Hacker News' rate of change this is
irrelevant. All clients within one interval see the same ordering; consecutive
intervals may differ.

### Technology choices

- **Minimal APIs with `TypedResults`** for a single endpoint: correct OpenAPI
  metadata for free and no MVC stack to load.
- **`Microsoft.Extensions.Http.Resilience`** (Polly v8) standard handler rather
  than hand-rolled retries: timeouts, exponential back-off with jitter and a
  circuit breaker, configured from the options above.
- **`TimeProvider`** everywhere time is read or waited on, so the whole refresh
  loop, cache headers and cold-start wait are deterministic under test.
- **`ProblemDetails`** for every error (`AddProblemDetails`,
  `UseExceptionHandler`, `UseStatusCodePages`) with a `traceId` extension.
- **`JavaScriptEncoder.UnsafeRelaxedJsonEscaping`** so titles like `Foo & Bar`
  and non-ASCII characters are emitted verbatim. The output is still valid JSON
  and is only ever served as `application/json`, never embedded in HTML.
- **Source-generated logging** (`[LoggerMessage]`) and options validated with
  data annotations plus cross-field rules at start-up.
- **`TreatWarningsAsErrors` with the recommended analyzer set** for both
  projects.

## Assumptions

- "Best stories" are the ids Hacker News returns from `/v0/beststories.json`.
  Hacker News orders that list only approximately by score, so every listed item
  is fetched and the service sorts by the current `score` itself.
- Hacker News caps the list at 500 ids (about 200 today), so `n` is bounded by
  500. When fewer usable stories exist the API returns fewer than `n` items
  rather than an error.
- `n` is mandatory. Omitting it is a `400`, not a default page size.
- Items are passed through unmodified: no HTML entity decoding, trimming or
  URL normalisation. `uri` is `null` for text-only posts rather than a fabricated
  `news.ycombinator.com` permalink. `commentCount` is Hacker News' `descendants`
  (all nested comments), not the number of direct replies.
- Everything Hacker News lists is returned regardless of `type`, so a `poll`
  or `job` that makes the best list is included. Deleted, dead and time-less
  items are excluded.
- Equal scores are ordered by Hacker News' list position.
- Freshness of up to `RefreshInterval` plus the cycle duration (worst case about
  110 s with defaults) is acceptable. Hacker News sends `Cache-Control: no-cache`
  and no `ETag`, so conditional requests are impossible and a time-based refresh
  is the only viable strategy.
- Serving stale data indefinitely during a Hacker News outage is preferable to
  a `503` cliff. Staleness is visible through `Age`, `max-age=0` and the
  `Degraded` readiness state.
- The in-memory snapshot is per process. Running `k` replicas multiplies the
  Hacker News load by `k` (still bounded and small).
- No authentication or inbound rate limiting: this is an internal microservice
  behind a gateway that owns those concerns. TLS terminates at the edge; HTTPS
  redirection is enabled only in the Development environment.
- The service was verified against the live API on 2026-09-06: 200 ids listed,
  a cold start of about 4 s with 8 concurrent fetches, three text posts with
  `uri: null`, and the example story rendering exactly as in the specification.

## Tests

```bash
dotnet test
```

The suite runs completely offline. Hacker News is replaced by an in-memory
`HttpMessageHandler` that is installed as the *primary* handler of the named
client, so the real resilience pipeline is exercised too, and the clock is a
`FakeTimeProvider`, so cache ages, cold-start waits and refresh cadence are
asserted without sleeping.

| Area | What is covered |
| --- | --- |
| Unit: mapping and serialisation | Field-for-field mapping of the specification's example, `null` handling, `+00:00` time format, property order, relaxed escaping, culture independence |
| Unit: ranking | Score-descending order, deterministic ties, exclusions, last-known-good reuse, the coverage gate's integer arithmetic |
| Unit: Hacker News client | Every upstream outcome (`null`, 404, 5xx, HTML, truncated JSON, transport errors, cancellation) maps to the documented result without throwing |
| Unit: resilience pipeline | Retry then success, retry exhaustion, no retry on 404, total timeout on a hanging upstream, circuit breaker opening and half-opening (real clock, sub-second settings) |
| Unit: refresher and store | Cycle publishing, de-duplication and capping, bounded concurrency, timeout fallback, exception survival, one cycle per interval, prompt shutdown, single shared cold-start task |
| Unit: options | Every invalid setting fails start-up; overrides bind |
| Integration: contract and validation | Exact body for the specification's example, headers, every `n` failure mode, problem-details shapes for 400/404/405/503 |
| Integration: concurrency | 500 concurrent cold requests cause exactly one list fetch and one fetch per item with at most 8 in flight; warm requests cause zero upstream calls; one batch per refresh window |
| Integration: upstream failure | 503 on cold failure, stale-on-error with `Age`, item fallback, coverage rejection, recovery, readiness states |
| Live smoke (opt-in) | `HN_LIVE_TESTS=1 dotnet test --filter Category=Live` hits the real Hacker News API |

## What I would do with more time

- **Idle gating**: pause the refresh loop when no request has arrived for a few
  minutes and resume on the next one, so an idle instance costs Hacker News
  nothing.
- **Multi-instance coordination**: a leader-elected refresher (or a shared
  snapshot in Redis, e.g. through `HybridCache` as an L2) so `k` replicas
  produce one upstream load rather than `k`.
- **Persist the last snapshot** to disk or a cache so a restart during a Hacker
  News outage can serve data instead of `503`.
- **`ETag` / `If-None-Match`** on the response (the snapshot's fetch time is a
  natural validator) so downstream caches revalidate with 304s.
- **Tiered refresh**: re-fetch the top 50 every 30 s and the tail every few
  minutes, or drive updates from `/v0/updates.json`, to cut upstream calls
  further while keeping the head of the list fresher.
- **Observability**: OpenTelemetry metrics for snapshot age, cycle duration and
  outcome, breaker state and upstream latency; a Scalar/Swagger UI on top of the
  existing OpenAPI document.
- **Inbound hardening** if exposed publicly: `AddRateLimiter`, Kestrel
  connection limits, authentication.
- **Performance polish**: pre-serialise the snapshot to UTF-8 bytes once per
  cycle, `System.Text.Json` source generation and native AOT publish, response
  compression at the edge.
- **Delivery**: a CI pipeline running the tests, a k6 load test against the
  fake-backed app to document throughput, and API versioning (`/api/v2/...`) if
  the response shape ever has to change.

## Project layout

```
RestfulAPIDemo.slnx
Directory.Build.props              shared build settings (nullable, warnings as errors, analyzers)
Dockerfile, .dockerignore
RestfulAPIDemo/
  Program.cs                       composition root
  Log.cs                           source-generated log messages
  Api/
    BestStoriesEndpoints.cs        GET /api/stories/best
    NParser.cs                     validation of n
    Story.cs                       response contract
    BestStoriesOpenApiTransformer.cs
  BestStories/
    BestStoriesOptions.cs
    BestStoriesRefresher.cs        the background refresh loop (the only Hacker News caller)
    BestStoriesRanker.cs           pure ranking / coverage gate
    BestStoriesSnapshot.cs         immutable sorted snapshot
    SnapshotStore.cs               lock-free hand-over + shared cold-start task
    StoryMapper.cs                 Hacker News item -> Story
    SnapshotHealthCheck.cs         readiness
    BestStoriesServiceCollectionExtensions.cs
  HackerNews/
    HackerNewsClient.cs            non-throwing gateway over the named HttpClient
    HackerNewsItem.cs, ItemFetchResult.cs, IHackerNewsClient.cs, HackerNewsOptions.cs
    HackerNewsServiceCollectionExtensions.cs   HttpClient + resilience pipeline
  appsettings.json                 defaults documented above
RestfulAPIDemo.Tests/
  Fakes/                           fake Hacker News handler/client, test host factory, log capture
  Unit/, Integration/, Live/
```
