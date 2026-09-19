targetScope = 'resourceGroup'

@description('Required lowercase SHA256 image digest (64 hexadecimal characters), not a tag. Image repository is fixed.')
@minLength(64)
@maxLength(64)
param imageDigest string

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' existing = {
  name: 'voice-environment'
}

resource runtimeIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = {
  name: 'voice-runtime'
}

resource speech 'Microsoft.CognitiveServices/accounts@2023-05-01' existing = {
  name: 'voice-speech-2cmx22yjxadpg'
}

resource job 'Microsoft.App/jobs@2024-03-01' = {
  name: 'voice-live-acceptance'
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
          server: 'eonvoice20260920.azurecr.io'
          identity: runtimeIdentity.id
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'live-probe'
          image: 'eonvoice20260920.azurecr.io/voice-live-acceptance@sha256:${imageDigest}'
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
            { name: 'Azure__SpeechEndpoint', value: 'https://voice-speech-2cmx22yjxadpg.cognitiveservices.azure.com/' }
            { name: 'Azure__OpenAIEndpoint', value: 'https://voice-ai-2cmx22yjxadpg.openai.azure.com/' }
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
