# API cache storage

The API keeps `Microsoft.Extensions.Caching.Hybrid` for cache access and concurrent miss coalescing. Its local reads and writes are disabled. `MongoDistributedCache` implements `IDistributedCache` using the existing `ConnectionStrings__MongoDb` and `Mongo__DatabaseName` settings.

| Data | Expiration |
| --- | --- |
| Bungie manifest and versioned manifest tables | 1 day |
| Parsed weapon definitions | 1 day |
| Leaderboard boards | 5 minutes |
| Leaderboard percentile thresholds | 1 day, refreshed every 12 hours |
| Queue-position snapshot | 30 seconds |
| Login sessions and cookies | At most 1 day, bounded by refresh-token expiry |

Every distributed cache write has a hard one-day maximum, including entries with sliding expiration or no explicit expiration. Shorter caller expirations are preserved. Reads enforce expiration immediately; MongoDB TTL deletion can happen later.

Small values are stored in `cache_entries`. Values over 4 MiB use immutable 4 MiB documents in `cache_chunks`, with a generation pointer in `cache_entries`. This supports large manifest tables without exceeding MongoDB's 16 MiB BSON document limit. Writers publish the pointer after all chunks exist. Replaced, removed, and interrupted uploads leave chunks until their original TTL expires, allowing concurrent readers to finish without mixing generations. Startup creates TTL indexes on both collections.

Redis remains required for crawler streams, job status, pub/sub, admission quotas, leaderboard repair work, and locks. These coordinate work; application cache payloads no longer use Redis. The Rust crawler's private SQLite manifest is its lookup database and remains separate from the API cache.

## Deploy the change

1. Ensure the API's existing MongoDB account can read, write, and create indexes in `Mongo__DatabaseName`, including `cache_entries` and `cache_chunks`.
2. Deploy the API. No new environment variables or external services are required. Startup creates the collections and TTL indexes and warms weapon definitions.
3. Sign in again. Existing Redis-backed login sessions are not migrated; new sessions last at most one day, even if `AuthSession__Lifetime` is configured longer.

Old Redis cache keys are no longer read. They expire according to their original lifetimes. Do not flush Redis: it still contains crawler and coordination state. No data migration is required.

Disabling local caches means each cache hit uses MongoDB and deserializes its value. Parsed weapon summaries avoid reparsing the complete inventory manifest on every lookup, but this migration alone does not establish a CPU improvement.

## Verify against MongoDB

Set `MONGODB_CACHE_TEST_URI` to a test MongoDB server and run `dotnet test Destiny2Report.sln --configuration Release`. Integration tests create uniquely named test databases and drop them afterward. CI provides a MongoDB service and runs these tests. Without the variable, these integration tests are skipped.
