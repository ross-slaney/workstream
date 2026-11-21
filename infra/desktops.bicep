// The desktops' own resource group: the API's identity may make, start, stop and delete machines here, with
// the network interface and disk each one is made with (api/Desktops/DesktopHosts.cs). Nothing is declared
// in this group: what is in it is what the API put there.
param principalId string

// Virtual Machine Contributor: machines, their disks and their network interfaces.
var machineContributor = '9980e02c-c2be-4d73-94e8-173b1dc7cf3c'

resource machines 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(resourceGroup().id, principalId, machineContributor)
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', machineContributor)
  }
}
