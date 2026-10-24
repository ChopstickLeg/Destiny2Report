# Destiny2Report Rust crawler

This is the application's only crawler. It consumes `crawler:jobs`, calls the required documented Bungie endpoints through a hand-written Reqwest client, and stages immutable, queryable BSON generations for the ASP.NET Core application's report and finalization services.

It exposes no network port and does not use protobuf or gRPC for application traffic, opaque compressed Mongo payloads, a distributed rate limiter, or a C# crawler compatibility path.

## OpenTelemetry

The crawler always writes structured JSON logs to stdout. When `OTEL_EXPORTER_OTLP_ENDPOINT` is set, it also exports both spans and structured log events through OTLP.

- Set `OTEL_EXPORTER_OTLP_PROTOCOL` to `grpc` or `http/protobuf`.
- For OTLP/HTTP, configure the base endpoint; the exporter appends `/v1/traces` and `/v1/logs`.
- Set `OTEL_EXPORTER_OTLP_AUTHORIZATION_HEADER` for a complete authorization value, or `OTEL_EXPORTER_OTLP_BEARER_TOKEN` for a bearer token.
- Signal-specific OTLP endpoint, protocol, and header variables remain supported by the OpenTelemetry exporter.

Run multiple replicas with `docker compose up --scale crawler-rust=3`. Configuration and ownership rules are documented in `../docs/crawler-protocol-v1.md`.

Manifest definitions use the existing MongoDB connection and are shared across replicas. Startup creates TTL indexes and populates English JSON tables; a table is published after its entire generation is uploaded. Definitions expire after one day. An hourly background loop refreshes tables with 12 hours or less remaining, including during long crawls. SQLite and `CRAWLER__MANIFEST_PATH` are no longer used. See `../docs/caching.md` for deployment and session-storage details.
