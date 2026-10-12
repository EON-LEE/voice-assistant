using Azure.AI.OpenAI;
using Azure.Core;
using Azure.Identity;
using Azure.Search.Documents;

namespace VoiceAssistant.Api;

public sealed class AzureServiceClients
{
    public TokenCredential Credential { get; }
    public AzureOpenAIClient OpenAI { get; }
    public SearchClient? Search { get; }

    public AzureServiceClients(ServiceSettings settings)
    {
        Credential = new DefaultAzureCredential();
        OpenAI = new(new Uri(settings.OpenAIEndpoint), Credential);
        if (settings.SearchEnabled) Search = new(new Uri(settings.SearchEndpoint), settings.SearchIndex, Credential);
    }
}
