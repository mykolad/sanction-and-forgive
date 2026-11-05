using 'platform.bicep'

// The owner's identity stays out of this public repo: set these in the shell locally, and as repository variables
// for the deploy workflow (infra/README.md). Alerts need no address: they go to the subscription's Owner role.
param sqlAdminObjectId = readEnvironmentVariable('SQL_ADMIN_OBJECT_ID')
param sqlAdminLogin = readEnvironmentVariable('SQL_ADMIN_LOGIN')
