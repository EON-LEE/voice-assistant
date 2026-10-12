targetScope = 'resourceGroup'

@minLength(3)
@maxLength(12)
@description('Lowercase alphanumeric application prefix.')
param namePrefix string
param location string = resourceGroup().location
param speechLocation string
param openAILocation string
param searchLocation string = location

@description('Existing ACR in this subscription. Its image must already be built and pushed.')
param registryName string
param registryResourceGroup string
@description('Full ACR image reference pinned to sha256 digest; no registry passwords.')
param image string

@secure()
param tenantId string
@secure()
@description('API v2 token audience, NOT the SPA client ID.')
param apiAudience string
@secure()
param spaClientId string
@secure()
@description('Full exposed API scope URI ending in /Meeting.Access.')
param apiScope string

param chatModelName string
param chatModelVersion string
param chatDeploymentName string = 'meeting-chat'
param chatDeploymentSku string
@minValue(1)
param chatCapacity int
@allowed([
  'text-embedding-3-small'
])
@description('Current backend requests native 1536-dimensional vectors; change code/schema together for another model.')
param embeddingModelName string = 'text-embedding-3-small'
param embeddingModelVersion string
param embeddingDeploymentName string = 'meeting-embedding'
param embeddingDeploymentSku string
@minValue(1)
param embeddingCapacity int
param searchIndexName string = 'meeting-knowledge'
@allowed([
  'basic'
  'standard'
  'standard2'
])
param searchSku string = 'basic'
@allowed(['free', 'standard'])
@description('Semantic ranker billing mode. Availability, limits, costs and latency must be verified for the deployed service.')
param searchSemanticSearch string = 'free'

@description('Optional separate ingestion operator object ID. Empty creates no operator role assignments.')
param ingestionPrincipalId string = ''
@allowed([
  'User'
  'ServicePrincipal'
  'Group'
])
param ingestionPrincipalType string = 'User'
@description('Maximum minutes one meeting session may stay open (API Session__MaxMinutes).')
@minValue(5)
@maxValue(180)
param sessionMaxMinutes int = 90
@description('Optional pre-existing Azure Monitor action group ARM ID. No email/webhook content is configured here.')
param alertActionGroupId string = ''

var suffix = uniqueString(resourceGroup().id, namePrefix)
var appName = '${namePrefix}-web'
var speechUserRole = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'f2dc8367-1007-4938-bd23-fe263f013447')
var openAIUserRole = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
// Search Index Data Contributor: the runtime stores each user's own uploaded meeting materials (ACL = caller oid).
var searchDataRole = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '8ebe5a00-799e-43f5-93ac-243d3dce84a7')

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${namePrefix}-runtime'
  location: location
}

module registryAccess 'modules/registry-access.bicep' = {
  name: '${namePrefix}-registry-access'
  scope: resourceGroup(registryResourceGroup)
  params: {
    registryName: registryName
    principalId: identity.properties.principalId
  }
}

resource speech 'Microsoft.CognitiveServices/accounts@2024-10-01' = {
  name: '${namePrefix}-speech-${suffix}'
  location: speechLocation
  kind: 'SpeechServices'
  sku: { name: 'S0' }
  properties: {
    customSubDomainName: '${namePrefix}-speech-${suffix}'
    disableLocalAuth: true
    publicNetworkAccess: 'Enabled'
  }
}

resource openAI 'Microsoft.CognitiveServices/accounts@2024-10-01' = {
  name: '${namePrefix}-ai-${suffix}'
  location: openAILocation
  kind: 'OpenAI'
  sku: { name: 'S0' }
  properties: {
    customSubDomainName: '${namePrefix}-ai-${suffix}'
    disableLocalAuth: true
    publicNetworkAccess: 'Enabled'
  }
}

resource chat 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = {
  parent: openAI
  name: chatDeploymentName
  sku: { name: chatDeploymentSku, capacity: chatCapacity }
  properties: {
    model: { format: 'OpenAI', name: chatModelName, version: chatModelVersion }
    versionUpgradeOption: 'NoAutoUpgrade'
  }
}

@description('Serialize account deployment updates to avoid concurrent Cognitive Services writes.')
resource embedding 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = {
  parent: openAI
  name: embeddingDeploymentName
  sku: { name: embeddingDeploymentSku, capacity: embeddingCapacity }
  properties: {
    model: { format: 'OpenAI', name: embeddingModelName, version: embeddingModelVersion }
    versionUpgradeOption: 'NoAutoUpgrade'
  }
  dependsOn: [chat]
}

resource search 'Microsoft.Search/searchServices@2023-11-01' = {
  name: '${namePrefix}-search-${suffix}'
  location: searchLocation
  sku: { name: searchSku }
  properties: {
    replicaCount: 1
    partitionCount: 1
    hostingMode: 'default'
    semanticSearch: searchSemanticSearch
    disableLocalAuth: true
    publicNetworkAccess: 'enabled'
  }
}

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: 'docs${suffix}'
  location: location
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: {
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    defaultToOAuthAuthentication: true
    publicNetworkAccess: 'Disabled'
    encryption: {
      keySource: 'Microsoft.Storage'
      services: { blob: { enabled: true, keyType: 'Account' } }
    }
  }
}
resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
  properties: {
    isVersioningEnabled: false
    deleteRetentionPolicy: { enabled: false }
    containerDeleteRetentionPolicy: { enabled: false }
  }
}
resource documents 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'documents'
  properties: { publicAccess: 'None' }
}

resource speechAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(speech.id, identity.id, speechUserRole)
  scope: speech
  properties: { principalId: identity.properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: speechUserRole }
}
resource openAIAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(openAI.id, identity.id, openAIUserRole)
  scope: openAI
  properties: { principalId: identity.properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: openAIUserRole }
}
resource searchAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(search.id, identity.id, searchDataRole)
  scope: search
  properties: { principalId: identity.properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: searchDataRole }
}

