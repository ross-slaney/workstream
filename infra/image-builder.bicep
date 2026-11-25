// The machine the desktops' image is built on: a new Debian 13 in a resource group of its own, which
// .github/actions/bake-desktop-image makes, runs desktop/image/bake.sh on, captures into the gallery and
// deletes. It has a public address to reach the internet with and a rule set that admits nothing: the bake is
// run through Azure's run command, not over the network.
//
// Trusted Launch is the builder's because it is the image's: a disk built with it goes into the `machine`
// definition and no other, and a disk built without it into `machine-hibernate` and not into `machine`
// (gallery.bicep). Azure refused both the other ways round when they were tried.
param location string = resourceGroup().location
@description('Whether the builder, and so the image captured from it, has Trusted Launch. False for the image of desktops that hibernate.')
param trustedLaunch bool = true
@secure()
@description('Nobody signs in to the builder. Azure wants an account all the same.')
param adminPassword string = ''
@description('A version of the gallery\'s own image to start from, for the builder the iPhone simulator is added to (.github/actions/add-iphone-to-image). Empty starts from Debian. Such an image is specialized, so the machine is what the image was and is given no account.')
param fromImage string = ''
@description('The builder\'s size. The one the simulator is added to is a Large desktop\'s machine, which is what the simulator runs on.')
param size string = 'Standard_D4as_v7'


resource rules 'Microsoft.Network/networkSecurityGroups@2024-05-01' = {
  name: 'builder'
  location: location
}

resource network 'Microsoft.Network/virtualNetworks@2024-05-01' = {
  name: 'builder'
  location: location
  properties: {
    addressSpace: { addressPrefixes: ['10.0.0.0/24'] }
    subnets: [{ name: 'builder', properties: { addressPrefix: '10.0.0.0/24', networkSecurityGroup: { id: rules.id } } }]
  }
}

resource address 'Microsoft.Network/publicIPAddresses@2024-05-01' = {
  name: 'builder'
  location: location
  sku: { name: 'Standard' }
  properties: { publicIPAllocationMethod: 'Static' }
}

resource card 'Microsoft.Network/networkInterfaces@2024-05-01' = {
  name: 'builder'
  location: location
  properties: {
    ipConfigurations: [
      {
        name: 'ip'
        properties: { subnet: { id: network.properties.subnets[0].id }, publicIPAddress: { id: address.id } }
      }
    ]
  }
}

resource builder 'Microsoft.Compute/virtualMachines@2024-11-01' = {
  name: 'builder'
  location: location
  properties: {
    hardwareProfile: { vmSize: size }
    storageProfile: {
      imageReference: empty(fromImage) ? { publisher: 'Debian', offer: 'debian-13', sku: '13-gen2', version: 'latest' } : { id: fromImage }
      osDisk: { createOption: 'FromImage', diskSizeGB: 64, managedDisk: { storageAccountType: 'Premium_LRS' } }
    }
    osProfile: empty(fromImage) ? { computerName: 'builder', adminUsername: 'builder', adminPassword: adminPassword } : null
    // Left out, not set to Standard: Azure takes that word only on a subscription that asked to be allowed it.
    securityProfile: trustedLaunch
      ? { securityType: 'TrustedLaunch', uefiSettings: { secureBootEnabled: true, vTpmEnabled: true } }
      : null
    networkProfile: { networkInterfaces: [{ id: card.id }] }
  }
}
