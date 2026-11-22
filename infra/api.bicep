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
@description('Email: the Communication Services resource it is sent with (foundation.bicep), as { group, name, sender }. Empty sends none: sign-in then needs passwordSignIn, and an invitation is a link to pass on.')
param communication object = {}
@description('Sign in with an email and a password beside the emailed code. On for a stage, off in production (api/Workstream.cs PasswordSignIn).')
param passwordSignIn bool = false
@description('The one address that reaches /admin; empty leaves nobody as administrator.')
param administratorEmail string = ''
@description('The administrator\'s password, for a stage; empty (production, always) sets none.')
@secure()
param administratorPassword string = ''
@description('The resource group desktops are made in (desktops.bicep).')
param desktopsGroup string
param desktopsSubnetId string
@description('Spares: how many may run at once over every team, and how many hours of spare time there may be in a day. A spare is a running machine nobody is using yet, so these two are what it can cost (api/Desktops/DesktopHosts.cs, SpareOptions). 0 at once turns spares off.')
param sparesAtOnce int = 0
param spareHoursADay int = 6
@description('The image version a desktop is made from: the one this deployment was made with.')
param desktopImageId string
@description('Whether a desktop hibernates when it is stopped (api/Desktops/DesktopHosts.cs, AzureOptions.Hibernate). Azure hibernates the Dasv6 sizes and not the Dasv7 ones the API otherwise uses, at the same cores, memory and price, so the sizes change with it.')
param desktopsHibernate bool = false
@description('The agents\' models (api/Model/ModelGateway.cs): the Foundry resource\'s Anthropic endpoint; empty leaves agents without one.')
param modelEndpoint string = ''
@secure()
param modelKey string = ''
@description('Sign in with GitHub (api/Workstream.cs SeedGitHubSignIn): the id of the GitHub OAuth client people sign in with, and its secret. Both, or the sign-in page shows no GitHub button and desktops get no GitHub token.')
param gitHubSignInClientId string = ''
@secure()
param gitHubSignInClientSecret string = ''

var models = !empty(modelEndpoint) && !empty(modelKey)
var gitHubSignIn = !empty(gitHubSignInClientId) && !empty(gitHubSignInClientSecret)
var email = !empty(communication)
var administrator = passwordSignIn && !empty(administratorEmail) && !empty(administratorPassword)

resource emailService 'Microsoft.Communication/communicationServices@2023-04-01' existing = if (email) {
  scope: resourceGroup(communication.?group ?? resourceGroup().name)
  name: communication.?name ?? 'none'
}

resource app 'Microsoft.App/containerApps@2025-01-01' = {
  name: name
  location: location
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${identityId}': {} } }
  properties: {
    managedEnvironmentId: environmentId
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      secrets: concat(email ? [{ name: 'acs', value: emailService!.listKeys().primaryConnectionString }] : [], models ? [{ name: 'model-key', value: modelKey }] : [], administrator ? [{ name: 'administrator-password', value: administratorPassword }] : [], gitHubSignIn ? [{ name: 'github-sign-in-client-secret', value: gitHubSignInClientSecret }] : [])
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
          // The identity the API signs in to Azure as, for the database and for making desktops.
          { name: 'AZURE_CLIENT_ID', value: clientId }
          // Production signs in by emailed code; the administrator is the one address that reaches /admin.
          { name: 'Workstream__Auth__PasswordSignIn', value: passwordSignIn ? 'true' : 'false' }
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
          { name: 'Workstream__Desktops__Spares__Enabled', value: sparesAtOnce > 0 ? 'true' : 'false' }
          { name: 'Workstream__Desktops__Spares__Max', value: string(sparesAtOnce) }
          { name: 'Workstream__Desktops__Spares__HoursPerDay', value: string(spareHoursADay) }
        ], desktopsHibernate ? [
          { name: 'Workstream__Desktops__Azure__Hibernate', value: 'true' }
          { name: 'Workstream__Desktops__Azure__Sizes__small', value: 'Standard_D2as_v6' }
          { name: 'Workstream__Desktops__Azure__Sizes__standard', value: 'Standard_D4as_v6' }
          { name: 'Workstream__Desktops__Azure__Sizes__large', value: 'Standard_D8as_v6' }
        ] : [], email ? [
          { name: 'SqlOS__EmailOtp__AzureCommunicationServicesConnectionString', secretRef: 'acs' }
          { name: 'SqlOS__EmailOtp__FromAddress', value: communication.sender }
        ] : [], administrator ? [
          { name: 'Workstream__Administrator__Password', secretRef: 'administrator-password' }
        ] : [], models ? [
          { name: 'Workstream__Model__Endpoint', value: modelEndpoint }
          { name: 'Workstream__Model__Key', secretRef: 'model-key' }
        ] : [], gitHubSignIn ? [
          { name: 'SqlOS__Oidc__GitHub__ClientId', value: gitHubSignInClientId }
          { name: 'SqlOS__Oidc__GitHub__ClientSecret', secretRef: 'github-sign-in-client-secret' }
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
