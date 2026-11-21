// The desktops' image: one gallery with one image in it. A release bakes a version from desktop/image/
// (.github/actions/bake-desktop-image) and names it for itself. This is its own file because a release bakes
// the image before it deploys anything else, so the place to keep it has to be there first; foundation.bicep
// includes it.
//
// The image is specialized: a machine made from a version starts as it is, with nothing to provision
// (desktop/image/seal.sh). Each deployment's API makes its desktops from one version, the one it was
// deployed with (main.bicep `desktopImageVersion`), so a new version changes nothing until the release that
// baked it is deployed.
param location string = resourceGroup().location

resource gallery 'Microsoft.Compute/galleries@2024-03-03' = {
  name: 'workstream'
  location: location
}

resource machine 'Microsoft.Compute/galleries/images@2024-03-03' = {
  parent: gallery
  name: 'machine'
  location: location
  properties: {
    osType: 'Linux'
    osState: 'Specialized'
    hyperVGeneration: 'V2'
    architecture: 'x64'
    identifier: { publisher: 'workstream', offer: 'machine', sku: 'debian-13' }
    // The v7 sizes boot from NVMe disks only.
    features: [
      { name: 'SecurityType', value: 'TrustedLaunch' }
      { name: 'DiskControllerTypes', value: 'SCSI, NVMe' }
    ]
  }
}
