// Everything app.bicep runs on, in the resource group bootstrap.bicep created: logs, the Container Apps environment,
// the app's identity, and the SQL server and database. Deployed before the migrations, so the database exists when
// they run (infra/README.md). Creates no role assignments: the deploy identity is only Contributor.
targetScope = 'resourceGroup'

param location string = resourceGroup().location

@description('Object ID of the SQL server\'s Entra admin (the owner). Kept out of the repo: SQL_ADMIN_OBJECT_ID.')
param sqlAdminObjectId string

@description('Login name shown for the SQL Entra admin. Kept out of the repo: SQL_ADMIN_LOGIN.')
param sqlAdminLogin string

@description('Where the storage alert is emailed. Kept out of the repo: ALERT_EMAIL.')
param alertEmail string

@description('Log Analytics daily ingestion cap in GB. Console logs only, so a small cap is plenty.')
param logDailyCapGb string = '0.2'

var name = 'sanction-and-forgive'

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${name}-logs'
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
    workspaceCapping: {
      dailyQuotaGb: json(logDailyCapGb)
    }
  }
}

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: '${name}-cae'
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs.properties.customerId
        sharedKey: logs.listKeys().primarySharedKey
      }
    }
    workloadProfiles: [
      {
        name: 'Consumption'
        workloadProfileType: 'Consumption'
      }
    ]
    zoneRedundant: false
  }
}

// One identity for the app and the import job. Its database user and rights: infra/sql/grant-identities.sql.
resource appIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${name}-app'
  location: location
}

resource sqlServer 'Microsoft.Sql/servers@2023-08-01' = {
  name: '${name}-sql'
  location: location
  properties: {
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    administrators: {
      administratorType: 'ActiveDirectory'
      azureADOnlyAuthentication: true
      principalType: 'User'
      login: sqlAdminLogin
      sid: sqlAdminObjectId
      tenantId: tenant().tenantId
    }
  }
}

// The app and the job reach SQL as Azure services. Entra-only authentication is the control, not the network.
// The deploy workflow opens the runner's IP for the migrations and closes it afterwards.
resource allowAzureServices 'Microsoft.Sql/servers/firewallRules@2023-08-01' = {
  parent: sqlServer
  name: 'AllowAllWindowsAzureIps'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

// Basic: 5 DTU, 2 GB, always on. Lists only, never user input. Backups stay local: the lists can be re-imported.
resource database 'Microsoft.Sql/servers/databases@2023-08-01' = {
  parent: sqlServer
  name: '${name}-db'
  location: location
  sku: {
    name: 'Basic'
    tier: 'Basic'
    capacity: 5
  }
  properties: {
    maxSizeBytes: 2147483648
    requestedBackupStorageRedundancy: 'Local'
  }
}

resource alerts 'Microsoft.Insights/actionGroups@2023-01-01' = {
  name: '${name}-alerts'
  location: 'global'
  properties: {
    groupShortName: 'sanctions'
    enabled: true
    emailReceivers: [
      {
        name: 'owner'
        emailAddress: alertEmail
        useCommonAlertSchema: true
      }
    ]
  }
}

// Each list keeps two imports in 2 GB. Past 80%, it's time to look before an import fails.
resource storageAlert 'Microsoft.Insights/metricAlerts@2018-03-01' = {
  name: '${name}-db-storage'
  location: 'global'
  properties: {
    description: 'Database storage above 80% of its 2 GB.'
    severity: 2
    enabled: true
    scopes: [
      database.id
    ]
    evaluationFrequency: 'PT1H'
    windowSize: 'PT1H'
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        {
          criterionType: 'StaticThresholdCriterion'
          name: 'storage'
          metricName: 'storage_percent'
          metricNamespace: 'Microsoft.Sql/servers/databases'
          operator: 'GreaterThan'
          threshold: 80
          timeAggregation: 'Maximum'
        }
      ]
    }
    actions: [
      {
        actionGroupId: alerts.id
      }
    ]
  }
}

output environmentId string = environment.id
output appIdentityId string = appIdentity.id
output appIdentityClientId string = appIdentity.properties.clientId
output sqlServerFqdn string = sqlServer.properties.fullyQualifiedDomainName
output databaseName string = database.name
