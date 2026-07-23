FROM rust:1.88-slim-bookworm AS build
WORKDIR /src
COPY crawler-rs/Cargo.toml crawler-rs/Cargo.lock* ./
COPY crawler-rs/src ./src
RUN cargo build --locked --release

FROM debian:bookworm-slim AS final
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /src/target/release/destiny2report-crawler /usr/local/bin/destiny2report-crawler
USER 65532:65532
ENTRYPOINT ["destiny2report-crawler"]