resource ingestionSearchService 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(ingestionPrincipalId)) {
  name: guid(search.id, ingestionPrincipalId, 'search-service')
  scope: search
  properties: {
    principalId: ingestionPrincipalId
    principalType: ingestionPrincipalType
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7ca78c08-252a-4471-8644-bb5ff32d4ba0')
  }
}
resource ingestionSearchData 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(ingestionPrincipalId)) {
  name: guid(search.id, ingestionPrincipalId, 'search-data')
  scope: search
  properties: {
    principalId: ingestionPrincipalId
    principalType: ingestionPrincipalType
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '8ebe5a00-799e-43f5-93ac-243d3dce84a7')
  }
}
resource ingestionOpenAI 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(ingestionPrincipalId)) {
  name: guid(openAI.id, ingestionPrincipalId, openAIUserRole)
  scope: openAI
  properties: { principalId: ingestionPrincipalId, principalType: ingestionPrincipalType, roleDefinitionId: openAIUserRole }
}
resource ingestionBlob 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(ingestionPrincipalId)) {
  name: guid(documents.id, ingestionPrincipalId, 'blob-data')
  scope: documents
  properties: {
    principalId: ingestionPrincipalId
    principalType: ingestionPrincipalType
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
  }
}

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: '${namePrefix}-environment'
  location: location
  properties: {
    // No console/access/request diagnostics: WebSocket URLs contain one-use tickets.
    appLogsConfiguration: {}
    workloadProfiles: [{ name: 'Consumption', workloadProfileType: 'Consumption' }]
  }
}

var browserOrigin = 'https://${appName}.${environment.properties.defaultDomain}'
resource app 'Microsoft.App/containerApps@2024-03-01' = {
  name: appName
  location: location
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${identity.id}': {} } }
  properties: {
    managedEnvironmentId: environment.id
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
        traffic: [{ latestRevision: true, weight: 100 }]
      }
      registries: [{ server: registryAccess.outputs.loginServer, identity: identity.id }]
    }
    template: {
      scale: { minReplicas: 1, maxReplicas: 1 }
      containers: [{
        name: 'web-api'
        image: image
        resources: { cpu: json('0.5'), memory: '1Gi' }
        env: [
          { name: 'ASPNETCORE_HTTP_PORTS', value: '8080' }
          { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
          { name: 'Provider__Mode', value: 'Azure' }
          { name: 'AZURE_CLIENT_ID', value: identity.properties.clientId }
          { name: 'Authentication__TenantId', value: tenantId }
          { name: 'Authentication__Audience', value: apiAudience }
          { name: 'Authentication__ClientId', value: spaClientId }
          { name: 'Authentication__Scope', value: apiScope }
          { name: 'Security__AllowedOrigins__0', value: browserOrigin }
          { name: 'Azure__SpeechRegion', value: speechLocation }
          { name: 'Azure__SpeechResourceId', value: speech.id }
          { name: 'Azure__SpeechEndpoint', value: speech.properties.endpoint }
          { name: 'Azure__OpenAIEndpoint', value: openAI.properties.endpoint }
          { name: 'Azure__ChatDeployment', value: chatDeploymentName }
          { name: 'Azure__EmbeddingDeployment', value: embeddingDeploymentName }
          { name: 'Azure__SearchEndpoint', value: 'https://${search.name}.search.windows.net' }
          { name: 'Azure__SearchIndex', value: searchIndexName }
          { name: 'Azure__SearchSemanticConfiguration', value: 'meeting-semantic' }
          { name: 'Azure__SearchMinimumRerankerScore', value: '2.0' }
          { name: 'Session__MaxMinutes', value: string(sessionMaxMinutes) }
          { name: 'Logging__LogLevel__Microsoft.AspNetCore', value: 'Warning' }
          { name: 'Logging__LogLevel__Azure', value: 'Warning' }
        ]
        probes: [
          { type: 'Startup', httpGet: { path: '/health/live', port: 8080 }, periodSeconds: 5, failureThreshold: 30 }
          { type: 'Liveness', httpGet: { path: '/health/live', port: 8080 }, periodSeconds: 30, failureThreshold: 3 }
          { type: 'Readiness', httpGet: { path: '/health/ready', port: 8080 }, periodSeconds: 10, failureThreshold: 3 }
        ]
      }]
    }
  }
  dependsOn: [speechAccess, openAIAccess, searchAccess, embedding]
}

resource replicaAlert 'Microsoft.Insights/metricAlerts@2018-03-01' = {
  name: '${namePrefix}-no-replicas'
  location: 'global'
  properties: {
    description: 'No active replica; metric-only alert, no audio, prompts, URLs or tickets.'
    severity: 1
    enabled: true
    scopes: [app.id]
    evaluationFrequency: 'PT1M'
    windowSize: 'PT5M'
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [{
        name: 'NoReplica'
        metricNamespace: 'Microsoft.App/containerApps'
        metricName: 'Replicas'
        operator: 'LessThan'
        threshold: 1
        timeAggregation: 'Average'
        criterionType: 'StaticThresholdCriterion'
      }]
    }
    actions: empty(alertActionGroupId) ? [] : [{ actionGroupId: alertActionGroupId }]
  }
}

output browserUrl string = browserOrigin
output runtimePrincipalId string = identity.properties.principalId
output speechResourceId string = speech.id
output openAIEndpoint string = openAI.properties.endpoint
output searchEndpoint string = 'https://${search.name}.search.windows.net'
output storageAccountName string = storage.name
output documentsContainer string = documents.name
