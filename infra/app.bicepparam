using 'app.bicep'

// The deploy workflow passes the commit's image tag; locally: $env:IMAGE_TAG = '<sha>'.
param imageTag = readEnvironmentVariable('IMAGE_TAG')

// A load test deploys with RATE_LIMIT_PER_MINUTE=0, then deploys again without it (spec section 7).
param rateLimitPerMinute = int(readEnvironmentVariable('RATE_LIMIT_PER_MINUTE', '120'))
