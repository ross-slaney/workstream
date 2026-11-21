// What everything else stands on, made once and kept: the registry, the logs, the private network, the
// container apps environment inside it, email, and the gallery that holds the desktops' image.
// release.yml deploys this file by itself on a fresh installation, because the images need the registry and
// the web image needs the environment's domain before main.bicep can run; main.bicep then deploys it again.
param location string = resourceGroup().location

var suffix = uniqueString(subscription().id, 'workstream')
var networkPrefix = '10.60.0.0/16'
var appsPrefix = '10.60.0.0/23'
var desktopsPrefix = '10.60.16.0/20'
// A desktop's display (desktop/start.sh), which only the API's relay connects to.
var displayPort = '5900'

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: 'ws${suffix}'
  location: location
  sku: { name: 'Basic' }
  properties: { adminUserEnabled: false }
}

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'workstream-logs'
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
    workspaceCapping: { dailyQuotaGb: json('0.2') }
  }
}

// --- The network ------------------------------------------------------------------------------------------
// Two subnets. `apps` is the container apps environment's. `desktops` is where every task's machine sits: no
// machine has a public address, they reach the internet through one NAT gateway, and the rules below admit
// one thing in, the API's relay to a desktop's display, and refuse every other machine on the network,
// other desktops included. A desktop reaches the API the way anything else does, at its public address.
resource natAddress 'Microsoft.Network/publicIPAddresses@2024-05-01' = {
  name: 'workstream-nat'
  location: location
  sku: { name: 'Standard' }
  properties: { publicIPAllocationMethod: 'Static' }
}

resource nat 'Microsoft.Network/natGateways@2024-05-01' = {
  name: 'workstream-nat'
  location: location
  sku: { name: 'Standard' }
  properties: {
    idleTimeoutInMinutes: 10
    publicIpAddresses: [{ id: natAddress.id }]
  }
}

resource desktopRules 'Microsoft.Network/networkSecurityGroups@2024-05-01' = {
  name: 'workstream-desktops'
  location: location
  properties: {
    securityRules: [
      {
        name: 'display-from-the-api'
        properties: {
          priority: 100
          direction: 'Inbound'
          access: 'Allow'
          protocol: 'Tcp'
          sourceAddressPrefix: appsPrefix
          sourcePortRange: '*'
          destinationAddressPrefix: '*'
          destinationPortRange: displayPort
        }
      }
      {
        name: 'nothing-else-from-the-network'
        properties: {
          priority: 4000
          direction: 'Inbound'
          access: 'Deny'
          protocol: '*'
          sourceAddressPrefix: 'VirtualNetwork'
          sourcePortRange: '*'
          destinationAddressPrefix: '*'
          destinationPortRange: '*'
        }
      }
      {
        // A desktop runs whatever its agent is asked to: it must not reach another desktop or the apps' subnet.
        name: 'nothing-to-the-network'
        properties: {
          priority: 100
          direction: 'Outbound'
          access: 'Deny'
          protocol: '*'
          sourceAddressPrefix: '*'
          sourcePortRange: '*'
          destinationAddressPrefix: networkPrefix
          destinationPortRange: '*'
        }
      }
    ]
  }
}

resource network 'Microsoft.Network/virtualNetworks@2024-05-01' = {
  name: 'workstream-net'
  location: location
  properties: {
    addressSpace: { addressPrefixes: [networkPrefix] }
    subnets: [
      {
        name: 'apps'
        properties: {
          addressPrefix: appsPrefix
          delegations: [{ name: 'container-apps', properties: { serviceName: 'Microsoft.App/environments' } }]
        }
      }
      {
        name: 'desktops'
        properties: {
          addressPrefix: desktopsPrefix
          natGateway: { id: nat.id }
          networkSecurityGroup: { id: desktopRules.id }
          defaultOutboundAccess: false
        }
      }
    ]
  }
}

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: 'workstream-env'
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs.properties.customerId
        sharedKey: logs.listKeys().primarySharedKey
      }
    }
    // Inside the network, so the API reaches a desktop at its private address; still answering the public.
    vnetConfiguration: {
      infrastructureSubnetId: '${network.id}/subnets/apps'
      internal: false
    }
    workloadProfiles: [{ name: 'Consumption', workloadProfileType: 'Consumption' }]
  }
}

// --- Email ------------------------------------------------------------------------------------------------
// Sign-in codes and invitations, from Azure Communication Services with a domain Azure manages, so email
// works with no DNS of our own. A domain of our own replaces it when the product has one.
resource email 'Microsoft.Communication/emailServices@2023-04-01' = {
  name: 'workstream-email-${suffix}'
  location: 'global'
  properties: { dataLocation: 'United States' }
}

resource emailDomain 'Microsoft.Communication/emailServices/domains@2023-04-01' = {
  parent: email
  name: 'AzureManagedDomain'
  location: 'global'
  properties: { domainManagement: 'AzureManaged', userEngagementTracking: 'Disabled' }
}

resource communication 'Microsoft.Communication/communicationServices@2023-04-01' = {
  name: 'workstream-comm-${suffix}'
  location: 'global'
  properties: {
    dataLocation: 'United States'
    linkedDomains: [emailDomain.id]
  }
}

// --- The desktops' image ----------------------------------------------------------------------------------
// One image, baked by release.yml from desktop/image/ and kept here as versions named for the release. The
// API makes each desktop from the newest.
resource gallery 'Microsoft.Compute/galleries@2024-03-03' = {
  name: 'workstream'
  location: location
}

resource desktopImage 'Microsoft.Compute/galleries/images@2024-03-03' = {
  parent: gallery
  name: 'desktop'
  location: location
  properties: {
    osType: 'Linux'
    osState: 'Generalized'
    hyperVGeneration: 'V2'
    architecture: 'x64'
    identifier: { publisher: 'workstream', offer: 'desktop', sku: 'debian-13' }
    // The v7 sizes boot from NVMe disks only.
    features: [
      { name: 'SecurityType', value: 'TrustedLaunch' }
      { name: 'DiskControllerTypes', value: 'SCSI, NVMe' }
    ]
  }
}

// main.bicep finds everything here by name, so a stage can stand on this foundation without deploying it.
output registryName string = registry.name
output defaultDomain string = environment.properties.defaultDomain
output desktopImageId string = desktopImage.id
output communicationName string = communication.name
