using 'app.bicep'

// The deploy workflow passes the commit's image tag; locally: $env:IMAGE_TAG = '<sha>'.
param imageTag = readEnvironmentVariable('IMAGE_TAG')

// The implementation (dotnet, go or rust), as the deploy workflow's input chose it; Rust when unset (the owner's
// decision, 2026-11-12, after the load test without the sign-in sidecar: docs/load-test.md).
param implementation = readEnvironmentVariable('APP_IMPLEMENTATION', 'rust')

// The replica's size: 0.25 vCPU and 0.5Gi (the owner's decision, 2026-11-12; 0.5 and 1Gi from 2026-11-07); a load
// test may set APP_CPU and APP_MEMORY for its run, then deploys again without them.
param cpu = readEnvironmentVariable('APP_CPU', '0.25')
param memory = readEnvironmentVariable('APP_MEMORY', '0.5Gi')

// How many replicas run: two (the owner's decision, 2026-11-12; one before); a load test may set APP_REPLICAS for its
// run, then deploys again without it. Each replica holds every index and its own rate limit.
param minReplicas = int(readEnvironmentVariable('APP_REPLICAS', '2'))
param maxReplicas = int(readEnvironmentVariable('APP_REPLICAS', '2'))

// A load test deploys with RATE_LIMIT_PER_MINUTE=0, then deploys again without it (spec section 7).
param rateLimitPerMinute = int(readEnvironmentVariable('RATE_LIMIT_PER_MINUTE', '120'))

// The admin sign-in's app registration (repository variable ADMIN_CLIENT_ID, from infra/admin-sign-in.ps1); unset: off.
param adminClientId = readEnvironmentVariable('ADMIN_CLIENT_ID', '')
