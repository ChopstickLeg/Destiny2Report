FROM rust:1.88-slim-bookworm AS build
WORKDIR /src
COPY crawler-rs/Cargo.toml crawler-rs/Cargo.lock* ./
COPY crawler-rs/src ./src
RUN --mount=type=cache,target=/usr/local/cargo/registry,sharing=locked \
    --mount=type=cache,target=/src/target,sharing=locked \
    cargo build --locked --release \
    && cp /src/target/release/destiny2report-crawler /tmp/destiny2report-crawler

FROM build AS test
RUN --mount=type=cache,target=/usr/local/cargo/registry,sharing=locked \
    --mount=type=cache,target=/src/target,sharing=locked \
    cargo test --locked --release

FROM debian:bookworm-slim AS final
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /tmp/destiny2report-crawler /usr/local/bin/destiny2report-crawler
USER 65532:65532
ENTRYPOINT ["destiny2report-crawler"]
