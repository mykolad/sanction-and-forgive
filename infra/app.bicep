// The app itself, on the platform (platform.bicep): the Container App serving the website and API, and the daily
// import job, both from the same public image (ghcr.io, so no registry or pull role). Deployed by the deploy
// workflow after the migrations, with the commit's image tag.
targetScope = 'resourceGroup'

param location string = resourceGroup().location

@description('The image tag to run: the commit SHA the deploy workflow built.')
param imageTag string

param image string = 'ghcr.io/mykolad/sanction-and-forgive'

@description('Which implementation runs (the experiment of 2026-11-09, docs/conformance.md): dotnet, the reference and the default, or a port (go, rust), whose image is tag <implementation>-<sha> of the same package.')
@allowed([
  'dotnet'
  'go'
  'rust'
])
param implementation string = 'dotnet'

@description('One replica, always on (section 8). Raise only after measuring, and with the owner.')
param minReplicas int = 1
param maxReplicas int = 1

@description('vCPU per replica, as a string (Bicep has no decimals). 0.5 with 1Gi since 2026-11-07 (owner\'s decision, after the load test: memory was the limit at 0.25 with 0.5Gi).')
param cpu string = '0.5'
param memory string = '1Gi'

@description('Screenings per minute per client IP; 0 turns the limit off (load tests only).')
param rateLimitPerMinute int = 120

@description('Client IP ranges (CIDR) allowed through the ingress; empty means open. The real ones stay out of the repo.')
param allowedCidrs array = []

@description('Client id of the admin sign-in app registration (infra/admin-sign-in.ps1). Empty: sign-in stays off.')
param adminClientId string = ''

// Every image serves the API on 8080 as its entry point and holds the CLI: .NET's as a DLL, a port's as /app/cli.
var imageReference = implementation == 'dotnet' ? '${image}:${imageTag}' : '${image}:${implementation}-${imageTag}'
var cliCommand = implementation == 'dotnet' ? [
  'dotnet'
  'SanctionAndForgive.Cli.dll'
] : [
  '/app/cli'
]

var name = 'sanction-and-forgive'

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' existing = {
  name: '${name}-cae'
}

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = {
  name: '${name}-app'
}

resource sqlServer 'Microsoft.Sql/servers@2023-08-01' existing = {
  name: '${name}-sql'
}

// Entra only: the app and the job sign in as the user-assigned identity, no password anywhere.
var connectionString = 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Database=${name}-db;Authentication=Active Directory Managed Identity;User Id=${identity.properties.clientId};Encrypt=True;Connect Timeout=60'

var sharedEnv = [
  {
    name: 'ConnectionStrings__Lists'
    value: connectionString
  }
  {
    name: 'AZURE_CLIENT_ID'
    value: identity.properties.clientId
  }
]

resource app 'Microsoft.App/containerApps@2024-03-01' = {
  name: name
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identity.id}': {}
    }
  }
  properties: {
    environmentId: environment.id
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      // Azure's defaults, stated so what-if doesn't report them as removals.
      maxInactiveRevisions: 100
      ingress: {
        external: true
        targetPort: 8080
        exposedPort: 0
        transport: 'auto'
        allowInsecure: false
        traffic: [
          {
            latestRevision: true
            weight: 100
          }
        ]
        ipSecurityRestrictions: [for (cidr, i) in allowedCidrs: {
          name: 'allowed-${i}'
          action: 'Allow'
          ipAddressRange: cidr
        }]
      }
    }
    template: {
      containers: [
        {
          name: 'api'
          image: imageReference
          resources: {
            cpu: json(cpu)
            memory: memory
          }
          env: concat(sharedEnv, [
            {
              name: 'RateLimiting__PerMinute'
              value: string(rateLimitPerMinute)
            }
            {
              // The admin sign-in, in the app (owner's decision 2026-11-11, docs/api.md): the app registration and
              // its tenant. Empty: sign-in off, and the admin features fail closed.
              name: 'Admin__ClientId'
              value: adminClientId
            }
            {
              name: 'Admin__TenantId'
              value: tenant().tenantId
            }
          ])
          // Every probe waits 5 s for an answer, not the platform's default 1 s: on a small replica a probe sometimes
          // took longer than 1 s while the app was nearly idle, and the replica was then restarted or taken out of
          // service mid-load (docs/load-test.md, "Probes").
          probes: [
            {
              // The process answers; no database call.
              type: 'Liveness'
              httpGet: {
                path: '/healthz'
                port: 8080
              }
              periodSeconds: 30
              timeoutSeconds: 5
              failureThreshold: 3
            }
            {
              // The first index load finished: until then the old revision keeps serving. Six misses in a row (a
              // minute) before a ready replica is taken out of service; becoming ready still takes one answer.
              type: 'Readiness'
              httpGet: {
                path: '/readyz'
                port: 8080
              }
              periodSeconds: 10
              timeoutSeconds: 5
              failureThreshold: 6
            }
            {
              type: 'Startup'
              httpGet: {
                path: '/healthz'
                port: 8080
              }
              periodSeconds: 5
              timeoutSeconds: 5
              failureThreshold: 24
            }
          ]
        }
      ]
      scale: {
        minReplicas: minReplicas
        maxReplicas: maxReplicas
      }
    }
  }
}

// The daily import of every active feed (section 4): same image, the CLI as the command.
resource importJob 'Microsoft.App/jobs@2024-03-01' = {
  name: '${name}-import'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identity.id}': {}
    }
  }
  properties: {
    environmentId: environment.id
    workloadProfileName: 'Consumption'
    configuration: {
      triggerType: 'Schedule'
      scheduleTriggerConfig: {
        cronExpression: '30 3 * * *'
        parallelism: 1
        replicaCompletionCount: 1
      }
      // An hour: a run takes about 3 minutes, but each feed may retry a passing fault for up to 10 minutes, and its
      // last attempt may take up to 10 more (FeedDownloader, DownloadRetry.Default).
      replicaTimeout: 3600
      replicaRetryLimit: 0
    }
    template: {
      containers: [
        {
          name: 'import'
          image: imageReference
          command: concat(cliCommand, [
            'import'
            '--source'
            'all'
          ])
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: sharedEnv
        }
      ]
    }
  }
}

// The platform's built-in sign-in is off: it ran as a sidecar in every replica (about 100 MiB of a small one's 512 under
// load, docs/load-test.md), and the app now signs administrators in itself. Deployed after the app (its parent), so
// the new revision, which never reads the platform's principal header, is in place before the sidecar goes.
resource auth 'Microsoft.App/containerApps/authConfigs@2024-03-01' = {
  parent: app
  name: 'current'
  properties: {
    platform: {
      enabled: false
    }
  }
}

output url string = 'https://${app.properties.configuration.ingress.fqdn}'
output latestRevision string = app.properties.latestRevisionName
