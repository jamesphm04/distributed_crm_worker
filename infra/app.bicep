// The worker's Container App, deployed by .github/workflows/deploy.yml into the resource group the
// distributed_crm_infra stack created. Shared names and IDs come from that stack's outputs.

@description('Azure region for every resource.')
param location string = resourceGroup().location

@description('Tags applied to every resource.')
param tags object = {
  project: 'distributed-crm'
  purpose: 'demo'
  owner: 'james'
}

@description('Image tag to deploy: the git commit SHA the image was built from.')
param imageTag string

@description('The dcrm-dev stack\'s outputs, as name: value.')
param stack stackOutputs

// The outputs this template uses. Others the stack has are ignored.
type stackOutputs = {
  containerAppsEnvironmentId: string
  registryLoginServer: string
  postgresHost: string
  postgresDatabaseName: string
  redisHost: string
  serviceBusNamespace: string
  appInsightsConnectionString: string
  workerIdentityId: string
  workerIdentityName: string
  workerClientId: string
}

// Health endpoints only; nothing outside the container reaches them except the platform's probes.
var healthPort = 8080

resource worker 'Microsoft.App/containerApps@2025-07-01' = {
  name: 'ca-dcrm-worker-dev'
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${stack.workerIdentityId}': {}
    }
  }
  properties: {
    environmentId: stack.containerAppsEnvironmentId
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      // No ingress: the worker takes its work from the queue, never from HTTP.
      registries: [
        {
          server: stack.registryLoginServer
          identity: stack.workerIdentityId
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'worker'
          image: '${stack.registryLoginServer}/distributed-crm-worker:${imageTag}'
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: [
            // No password or key in any of these three, so each signs in with a token for the worker's identity.
            {
              name: 'ConnectionStrings__DistributedCrm'
              value: 'Host=${stack.postgresHost};Database=${stack.postgresDatabaseName};Username=${stack.workerIdentityName};Ssl Mode=Require'
            }
            {
              name: 'ConnectionStrings__Redis'
              value: '${stack.redisHost}:10000,ssl=true'
            }
            {
              name: 'ConnectionStrings__ServiceBus'
              value: stack.serviceBusNamespace
            }
            {
              name: 'AZURE_CLIENT_ID'
              value: stack.workerClientId
            }
            {
              name: 'AZURE_TOKEN_CREDENTIALS'
              value: 'ManagedIdentityCredential'
            }
            {
              name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
              value: stack.appInsightsConnectionString
            }
          ]
          probes: [
            {
              type: 'Startup'
              httpGet: {
                path: '/health/live'
                port: healthPort
              }
              periodSeconds: 2
              failureThreshold: 30
            }
            {
              type: 'Liveness'
              httpGet: {
                path: '/health/live'
                port: healthPort
              }
              periodSeconds: 10
              failureThreshold: 3
            }
            // The worker serves no traffic, so readiness only marks the replica healthy in the portal and the
            // revision status; it still checks all three dependencies, which the worker can't work without.
            {
              type: 'Readiness'
              httpGet: {
                path: '/health/ready'
                port: healthPort
              }
              periodSeconds: 10
              timeoutSeconds: 6
              failureThreshold: 3
            }
          ]
        }
      ]
      // One consumer. Scaling on queue length (KEDA) is a Phase 15 question, answered by measurement.
      scale: {
        minReplicas: 1
        maxReplicas: 1
      }
    }
  }
}

output name string = worker.name
