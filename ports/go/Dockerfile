# The Go implementation (docs/conformance.md). Build from the repository root:
#   docker build -f ports/go/Dockerfile -t sanction-and-forgive-go .
# The dictionaries and the pages come from the .NET projects, their one source of truth.
FROM golang:1.27 AS build
WORKDIR /src/ports/go
COPY ports/go/go.mod ports/go/go.sum ./
RUN go mod download
COPY ports/go/ ./
COPY src/SanctionAndForgive.Matching/Data/ matching/data/
COPY src/SanctionAndForgive.Api/wwwroot/ api/wwwroot/
RUN go vet ./... && go test ./...
ARG VERSION=dev
RUN CGO_ENABLED=0 go build -trimpath -ldflags "-s -w" -o /out/api ./cmd/api \
 && CGO_ENABLED=0 go build -trimpath -ldflags "-s -w" -o /out/cli ./cmd/cli

# Distroless: no shell, CA certificates for the feeds and Azure, non-root.
FROM gcr.io/distroless/static-debian12:nonroot
WORKDIR /app
COPY --from=build /out/api /out/cli /app/
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
