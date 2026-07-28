# Destiny2Report Rust crawler

This is the application's only crawler. It consumes `crawler:jobs`, calls the required documented Bungie endpoints through a hand-written Reqwest client, and stages immutable, queryable BSON generations for the ASP.NET Core application's report and finalization services.

It exposes no network port and does not use protobuf, gRPC, opaque compressed Mongo payloads, a distributed rate limiter, or a C# crawler compatibility path.

Run multiple replicas with `docker compose up --scale crawler-rust=3`. Configuration and ownership rules are documented in `../docs/crawler-protocol-v1.md`.
