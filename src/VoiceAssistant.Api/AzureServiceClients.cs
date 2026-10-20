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
    /// <summary>Clients for live reply retrieval only: the SDKs do not retry, so the provider's single fast retry is the only one.</summary>
    public AzureOpenAIClient RetrievalOpenAI { get; }
    public SearchClient? RetrievalSearch { get; }

    public AzureServiceClients(ServiceSettings settings)
    {
        Credential = new DefaultAzureCredential();
        OpenAI = new(new Uri(settings.OpenAIEndpoint), Credential);
        RetrievalOpenAI = new(new Uri(settings.OpenAIEndpoint), Credential, AzureMeetingProvider.RetrievalOpenAIOptions());
        if (settings.SearchEnabled)
        {
            Search = new(new Uri(settings.SearchEndpoint), settings.SearchIndex, Credential);
            RetrievalSearch = new(new Uri(settings.SearchEndpoint), settings.SearchIndex, Credential, AzureMeetingProvider.RetrievalSearchOptions());
        }
    }
}
