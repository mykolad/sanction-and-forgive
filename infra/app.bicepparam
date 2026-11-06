using 'app.bicep'

// The deploy workflow passes the commit's image tag; locally: $env:IMAGE_TAG = '<sha>'.
param imageTag = readEnvironmentVariable('IMAGE_TAG')

// A load test deploys with RATE_LIMIT_PER_MINUTE=0, then deploys again without it (spec section 7).
param rateLimitPerMinute = int(readEnvironmentVariable('RATE_LIMIT_PER_MINUTE', '120'))

// The admin sign-in's app registration (repository variable ADMIN_CLIENT_ID, from infra/admin-sign-in.ps1); unset: off.
param adminClientId = readEnvironmentVariable('ADMIN_CLIENT_ID', '')

// Set by the deploy workflow only after it has checked the Container App's auth config (see deploy.yml, "App").
param adminSignInReady = bool(readEnvironmentVariable('ADMIN_SIGN_IN_READY', 'false'))
