param location string = resourceGroup().location
param name string
extension microsoftGraphV1

// The one identity both container apps run as: it pulls their images, owns the database's schema, and, for
// the API, makes and removes desktops (desktops.bicep, desktop-access.bicep).
resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${name}-runtime'
  location: location
}

// The server's one Entra administrator is a group: the runtime, which owns the schema because SqlOS and
// SchemaSync deploy at startup, and whoever reads or fixes the data by hand (README "Database access").
// Bicep keeps the runtime in the group and never removes anyone: adding and removing people is
// `az ad group member add|remove`, not a deploy. The deploying principal needs Microsoft Graph
// `Group.ReadWrite.All` (scripts/setup-azure.sh).
resource sqlAdmins 'Microsoft.Graph/groups@v1.0' = {
  displayName: '${name}-sql-admins'
  uniqueName: '${name}-sql-admins'
  mailNickname: '${name}-sql-admins'
  mailEnabled: false
  securityEnabled: true
  members: { relationships: [identity.properties.principalId] }
}

resource server 'Microsoft.Sql/servers@2023-08-01' = {
  name: '${name}-${uniqueString(subscription().id, resourceGroup().name)}'
  location: location
  properties: {
    version: '12.0'
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    administrators: {
      administratorType: 'ActiveDirectory'
      principalType: 'Group'
      login: sqlAdmins.displayName
      sid: sqlAdmins.id
      tenantId: tenant().tenantId
      azureADOnlyAuthentication: true
    }
  }
}

resource database 'Microsoft.Sql/servers/databases@2023-08-01' = {
  parent: server
  name: 'Workstream'
  location: location
  // Basic (5 DTU, 2 GB): $4.90 a month, the same every month. Serverless would never pause, because the
  // jobs poll every few seconds.
  sku: { name: 'Basic', tier: 'Basic', capacity: 5 }
  properties: {
    maxSizeBytes: 2147483648
    requestedBackupStorageRedundancy: 'Local'
    zoneRedundant: false
  }
}

resource firewall 'Microsoft.Sql/servers/firewallRules@2023-08-01' = {
  parent: server
  name: 'AzureServices'
  properties: { startIpAddress: '0.0.0.0', endIpAddress: '0.0.0.0' }
}

// Where the iPhone builds' files are kept: the source a desktop sends, and the app and the log a Mac hands
// back (api/Builds/Builds.cs). Private: no public access and no account key, only the runtime identity,
// which reads and writes the one container as Storage Blob Data Contributor. The API removes what it no
// longer needs, so there is no lifecycle rule.
resource builds 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: 'wsb${uniqueString(subscription().id, resourceGroup().name)}'
  location: location
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: {
    accessTier: 'Hot'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
  }
}

resource buildsBlobs 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: builds
  name: 'default'
}

resource buildsContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: buildsBlobs
  name: 'builds'
  properties: { publicAccess: 'None' }
}

// Storage Blob Data Contributor, on this account alone.
resource buildsAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: builds
  name: guid(builds.id, identity.id, 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
  properties: {
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
  }
}

output buildsContainer string = '${builds.properties.primaryEndpoints.blob}builds'
output identityId string = identity.id
output principalId string = identity.properties.principalId
output clientId string = identity.properties.clientId
output serverHost string = server.properties.fullyQualifiedDomainName
