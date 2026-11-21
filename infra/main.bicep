targetScope = 'subscription'
// The whole product in one deployment: `az deployment sub create -f infra/main.bicep`.
// Two container apps, the API (api.bicep) and the web app (web.bicep), on an environment inside a private
// network; and the desktops, one virtual machine per task, which the API makes and removes itself in a
// resource group that holds nothing else. ARM runs independent modules in parallel.
//
//   web (Expo export)     workstream-web.<the environment's domain>
//   API (.NET + SqlOS)    workstream-api.<the environment's domain>   REST, sign-in, the desktop relay,
//                                                                     the model gateway and the jobs
//   desktops              workstream-desktops/ws-<task>                no public address; reached only by the API

@description('API image already pushed to the registry, e.g. <registry>/workstream-api:v0.1.0.')
param image string
@description('Web image already pushed to the registry, e.g. <registry>/workstream-web:v0.1.0.')
param webImage string
param sourceSha string
@description('Validated published release tag from release.yml.')
param releaseTag string = ''
param location string = 'eastus2'
@description('The system administrator: the one address that reaches /admin, the GitHub variable ADMINISTRATOR_EMAIL. They sign in by emailed code like everyone else.')
param administratorEmail string
@description('The agents\' models: the Foundry resource\'s Anthropic endpoint, e.g. https://<resource>.services.ai.azure.com/anthropic. The resource and its deployments are made by hand (README "Models"), never here; empty runs with agents that say models are not set up.')
param modelEndpoint string = ''
@secure()
param modelKey string = ''

var name = 'workstream'
var desktopsGroupName = 'workstream-desktops'
var registryName = 'ws${uniqueString(subscription().id, name)}'
var tags = { project: name, environment: 'production', managedBy: 'bicep' }

resource group 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: name
  location: location
  tags: tags
}

// The desktops and nothing else, so what is in it is exactly what tasks are running on, and a stray is easy to see.
resource desktopsGroup 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: desktopsGroupName
  location: location
  tags: tags
}

// Registry, logs, the network, the container apps environment, email and the desktop image's gallery.
module foundation 'foundation.bicep' = {
  name: 'foundation'
  scope: group
  params: { location: location }
}

module data 'data.bicep' = {
  name: 'data'
  scope: group
  params: { name: name, location: location }
}

// Both apps pull their images with the one runtime identity (data.bicep).
module registry 'registry-access.bicep' = {
  name: 'registry-access'
  scope: group
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
  name: 'desktop-access'
  scope: group
  dependsOn: [foundation]
  params: { principalId: data.outputs.principalId }
}

var apiOrigin = 'https://${name}-api.${foundation.outputs.defaultDomain}'
var webOrigin = 'https://${name}-web.${foundation.outputs.defaultDomain}'

module api 'api.bicep' = {
  name: 'api'
  scope: group
  dependsOn: [registry, desktops, desktopAccess]
  params: {
    name: '${name}-api'
    location: location
    environmentId: foundation.outputs.environmentId
    registryHost: '${registryName}.azurecr.io'
    identityId: data.outputs.identityId
    clientId: data.outputs.clientId
    serverHost: data.outputs.serverHost
    image: image
    sourceSha: sourceSha
    release: releaseTag
    publicOrigin: apiOrigin
    webOrigin: webOrigin
    communicationName: foundation.outputs.communicationName
    sender: foundation.outputs.sender
    administratorEmail: administratorEmail
    desktopsGroup: desktopsGroup.name
    desktopsSubnetId: foundation.outputs.desktopsSubnetId
    desktopImageId: foundation.outputs.desktopImageId
    modelEndpoint: modelEndpoint
    modelKey: modelKey
  }
}

module web 'web.bicep' = {
  name: 'web'
  scope: group
  dependsOn: [registry]
  params: {
    name: '${name}-web'
    location: location
    environmentId: foundation.outputs.environmentId
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
