# The Rust implementation (docs/conformance.md). Build from the repository root:
#   docker build -f ports/rust/Dockerfile -t sanction-and-forgive-rust .
# The dictionaries and the pages come from the .NET projects, their one source of truth (embedded by build.rs); the
# OpenAPI document is the one the Go port keeps.
FROM rust:1.99-bookworm AS build
WORKDIR /src
COPY src/SanctionAndForgive.Matching/Data/ src/SanctionAndForgive.Matching/Data/
COPY src/SanctionAndForgive.Api/wwwroot/ src/SanctionAndForgive.Api/wwwroot/
COPY ports/go/api/openapi.json ports/go/api/openapi.json
COPY ports/rust/ ports/rust/
WORKDIR /src/ports/rust
# linux-gnu, not musl: f64::ln must be glibc's log, as .NET's Math.Log is (README.md).
RUN cargo test --release --locked && cargo build --release --locked --bin api --bin cli

# The licences of the libraries compiled into api and cli (about.toml: Linux target, no dev- or build-dependencies),
# gathered by cargo about from Cargo.lock in a stage of its own, so it runs alongside the build. --fail: a library
# whose licence can't be read or isn't accepted stops the build.
FROM rust:1.99-bookworm AS notices
RUN cargo install cargo-about --version 0.9.3 --locked --features cli
WORKDIR /src/ports/rust
COPY ports/rust/ ./
RUN cargo fetch --locked && cargo about generate --frozen --fail about.hbs -o /THIRD-PARTY-CODE.txt

# Distroless with glibc (for the logarithm): no shell, CA certificates for the feeds and Azure, non-root.
FROM gcr.io/distroless/cc-debian12:nonroot
WORKDIR /app
COPY --from=build /src/ports/rust/target/release/api /src/ports/rust/target/release/cli /app/
# The licence, and the notices for the dictionaries derived from OpenCC and Unicode CLDR data (all embedded).
COPY LICENSE THIRD-PARTY-NOTICES.txt /app/
# The licences of the libraries compiled into the binaries.
COPY --from=notices /THIRD-PARTY-CODE.txt /app/
EXPOSE 8080
ARG VERSION=dev
ARG REVISION=
ARG COMMIT_DATE=
ARG SOURCE_URL=
LABEL org.opencontainers.image.version=$VERSION \
      org.opencontainers.image.revision=$REVISION \
      org.opencontainers.image.created=$COMMIT_DATE \
      org.opencontainers.image.source=$SOURCE_URL \
      org.opencontainers.image.licenses=MIT
ENV App__Version=$VERSION \
    App__Revision=$REVISION \
    App__CommitDate=$COMMIT_DATE \
    App__SourceUrl=$SOURCE_URL
ENTRYPOINT ["/app/api"]
