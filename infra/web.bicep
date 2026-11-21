// The web app: Expo's server output behind a small Node process (client/server.js, built by
// client/Dockerfile), rendering every page per request. No secrets, no database: it only pulls its image,
// with the runtime identity. One replica stays warm so the first visitor does not wait for a start.
param location string = resourceGroup().location
param name string
param environmentId string
param registryHost string
param identityId string
param image string
param sourceSha string
param release string = ''
resource app 'Microsoft.App/containerApps@2025-01-01' = {
  name: name
  location: location
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${identityId}': {} } }
  properties: {
    managedEnvironmentId: environmentId
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      registries: [{ server: registryHost, identity: identityId }]
      ingress: {
        external: true
        targetPort: 8080
        allowInsecure: false
        transport: 'auto'
      }
    }
    template: {
      containers: [{
        name: 'web'
        image: image
        resources: { cpu: json('0.25'), memory: '0.5Gi' }
        env: [
          { name: 'SOURCE_COMMIT_SHA', value: sourceSha }
          { name: 'WORKSTREAM_RELEASE', value: release }
        ]
        probes: [
          { type: 'Liveness', httpGet: { path: '/healthz', port: 8080 }, periodSeconds: 30, timeoutSeconds: 5, failureThreshold: 5 }
        ]
      }]
      scale: { minReplicas: 1, maxReplicas: 2, rules: [{ name: 'http', http: { metadata: { concurrentRequests: '100' } } }] }
    }
  }
}
output fqdn string = app.properties.configuration.ingress.fqdn
