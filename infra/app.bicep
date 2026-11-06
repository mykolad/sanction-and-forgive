// The app itself, on the platform (platform.bicep): the Container App serving the website and API, and the daily
// import job, both from the same public image (ghcr.io, so no registry or pull role). Deployed by the deploy
// workflow after the migrations, with the commit's image tag.
targetScope = 'resourceGroup'

param location string = resourceGroup().location

@description('The image tag to run: the commit SHA the deploy workflow built.')
param imageTag string

param image string = 'ghcr.io/mykolad/sanction-and-forgive'

@description('One replica, always on (section 8). Raise only after measuring, and with the owner.')
param minReplicas int = 1
param maxReplicas int = 1

@description('vCPU per replica, as a string (Bicep has no decimals): 0.25 with 0.5Gi is the smallest size.')
param cpu string = '0.25'
param memory string = '0.5Gi'

@description('Screenings per minute per client IP; 0 turns the limit off (load tests only).')
param rateLimitPerMinute int = 120

@description('Client IP ranges (CIDR) allowed through the ingress; empty means open. The real ones stay out of the repo.')
param allowedCidrs array = []

@description('Client id of the admin sign-in app registration (infra/admin-sign-in.ps1). Empty: sign-in stays off.')
param adminClientId string = ''

var signInEnabled = !empty(adminClientId)

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
          image: '${image}:${imageTag}'
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
              // The app trusts the platform's principal header only when this template also turns sign-in on.
              name: 'Admin__SignInEnabled'
              value: string(signInEnabled)
            }
          ])
          probes: [
            {
              // The process answers; no database call.
              type: 'Liveness'
              httpGet: {
                path: '/healthz'
                port: 8080
              }
              periodSeconds: 30
              failureThreshold: 3
            }
            {
              // The first index load finished: until then the old revision keeps serving.
              type: 'Readiness'
              httpGet: {
                path: '/readyz'
                port: 8080
              }
              periodSeconds: 10
              failureThreshold: 3
            }
            {
              type: 'Startup'
              httpGet: {
                path: '/healthz'
                port: 8080
              }
              periodSeconds: 5
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
      replicaTimeout: 1800
      replicaRetryLimit: 0
    }
    template: {
      containers: [
        {
          name: 'import'
          image: '${image}:${imageTag}'
          command: [
            'dotnet'
            'SanctionAndForgive.Cli.dll'
            'import'
            '--source'
            'all'
          ]
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

// Built-in Microsoft Entra ID sign-in, for the admin pages only: unauthenticated requests pass (screening never needs
// sign-in) and the app decides from the principal header. No client secret: the ID token carries the app roles.
resource auth 'Microsoft.App/containerApps/authConfigs@2024-03-01' = if (signInEnabled) {
  parent: app
  name: 'current'
  properties: {
    platform: {
      enabled: true
    }
    globalValidation: {
      unauthenticatedClientAction: 'AllowAnonymous'
    }
    identityProviders: {
      azureActiveDirectory: {
        enabled: true
        registration: {
          clientId: adminClientId
          openIdIssuer: '${az.environment().authentication.loginEndpoint}${tenant().tenantId}/v2.0'
        }
        validation: {
          allowedAudiences: [
            adminClientId
          ]
        }
      }
    }
  }
}

output url string = 'https://${app.properties.configuration.ingress.fqdn}'
output latestRevision string = app.properties.latestRevisionName
