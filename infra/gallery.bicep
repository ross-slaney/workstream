// The desktops' image: one gallery, and a release bakes a version from desktop/image/
// (.github/actions/bake-desktop-image) into one of its two definitions and names it for itself: `machine`,
// or `machine-hibernate` where desktops hibernate (main.bicep `desktopsHibernate`). This is its own file because a release bakes
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

// The same image for desktops that hibernate when they are stopped (api/Desktops/DesktopHosts.cs, Hibernate).
// It is a definition of its own because Azure makes a machine that can hibernate only from a definition that
// says its image can, and a definition's features cannot be changed once it is made (adding this one to
// `machine` was refused). Azure also refuses Trusted Launch on a Linux machine that hibernates, so these
// versions are built on a machine without it (image-builder.bicep) and this definition does not require it.
resource hibernating 'Microsoft.Compute/galleries/images@2024-03-03' = {
  parent: gallery
  name: 'machine-hibernate'
  location: location
  properties: {
    osType: 'Linux'
    osState: 'Specialized'
    hyperVGeneration: 'V2'
    architecture: 'x64'
    identifier: { publisher: 'workstream', offer: 'machine', sku: 'debian-13-hibernate' }
    features: [
      { name: 'SecurityType', value: 'TrustedLaunchSupported' }
      { name: 'DiskControllerTypes', value: 'SCSI, NVMe' }
      { name: 'IsHibernateSupported', value: 'true' }
    ]
  }
}
