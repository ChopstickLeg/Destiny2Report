# Rust crawler worker protocol v1

The ASP.NET Core API admits crawl jobs and the Rust worker is the only crawler implementation. Redis carries delivery and live status; MongoDB is authoritative for ownership and results. There is no RPC service and no backend-selection layer.

## Queue and ownership

- Jobs use the `crawler:jobs` Redis stream and `crawler-workers` consumer group.
- Every Rust process has a unique consumer name.
- Stream entries contain `protocolVersion`, `runId`, `membershipTypeId`, `membershipId`, `queuedAtUtc`, and `forceFullCrawl`.
- The API creates one active `crawl_jobs` run per 12-byte binary player key before dispatching it.
- A worker claims a queued run or reclaims an expired lease with one Mongo compare-and-set operation.
- A claim increments the per-player fence. Worker mutations match player key, run ID, expected state, fence, and lease owner.
- Losing a lease cancels outbound work and prevents stale progress, publication, acknowledgement, or cleanup.
- Redis acknowledgement occurs only after Mongo accepts the fenced candidate generation.

## Queryable Mongo storage

| Collection | Purpose |
| --- | --- |
| `crawl_jobs` | One mutable coordination document per player. |
| `reports` | Immutable generation-scoped BSON reports. |
| `crawl_state` | Immutable generation-scoped BSON incremental state. |
| `crawl_artifacts` | Immutable narrow BSON rows for weapons, deaths, emblems, and encounters. |

The player key is big-endian `int32 membershipType + int64 membershipId`. Reports and state are ordinary BSON subdocuments. Artifact rows use short field names and numeric kind, mode, class, hash, identity, and value fields. Nothing is compressed into an opaque Mongo value.

This is the initial storage schema. There are no migration readers, schema fallbacks, or older payload formats. Workers write state and artifacts, then the report, and finally assign `candidateGeneration` with a fenced update. Readers use only `activeGeneration`.

## C# application boundary

C# does not crawl Bungie players. It owns public APIs, report reads, leaderboards, notifications, administration, and finalization of completed Rust generations. Finalization makes a candidate generation active and performs those application-side effects idempotently.

## Per-instance request limits

Every Rust process owns independent ordinary, PGCR, and sherpa-history limiters. Redis and Mongo never distribute request permits. Scaling replicas intentionally increases aggregate configured throughput. A 429 pause applies only to the endpoint bucket in the replica that received it.

Settings are `CRAWLER__ORDINARY_REQUESTS_PER_SECOND_PER_INSTANCE`, `CRAWLER__PGCR_REQUESTS_PER_SECOND_PER_INSTANCE`, `CRAWLER__SHERPA_HISTORY_REQUESTS_PER_SECOND_PER_INSTANCE`, their corresponding queue limits, `CRAWLER__MAX_IN_FLIGHT_REQUESTS_PER_INSTANCE`, `CRAWLER__MAX_IN_FLIGHT_PGCRS_PER_INSTANCE`, and `CRAWLER__MAX_BUFFERED_PGCRS`.

Run multiple workers with `docker compose up --scale crawler-rust=3`.
