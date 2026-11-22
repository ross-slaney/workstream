targetScope = 'subscription'
// The whole product in one deployment: `az deployment sub create -f infra/main.bicep`.
// Two container apps, the API (api.bicep) and the web app (web.bicep), on an environment inside a private
// network; and the desktops, one virtual machine per task, which the API makes and removes itself in a
// resource group that holds nothing else. ARM runs independent modules in parallel.
//
//                         production                                  a stage (e.g. staging)
//   web (Expo export)     workstream-web.<the environment's domain>   workstream-<stage>-web.<…>
//   API (.NET + SqlOS)    workstream-api.<…>                          workstream-<stage>-api.<…>
//   desktops              workstream-desktops/ws-<task>               workstream-<stage>-desktops/ws-<task>
//
// Production owns what is shared (foundation.bicep: the registry, the network, the environment, email, the
// desktops' image). A stage is its own database, its own two apps and its own desktops on that shared
// foundation, signs in with a password so it can be tried without an inbox, and sends no email.

@description('Empty for production. A name, e.g. staging, for a second copy to try things on (.github/workflows/staging.yml).')
param stage string = ''
@description('API image already pushed to the registry, e.g. <registry>/workstream-api:v0.1.0.')
param image string
@description('Web image already pushed to the registry, e.g. <registry>/workstream-web:v0.1.0.')
param webImage string
param sourceSha string
@description('Validated published release tag from release.yml; a stage has none.')
param releaseTag string = ''
@description('Where everything runs. The Foundry resource is elsewhere and does not move with this.')
param location string = 'westus3'
@description('The system administrator: the one address that reaches /admin, the GitHub variable ADMINISTRATOR_EMAIL. In production they sign in by emailed code like everyone else. Empty leaves nobody as administrator.')
param administratorEmail string = ''
@description('A stage only: the administrator\'s password, the secret ADMINISTRATOR_PASSWORD on the GitHub environment `staging`. Never passed on in production.')
@secure()
param administratorPassword string = ''
@description('The version of the desktops\' image this deployment makes its desktops from, e.g. 0.2.0: the one its release baked, or the newest before it (scripts/desktop-image-version.sh). It is in the gallery before this runs.')
param desktopImageVersion string
@description('Whether a desktop hibernates when it is stopped, so that starting it again brings back every program as it was. Such desktops are made on the machine sizes Azure can hibernate, without Trusted Launch, from the gallery\'s machine-hibernate definition, which desktopImageVersion is then a version of (api.bicep, gallery.bicep). Set by the workflow that deploys (release.yml, staging.yml), which bakes into the same definition.')
param desktopsHibernate bool = false
@description('The agents\' models: the Foundry resource\'s Anthropic endpoint, e.g. https://<resource>.services.ai.azure.com/anthropic. The resource and its deployments are made by hand (README "Models"), never here; empty runs with agents that say models are not set up.')
param modelEndpoint string = ''
@secure()
param modelKey string = ''
@description('Sign in with GitHub: the Client ID of the GitHub OAuth client people sign in with, the GitHub variable GH_OAUTH_CLIENT_ID, and its secret, the secret GH_OAUTH_CLIENT_SECRET (infra/README.md, "Sign in with GitHub"). The client is made by hand, once; without both the sign-in page shows no GitHub button and desktops get no GitHub token.')
param gitHubSignInClientId string = ''
@secure()
param gitHubSignInClientSecret string = ''

var production = empty(stage)
var shared = 'workstream'
var name = production ? shared : '${shared}-${stage}'
var suffix = uniqueString(subscription().id, shared)
var registryName = 'ws${suffix}'
var tags = { project: shared, environment: production ? 'production' : stage, managedBy: 'bicep' }

resource group 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: name
  location: location
  tags: tags
}

// The desktops and nothing else, so what is in it is exactly what this copy's tasks are running on. The API
// removes any machine here that no task of its own knows, so two copies never share one.
resource desktopsGroup 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: '${name}-desktops'
  location: location
  tags: tags
}

// Registry, logs, the network, the container apps environment, email and the desktop image's gallery:
// production's, and shared.
module foundation 'foundation.bicep' = if (production) {
  name: 'foundation'
  scope: group
  params: { location: location }
}

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' existing = {
  scope: resourceGroup(shared)
  name: 'workstream-apps'
}

resource emailDomain 'Microsoft.Communication/emailServices/domains@2023-04-01' existing = {
  scope: resourceGroup(shared)
  name: 'workstream-email-${suffix}/AzureManagedDomain'
}

module data 'data.bicep' = {
  name: 'data'
  scope: group
  params: { name: name, location: location }
}

// Both apps pull their images with the one runtime identity (data.bicep). Named for the copy: the stage's
// cleanup finds this deployment by name.
module registry 'registry-access.bicep' = {
  name: '${name}-registry'
  scope: resourceGroup(shared)
  dependsOn: [foundation]
  params: { registryName: registryName, principalId: data.outputs.principalId }
}

// What lets the API make and remove desktops: machines in their own group, on the desktops' subnet, from the gallery's image.
module desktops 'desktops.bicep' = {
  name: 'desktops'
  scope: desktopsGroup
  params: { principalId: data.outputs.principalId }
}
module desktopAccess 'desktop-access.bicep' = {
  name: '${name}-desktop-access'
  scope: resourceGroup(shared)
  dependsOn: [foundation]
  params: { principalId: data.outputs.principalId }
}

var apiOrigin = 'https://${name}-api.${environment.properties.defaultDomain}'
var webOrigin = 'https://${name}-web.${environment.properties.defaultDomain}'

module api 'api.bicep' = {
  name: 'api'
  scope: group
  dependsOn: [foundation, registry, desktops, desktopAccess]
  params: {
    name: '${name}-api'
    location: location
    environmentId: environment.id
    registryHost: '${registryName}.azurecr.io'
    identityId: data.outputs.identityId
    clientId: data.outputs.clientId
    serverHost: data.outputs.serverHost
    image: image
    sourceSha: sourceSha
    release: releaseTag
    publicOrigin: apiOrigin
    webOrigin: webOrigin
    // Production sends sign-in codes and invitations; a stage signs in with a password and sends nothing.
    communication: production ? { group: shared, name: 'workstream-comm-${suffix}', sender: 'DoNotReply@${emailDomain.properties.mailFromSenderDomain}' } : {}
    passwordSignIn: !production
    administratorEmail: administratorEmail
    administratorPassword: production ? '' : administratorPassword
    desktopsGroup: desktopsGroup.name
    desktopsSubnetId: resourceId(subscription().subscriptionId, shared, 'Microsoft.Network/virtualNetworks/subnets', 'workstream-net', 'desktops')
    desktopImageId: resourceId(subscription().subscriptionId, shared, 'Microsoft.Compute/galleries/images/versions', 'workstream', desktopsHibernate ? 'machine-hibernate' : 'machine', desktopImageVersion)
    desktopsHibernate: desktopsHibernate
    modelEndpoint: modelEndpoint
    modelKey: modelKey
    gitHubSignInClientId: gitHubSignInClientId
    gitHubSignInClientSecret: gitHubSignInClientSecret
  }
}

module web 'web.bicep' = {
  name: 'web'
  scope: group
  dependsOn: [foundation, registry]
  params: {
    name: '${name}-web'
    location: location
    environmentId: environment.id
    registryHost: '${registryName}.azurecr.io'
    identityId: data.outputs.identityId
    image: webImage
    sourceSha: sourceSha
    release: releaseTag
  }
}

output origin string = webOrigin
output apiOrigin string = apiOrigin
output group string = group.name
