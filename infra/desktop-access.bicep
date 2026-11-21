// The two things outside the desktops' group that the API's identity needs to make a desktop: to put it on
// the desktops' subnet, and to read the image it is made from (foundation.bicep declares both).
param principalId string

// Virtual Machine Contributor carries "join a subnet" and nothing else that applies to one.
var machineContributor = '9980e02c-c2be-4d73-94e8-173b1dc7cf3c'
var reader = 'acdd72a7-3385-48ef-bd42-f606fba81ae7'

resource network 'Microsoft.Network/virtualNetworks@2024-05-01' existing = { name: 'workstream-net' }
resource subnet 'Microsoft.Network/virtualNetworks/subnets@2024-05-01' existing = { parent: network, name: 'desktops' }
resource gallery 'Microsoft.Compute/galleries@2024-03-03' existing = { name: 'workstream' }

resource join 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(subnet.id, principalId, machineContributor)
  scope: subnet
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', machineContributor)
  }
}

resource readImage 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(gallery.id, principalId, reader)
  scope: gallery
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', reader)
  }
}
