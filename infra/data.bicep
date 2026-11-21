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

output identityId string = identity.id
output principalId string = identity.properties.principalId
output clientId string = identity.properties.clientId
output serverHost string = server.properties.fullyQualifiedDomainName
