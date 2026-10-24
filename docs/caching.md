# Cache and session storage

The API keeps `Microsoft.Extensions.Caching.Hybrid` for cache access and concurrent miss coalescing. Its local reads and writes are disabled. `MongoDistributedCache` implements `IDistributedCache` using the existing `ConnectionStrings__MongoDb` and `Mongo__DatabaseName` settings.

| Data | Expiration |
| --- | --- |
| Bungie manifest and versioned manifest tables | 1 day |
| Parsed weapon definitions | 1 day |
| Leaderboard boards | 5 minutes |
| Leaderboard percentile thresholds | 1 day, refreshed every 12 hours |
| Queue-position snapshot | 30 seconds |
| Rust crawler manifest definitions | 1 day, refreshed every 12 hours |

Every distributed cache write has a hard one-day maximum, including entries with sliding expiration or no explicit expiration. Shorter caller expirations are preserved. Reads enforce expiration immediately; MongoDB TTL deletion can happen later.

Small values are stored in `cache_entries`. Values over 4 MiB use immutable 4 MiB documents in `cache_chunks`, with a generation pointer in `cache_entries`. This supports large manifest tables without exceeding MongoDB's 16 MiB BSON document limit. Writers publish the pointer after all chunks exist. Replaced, removed, and interrupted uploads leave chunks until their original TTL expires, allowing concurrent readers to finish without mixing generations. Startup creates TTL indexes on both collections.

Redis remains required for crawler streams, job status, pub/sub, admission quotas, leaderboard repair work, and locks. These coordinate work; application cache payloads no longer use Redis.

The Rust crawler downloads English JSON manifest tables into `manifest_entries`, with a published generation pointer and refresh lease per table in `manifest_tables`. Readers query MongoDB asynchronously. There is no SQLite file or retained process cache. Workers share refresh ownership and publish only complete tables. An independent hourly loop refreshes tables with 12 hours or less remaining, including during long crawls. Definition TTL indexes remove expired generations; reads reject expired tables and results that expire during cursor consumption. A failed refresh can use the previous generation until its one-day expiry, after which crawls fail until refresh succeeds.

Login sessions are persistent authentication state in `auth_sessions`, separate from the cache. They retain the configured 30-day lifetime, bounded by Bungie refresh-token expiry. The collection has its own TTL index and rejects expired sessions before cleanup.

## Deploy the change

1. Ensure the existing API and crawler MongoDB accounts can read, write, and create indexes in their configured database. New collections are `cache_entries`, `cache_chunks`, `auth_sessions`, `manifest_entries`, and `manifest_tables`.
2. Deploy the API and all crawler instances. No new environment variables or external services are required. Startup creates indexes and warms definitions. The first crawler startup populates the shared manifest and may take longer.
3. Remove `CRAWLER__MANIFEST_PATH` if it is configured; the crawler no longer reads it.
4. Sign in again. Existing Redis-backed login sessions are not migrated; new sessions retain their 30-day configured lifetime.

Old Redis cache keys are no longer read. They expire according to their original lifetimes. Do not flush Redis: it still contains crawler and coordination state. No data migration is required.

Disabling local caches means each cache hit uses MongoDB and deserializes its value. Parsed weapon summaries avoid reparsing the complete inventory manifest on every lookup.

## Leaderboard repair workload

Repairs read saved player scores in batches of 100. Scores are reused when their source crawl and metric version match the published report. Legacy version-0 snapshots use their publication timestamp as the freshness check. Missing or stale scores are recalculated and saved for subsequent repairs. Conditional writes preserve newer concurrent publications. Increment `PlayerLeaderboardSnapshot.CurrentMetricVersion` whenever metric formulas change; stored documents default to legacy version 0.

Fallback calculations project only required report fields and accumulator playtime. Removed players retain empty score tombstones so concurrent repairs cannot resurrect their scores; percentile thresholds exclude them. Repair failures back off exponentially from 30 seconds to five minutes. Requests arriving during a repair still schedule a follow-up pass so publication remains correct. Production CPU reduction must be measured after deployment.

## Verify against MongoDB

Set `MONGODB_CACHE_TEST_URI` to a test MongoDB server and run `dotnet test Destiny2Report.sln --configuration Release`. Integration tests create uniquely named test databases and drop them afterward. CI provides a MongoDB service and runs these tests. Without the variable, these integration tests are skipped.
