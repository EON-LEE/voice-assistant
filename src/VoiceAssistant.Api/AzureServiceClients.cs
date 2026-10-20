using Azure.AI.OpenAI;
using Azure.Core;
using Azure.Identity;
using Azure.Search.Documents;

namespace VoiceAssistant.Api;

public sealed class AzureServiceClients
{
    public TokenCredential Credential { get; }
    /// <summary>Practice, materials and other non-live calls, with the SDK's default retries.</summary>
    public AzureOpenAIClient OpenAI { get; }
    /// <summary>Live reply chat calls (answer, classifier, translation): at most one short retry, see <see cref="LiveChatRetryPolicy"/>.</summary>
    public AzureOpenAIClient LiveChat { get; }
    public SearchClient? Search { get; }
    /// <summary>Clients for live reply retrieval only: the SDKs do not retry, so the provider's single fast retry is the only one.</summary>
    public AzureOpenAIClient RetrievalOpenAI { get; }
    public SearchClient? RetrievalSearch { get; }

    public AzureServiceClients(ServiceSettings settings)
    {
        Credential = new DefaultAzureCredential();
        OpenAI = new(new Uri(settings.OpenAIEndpoint), Credential);
        LiveChat = new(new Uri(settings.OpenAIEndpoint), Credential, AzureMeetingProvider.LiveChatOptions(apiVersion: settings.ChatApiVersion));
        RetrievalOpenAI = new(new Uri(settings.OpenAIEndpoint), Credential, AzureMeetingProvider.RetrievalOpenAIOptions());
        if (settings.SearchEnabled)
        {
            Search = new(new Uri(settings.SearchEndpoint), settings.SearchIndex, Credential);
            RetrievalSearch = new(new Uri(settings.SearchEndpoint), settings.SearchIndex, Credential, AzureMeetingProvider.RetrievalSearchOptions());
        }
    }
}
