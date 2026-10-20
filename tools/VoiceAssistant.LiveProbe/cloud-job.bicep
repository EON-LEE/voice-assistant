targetScope = 'resourceGroup'

@description('Required lowercase SHA256 image digest (64 hexadecimal characters), not a tag. Image repository is fixed.')
@minLength(64)
@maxLength(64)
param imageDigest string

@allowed([
  'voice-environment'
  'voice-ingest-environment'
])
@description('Use the private diagnostic environment only for synthetic, content-free acceptance evidence.')
param environmentName string = 'voice-environment'

@description('Existing Azure Container Registry name (without .azurecr.io) that holds the voice-live-acceptance image.')
@minLength(5)
@maxLength(50)
param registryName string

@description('Existing Speech account (custom subdomain) used by the web app in this resource group.')
param speechName string

@description('Existing Azure OpenAI account used by the web app in this resource group.')
param openAIName string

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' existing = {
  name: environmentName
}

resource runtimeIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = {
  name: 'voice-runtime'
}

resource speech 'Microsoft.CognitiveServices/accounts@2023-05-01' existing = {
  name: speechName
}

resource openAI 'Microsoft.CognitiveServices/accounts@2023-05-01' existing = {
  name: openAIName
}

var registryServer = '${registryName}${az.environment().suffixes.acrLoginServer}'

resource job 'Microsoft.App/jobs@2024-03-01' = {
  name: environmentName == 'voice-environment' ? 'voice-live-acceptance' : 'voice-live-acceptance-private'
  location: resourceGroup().location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${runtimeIdentity.id}': {}
    }
  }
  properties: {
    environmentId: environment.id
    workloadProfileName: 'Consumption'
    configuration: {
      triggerType: 'Manual'
      replicaTimeout: 180
      replicaRetryLimit: 0
      manualTriggerConfig: {
        parallelism: 1
        replicaCompletionCount: 1
      }
      registries: [
        {
          server: registryServer
          identity: runtimeIdentity.id
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'live-probe'
          image: '${registryServer}/voice-live-acceptance@sha256:${imageDigest}'
          command: [
            'dotnet'
          ]
          args: [
            'VoiceAssistant.LiveProbe.dll'
            '--live'
            '--audio'
            '/app/fixtures/original-project.wav'
            '--metadata'
            '/app/fixtures/original-project.json'
            '--timeout-seconds'
            '120'
          ]
          resources: {
            cpu: 1
            memory: '2Gi'
          }
          env: [
            { name: 'DOTNET_ENVIRONMENT', value: 'Production' }
            { name: 'Provider__Mode', value: 'Azure' }
            { name: 'AZURE_CLIENT_ID', value: runtimeIdentity.properties.clientId }
            { name: 'Azure__SpeechRegion', value: speech.location }
            { name: 'Azure__SpeechResourceId', value: speech.id }
            { name: 'Azure__SpeechEndpoint', value: speech.properties.endpoint }
            { name: 'Azure__OpenAIEndpoint', value: openAI.properties.endpoint }
            { name: 'Azure__ChatDeployment', value: 'meeting-chat' }
            { name: 'Azure__ChatMaxOutputTokens', value: '2048' }
          ]
        }
      ]
    }
  }
}

output jobName string = job.name
output imageReference string = job.properties.template.containers[0].image
