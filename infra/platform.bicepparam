using 'platform.bicep'

// The owner's identity and email stay out of this public repo: set them in the shell locally, and as repository
// variables for the deploy workflow (infra/README.md).
param sqlAdminObjectId = readEnvironmentVariable('SQL_ADMIN_OBJECT_ID')
param sqlAdminLogin = readEnvironmentVariable('SQL_ADMIN_LOGIN')
param alertEmail = readEnvironmentVariable('ALERT_EMAIL')
