// Everything app.bicep runs on, in the resource group bootstrap.bicep created: logs, the Container Apps environment,
// the app's identity, and the SQL server and database. Deployed before the migrations, so the database exists when
// they run (infra/README.md). Creates no role assignments: the deploy identity is only Contributor.
targetScope = 'resourceGroup'

param location string = resourceGroup().location

@description('Object ID of the SQL server\'s Entra admin (the owner). Kept out of the repo: SQL_ADMIN_OBJECT_ID.')
param sqlAdminObjectId string

@description('Login name shown for the SQL Entra admin. Kept out of the repo: SQL_ADMIN_LOGIN.')
param sqlAdminLogin string

@description('Log Analytics daily ingestion cap in GB. Console logs only, so a small cap is plenty.')
param logDailyCapGb string = '0.2'

@description('Monthly cost budget for the resource group, in the billing currency. Notifies at 80% spent and 100% forecast.')
param monthlyBudget int = 20

@description('The budget\'s first month (the first day of a month). Fixed: a budget\'s start can\'t move later.')
param budgetStartDate string = '2026-11-01T00:00:00Z'

@description('The budget\'s end, as Azure would default it (ten years), stated so what-if stays quiet.')
param budgetEndDate string = '2036-11-01T00:00:00Z'

var name = 'sanction-and-forgive'

// Alerts email whoever holds Owner directly on the subscription: Azure looks the address up, so none is stored here,
// in the repository variables or in the deploy logs.
var ownerRoleId = '8e3af657-a8ff-443c-a75c-2fe8c4bcb635'

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
    // Azure's defaults, stated so what-if doesn't report them as removals.
    peerAuthentication: {
      mtls: {
        enabled: false
      }
    }
    peerTrafficConfiguration: {
      encryption: {
        enabled: false
      }
    }
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
    armRoleReceivers: [
      {
        name: 'subscription-owners'
        roleId: ownerRoleId
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

// A month's spend: catches a surprise, like a load test left with the rate limit off.
resource budget 'Microsoft.Consumption/budgets@2023-11-01' = {
  name: '${name}-monthly'
  properties: {
    category: 'Cost'
    amount: monthlyBudget
    timeGrain: 'Monthly'
    timePeriod: {
      startDate: budgetStartDate
      endDate: budgetEndDate
    }
    notifications: {
      actual80: {
        enabled: true
        operator: 'GreaterThanOrEqualTo'
        threshold: 80
        thresholdType: 'Actual'
        // No address in the repo: the Owner role and the action group (which emails the Owner role too).
        contactEmails: []
        contactRoles: [
          'Owner'
        ]
        contactGroups: [
          alerts.id
        ]
      }
      forecast100: {
        enabled: true
        operator: 'GreaterThanOrEqualTo'
        threshold: 100
        thresholdType: 'Forecasted'
        // No address in the repo: the Owner role and the action group (which emails the Owner role too).
        contactEmails: []
        contactRoles: [
          'Owner'
        ]
        contactGroups: [
          alerts.id
        ]
      }
    }
  }
}

// The daily import failed for a source (the CLI prints "FAILED" or a rejection), or the job execution itself failed.
// A failed refresh keeps the old list, which then ages to Not checked: better to hear about it before a demo.
// union isfuzzy, column_ifexists and an empty datatable leg: the log tables only appear once something has been logged.
resource importFailed 'Microsoft.Insights/scheduledQueryRules@2023-12-01' = {
  name: '${name}-import-failed'
  location: location
  properties: {
    description: 'The daily list import failed, rejected a source, or held an update for review.'
    severity: 2
    enabled: true
    scopes: [
      logs.id
    ]
    evaluationFrequency: 'PT1H'
    windowSize: 'PT1H'
    criteria: {
      allOf: [
        {
          // A multi-line string isn't interpolated: the job's name goes in with replace().
          query: replace('''
union isfuzzy=true
  (datatable(Log_s: string) []),
  (ContainerAppConsoleLogs_CL
    | where tostring(column_ifexists("ContainerGroupName_s", "")) startswith "__JOB__"
    | where tostring(column_ifexists("Log_s", "")) has_any ("FAILED", "Rejected", "HeldForReview")),
  (ContainerAppSystemLogs_CL
    | where tostring(column_ifexists("JobName_s", "")) == "__JOB__"
    | where tostring(column_ifexists("Reason_s", "")) has_any ("Failed", "BackoffLimitExceeded", "DeadlineExceeded"))
''', '__JOB__', '${name}-import')
          timeAggregation: 'Count'
          operator: 'GreaterThan'
          threshold: 0
          failingPeriods: {
            numberOfEvaluationPeriods: 1
            minFailingPeriodsToAlert: 1
          }
        }
      ]
    }
    autoMitigate: false
    actions: {
      actionGroups: [
        alerts.id
      ]
    }
  }
}

output environmentId string = environment.id
output appIdentityId string = appIdentity.id
output appIdentityClientId string = appIdentity.properties.clientId
output sqlServerFqdn string = sqlServer.properties.fullyQualifiedDomainName
output databaseName string = database.name
