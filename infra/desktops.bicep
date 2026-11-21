// The desktops' own resource group: the API's identity may make, start, stop and delete machines here, with
// the network interface and disk each one is made with, and the snapshots environments are saved as
// (api/Desktops/DesktopHosts.cs). Nothing is declared in this group: what is in it is what the API put there.
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

// Disk Snapshot Contributor: an environment's saved setup is a snapshot of the disk of the machine that
// prepared it, and a task's disk is made from it. Virtual Machine Contributor carries disks but not snapshots.
var snapshotContributor = '7efff54f-a5b4-42b5-a1c5-5411624893ce'

resource snapshots 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(resourceGroup().id, principalId, snapshotContributor)
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', snapshotContributor)
  }
}
