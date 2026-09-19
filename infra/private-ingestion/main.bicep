targetScope = 'resourceGroup'

param namePrefix string = 'voice-ingest'
param location string = resourceGroup().location
param storageAccountName string
param searchName string
param openAIName string
param registryName string
@description('Existing private-ingestion image in this registry, pinned to its verified sha256 digest.')
param image string
param allowedPrincipalId string
@description('Explicit RFC3339 version for the approved synthetic document. Reuse unchanged on recovery.')
param documentUpdatedAt string
param indexName string = 'meeting-knowledge'
param embeddingDeployment string = 'meeting-embedding'
param createIndex bool = false
@description('Operator acknowledgement that all application readers are stopped/drained. Does not itself stop the app.')
param confirmExclusiveMaintenance bool = false
@description('Opt-in 30-day redacted console/system diagnostics for this synthetic-only ingestion environment; never web diagnostics.')
param enableDiagnostics bool = false
param vnetAddressPrefix string = '10.246.0.0/23'
param infrastructureSubnetPrefix string = '10.246.0.0/24'
param endpointSubnetPrefix string = '10.246.1.0/27'
param privateEndpointIp string = '10.246.1.4'

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: storageAccountName
}
resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' existing = {
  parent: storage
  name: 'default'
}
resource documents 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' existing = {
  parent: blobService
  name: 'documents'
}
resource search 'Microsoft.Search/searchServices@2023-11-01' existing = {
  name: searchName
}
resource openAI 'Microsoft.CognitiveServices/accounts@2024-10-01' existing = {
  name: openAIName
}
resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = {
  name: registryName
}

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${namePrefix}-identity'
  location: location
}
resource blobAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(documents.id, identity.id, 'blob-data')
  scope: documents
  properties: {
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
  }
}
resource searchServiceAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(search.id, identity.id, 'search-service')
  scope: search
  properties: {
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7ca78c08-252a-4471-8644-bb5ff32d4ba0')
  }
}
resource searchDataAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(search.id, identity.id, 'search-data')
  scope: search
  properties: {
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '8ebe5a00-799e-43f5-93ac-243d3dce84a7')
  }
}
resource openAIAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(openAI.id, identity.id, 'openai-user')
  scope: openAI
  properties: {
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
  }
}
resource imageAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, identity.id, 'acr-pull')
  scope: registry
  properties: {
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
  }
}

resource network 'Microsoft.Network/virtualNetworks@2023-11-01' = {
  name: '${namePrefix}-network'
  location: location
  properties: {
    addressSpace: { addressPrefixes: [vnetAddressPrefix] }
    subnets: [
      {
        name: 'environment'
        properties: {
          addressPrefix: infrastructureSubnetPrefix
          delegations: [{ name: 'container-apps', properties: { serviceName: 'Microsoft.App/environments' } }]
        }
      }
      {
        name: 'endpoints'
        properties: {
          addressPrefix: endpointSubnetPrefix
          privateEndpointNetworkPolicies: 'Disabled'
        }
      }
    ]
  }
}
resource dns 'Microsoft.Network/privateDnsZones@2020-06-01' = {
  name: 'privatelink.blob.${az.environment().suffixes.storage}'
  location: 'global'
}
resource dnsLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = {
  parent: dns
  name: '${namePrefix}-link'
  location: 'global'
  properties: {
    registrationEnabled: false
    virtualNetwork: { id: network.id }
  }
}
resource endpoint 'Microsoft.Network/privateEndpoints@2023-11-01' = {
  name: '${namePrefix}-blob'
  location: location
  properties: {
    subnet: { id: resourceId('Microsoft.Network/virtualNetworks/subnets', network.name, 'endpoints') }
    privateLinkServiceConnections: [{
      name: 'blob'
      properties: {
        privateLinkServiceId: storage.id
        groupIds: ['blob']
      }
    }]
    ipConfigurations: [{
      name: 'blob'
      properties: {
        groupId: 'blob'
        memberName: 'blob'
        privateIPAddress: privateEndpointIp
      }
    }]
  }
}
resource dnsGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2023-11-01' = {
  parent: endpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [{ name: 'blob', properties: { privateDnsZoneId: dns.id } }]
  }
}
resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: '${namePrefix}-environment'
  location: location
  properties: {
    appLogsConfiguration: enableDiagnostics ? { destination: 'azure-monitor' } : {}
    vnetConfiguration: {
      infrastructureSubnetId: resourceId('Microsoft.Network/virtualNetworks/subnets', network.name, 'environment')
      internal: true
    }
    workloadProfiles: [{ name: 'Consumption', workloadProfileType: 'Consumption' }]
  }
}
resource diagnosticsWorkspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = if (enableDiagnostics) {
  name: '${namePrefix}-diagnostics'
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
    features: { disableLocalAuth: true }
  }
}
resource diagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = if (enableDiagnostics) {
  name: 'synthetic-ingestion-only'
  scope: environment
  properties: {
    workspaceId: diagnosticsWorkspace!.id
    logs: [
      { category: 'ContainerAppConsoleLogs', enabled: true }
      { category: 'ContainerAppSystemLogs', enabled: true }
    ]
  }
}
resource job 'Microsoft.App/jobs@2024-03-01' = {
  name: '${namePrefix}-job'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${identity.id}': {} }
  }
  properties: {
    environmentId: environment.id
    workloadProfileName: 'Consumption'
    configuration: {
      triggerType: 'Manual'
      replicaTimeout: 1800
      replicaRetryLimit: 0
      manualTriggerConfig: { parallelism: 1, replicaCompletionCount: 1 }
      registries: [{ server: registry.properties.loginServer, identity: identity.id }]
    }
    template: {
      containers: [{
        name: 'approved-synthetic-ingestion'
        image: image
        resources: { cpu: json('0.5'), memory: '1Gi' }
        env: [
          { name: 'AZURE_CLIENT_ID', value: identity.properties.clientId }
          { name: 'INGEST_SEARCH_NAME', value: searchName }
          { name: 'INGEST_OPENAI_NAME', value: openAIName }
          { name: 'INGEST_STORAGE_NAME', value: storageAccountName }
          { name: 'INGEST_INDEX_NAME', value: indexName }
          { name: 'INGEST_EMBEDDING_DEPLOYMENT', value: embeddingDeployment }
          { name: 'INGEST_ALLOWED_PRINCIPAL_ID', value: allowedPrincipalId }
          { name: 'INGEST_DOCUMENT_UPDATED_AT', value: documentUpdatedAt }
          { name: 'INGEST_BLOB_PRIVATE_IP', value: privateEndpointIp }
          { name: 'INGEST_CREATE_INDEX', value: createIndex ? 'true' : 'false' }
          { name: 'INGEST_CONFIRM_EXCLUSIVE_MAINTENANCE', value: confirmExclusiveMaintenance ? 'true' : 'false' }
        ]
      }]
    }
  }
  dependsOn: [blobAccess, searchServiceAccess, searchDataAccess, openAIAccess, imageAccess, dnsGroup, dnsLink]
}

output jobResourceId string = job.id
output ingestionPrincipalId string = identity.properties.principalId
output expectedPrivateBlobIp string = privateEndpointIp
output privateEndpointResourceId string = endpoint.id
output diagnosticsWorkspaceId string = enableDiagnostics ? diagnosticsWorkspace!.id : ''
