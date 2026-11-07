# Destiny2Report Rust crawler

This is the application's only crawler. It consumes `crawler:jobs`, calls the required documented Bungie endpoints through a hand-written Reqwest client, and stages immutable, queryable BSON generations for the ASP.NET Core application's report and finalization services.

It exposes no network port and does not use protobuf or gRPC for application traffic, opaque compressed Mongo payloads, a distributed rate limiter, or a C# crawler compatibility path.

## OpenTelemetry

The crawler always writes structured JSON logs to stdout. When `OTEL_EXPORTER_OTLP_ENDPOINT` is set, it also exports spans, structured log events, and memory metrics through OTLP. Each signal carries a unique `service.instance.id` matching the worker's consumer name, plus `host.name` from the container's `HOSTNAME`.

- Set `OTEL_EXPORTER_OTLP_PROTOCOL` to `grpc` or `http/protobuf`.
- For OTLP/HTTP, configure the base endpoint; the exporter appends `/v1/traces`, `/v1/logs`, and `/v1/metrics`.
- Set `OTEL_EXPORTER_OTLP_AUTHORIZATION_HEADER` for a complete authorization value, or `OTEL_EXPORTER_OTLP_BEARER_TOKEN` for a bearer token.
- Signal-specific OTLP endpoint, protocol, and header variables remain supported by the OpenTelemetry exporter.
- Metrics use the existing OTLP authorization configuration. Set `OTEL_EXPORTER_OTLP_METRICS_ENDPOINT` and `OTEL_EXPORTER_OTLP_METRICS_PROTOCOL` only if the metrics receiver differs from the shared endpoint. The receiver must accept metrics as well as logs and traces. Jaeger by itself is a trace receiver, so use an OpenTelemetry Collector or another metrics-capable backend for memory charts.
- Metrics export every 60 seconds by default. Set `OTEL_METRIC_EXPORT_INTERVAL=15000` in milliseconds for more frequent samples. No new variables are required.

On Linux, `process.memory.usage` reports RSS and `process.memory.peak_usage` reports the process high-water mark, both in bytes. With the default Docker cgroup v2 namespace, `crawler.container.memory.usage`, `crawler.container.memory.limit`, and `crawler.container.memory.oom_kills` report charged memory, a finite configured limit, and OOM kills. Unavailable readings and an unlimited cgroup limit are omitted. Other operating systems can run the crawler without Linux memory readings.

Structured `crawler memory sample` events appear every 30 seconds and at manifest download, parsing, upload, player-history, activity-analysis, result-construction, and artifact-staging boundaries. Phase start and finish events include elapsed milliseconds. Correlate `phase`, `rss_bytes`, `peak_rss_bytes`, and container memory with the worker identity and trace ID. Manifest events include input size and definition count. Activity events include history, encountered-player, and weapon-aggregate counts. A rising high-water mark with low current RSS indicates a transient peak. Persistently high RSS after a phase ends warrants allocator or retained-state profiling. An OOM kill can terminate the process before its final event or metric export, so retain host or container OOM events too.

Run multiple replicas with `docker compose up --scale crawler-rust=3`. Configuration and ownership rules are documented in `../docs/crawler-protocol-v1.md`.

Manifest definitions use the existing MongoDB connection and are shared across replicas. Startup creates TTL indexes and populates English JSON tables; a table is published after its entire generation is uploaded. Definitions expire after one day. An hourly background loop refreshes tables with 12 hours or less remaining, including during long crawls. SQLite and `CRAWLER__MANIFEST_PATH` are no longer used. See `../docs/caching.md` for deployment and session-storage details.

Manifest ingestion retains the downloaded bytes and borrows each raw definition until its upload batch needs an owned JSON string. It avoids expanding the complete inventory table into nested JSON objects. Artifact BSON conversion uses batches of at most 500 input entries, and recent activity timestamps are trimmed after each history page to the 500 IDs needed by incremental crawls. Existing collections, generation publication, one-day TTLs, and one-off encounter filtering are unchanged. Deploy the updated crawler image to all workers. No database migration or additional service is required when the existing OTLP receiver already supports metrics.
