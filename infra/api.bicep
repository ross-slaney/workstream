// The API: .NET + SqlOS. One process is the REST API, sign-in, the relay that carries a desktop's display
// to the people watching it, the gateway every agent's model calls go through, and the background jobs that
// make and remove desktops. One replica stays warm: startup runs SchemaSync, and the jobs run here.
param location string = resourceGroup().location
param name string
param environmentId string
param registryHost string
param identityId string
param clientId string
param serverHost string
param image string
param sourceSha string
param release string = ''
@description('The origin the API answers on. Issuer and token audience derive from it, and it is where a desktop calls back.')
param publicOrigin string
@description('The origin the web app is served from: the only origin CORS allows, and the sign-in redirect.')
param webOrigin string
@description('The Communication Services resource email is sent with (foundation.bicep).')
param communicationName string
@description('The address every email comes from.')
param sender string
param administratorEmail string
@description('The resource group desktops are made in (desktops.bicep).')
param desktopsGroup string
param desktopsSubnetId string
@description('The gallery image a desktop is made from; its newest version is used.')
param desktopImageId string
@description('The agents\' models (api/Model/ModelGateway.cs): the Foundry resource\'s Anthropic endpoint; empty leaves agents without one.')
param modelEndpoint string = ''
@secure()
param modelKey string = ''

var models = !empty(modelEndpoint) && !empty(modelKey)

resource communication 'Microsoft.Communication/communicationServices@2023-04-01' existing = { name: communicationName }

resource app 'Microsoft.App/containerApps@2025-01-01' = {
  name: name
  location: location
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${identityId}': {} } }
  properties: {
    managedEnvironmentId: environmentId
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      secrets: concat([{ name: 'acs', value: communication.listKeys().primaryConnectionString }], models ? [{ name: 'model-key', value: modelKey }] : [])
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
        name: 'api'
        image: image
        resources: { cpu: json('0.5'), memory: '1Gi' }
        env: concat([
          { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
          { name: 'ASPNETCORE_HTTP_PORTS', value: '8080' }
          { name: 'PUBLIC_ORIGIN', value: publicOrigin }
          { name: 'WEB_ORIGIN', value: webOrigin }
          { name: 'SOURCE_COMMIT_SHA', value: sourceSha }
          { name: 'WORKSTREAM_RELEASE', value: release }
          { name: 'CONNECTION_STRING', value: 'Server=tcp:${serverHost},1433;Database=Workstream;Authentication=Active Directory Managed Identity;User Id=${clientId};Encrypt=True;TrustServerCertificate=False;Connection Timeout=60;Command Timeout=300;' }
          { name: 'SqlOS__EmailOtp__AzureCommunicationServicesConnectionString', secretRef: 'acs' }
          { name: 'SqlOS__EmailOtp__FromAddress', value: sender }
          // The identity the API signs in to Azure as, for the database and for making desktops.
          { name: 'AZURE_CLIENT_ID', value: clientId }
          // Sign-in is by emailed code; the administrator is the one address that reaches /admin.
          { name: 'Workstream__Auth__PasswordSignIn', value: 'false' }
          { name: 'Workstream__Administrator__Email', value: administratorEmail }
          // There is no separate worker: this process runs the jobs.
          { name: 'Workstream__Jobs__InProcess', value: 'true' }
          { name: 'Workstream__Desktops__Host', value: 'azure' }
          { name: 'Workstream__Desktops__ApiUrl', value: publicOrigin }
          { name: 'Workstream__Desktops__Azure__SubscriptionId', value: subscription().subscriptionId }
          { name: 'Workstream__Desktops__Azure__ResourceGroup', value: desktopsGroup }
          { name: 'Workstream__Desktops__Azure__Location', value: location }
          { name: 'Workstream__Desktops__Azure__SubnetId', value: desktopsSubnetId }
          { name: 'Workstream__Desktops__Azure__ImageId', value: desktopImageId }
        ], models ? [
          { name: 'Workstream__Model__Endpoint', value: modelEndpoint }
          { name: 'Workstream__Model__Key', secretRef: 'model-key' }
        ] : [])
        probes: [
          { type: 'Startup', httpGet: { path: '/health', port: 8080 }, initialDelaySeconds: 5, periodSeconds: 10, timeoutSeconds: 5, failureThreshold: 90 }
          { type: 'Liveness', httpGet: { path: '/health', port: 8080 }, periodSeconds: 30, timeoutSeconds: 5, failureThreshold: 5 }
          { type: 'Readiness', httpGet: { path: '/ready', port: 8080 }, periodSeconds: 10, timeoutSeconds: 5, failureThreshold: 3 }
        ]
      }]
      // A person watching a desktop holds one connection open for as long as they watch.
      scale: { minReplicas: 1, maxReplicas: 2, rules: [{ name: 'http', http: { metadata: { concurrentRequests: '50' } } }] }
    }
  }
}
