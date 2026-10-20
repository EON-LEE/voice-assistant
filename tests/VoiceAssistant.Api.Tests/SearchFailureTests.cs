using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.AI.OpenAI;
using Azure.Core.Pipeline;
using Azure.Identity;
using Azure.Search.Documents;
using Microsoft.Extensions.Logging;
using VoiceAssistant.Api;
using Xunit;

namespace VoiceAssistant.Api.Tests;

public sealed class SearchFailureTests
{
    private const string Oid = "e59048e0-9a10-433e-8e0e-beb279c0c234";

    [Fact]
    public async Task ThrottledSearchIsRetriedOnceAndThenGrounds()
    {
        using var transport = new FaultTransport().Search(HttpStatusCode.TooManyRequests).Search(HttpStatusCode.OK, FaultTransport.Scored(3.2));
        using var http = new HttpClient(transport);
        var grounding = await FaultTransport.Provider(http).RetrieveAsync("What is the delivery plan?", Oid, CancellationToken.None);
        Assert.Equal("grounded", grounding.Status);
        Assert.Single(grounding.Documents);
        Assert.Equal(2, transport.SearchCalls);
        // The embedding that succeeded is reused, so the retry repeats only the search.
        Assert.Equal(1, transport.EmbeddingCalls);
    }

    [Fact]
    public async Task ThrottledEmbeddingIsRetriedOnce()
    {
        using var transport = new FaultTransport().Embedding(HttpStatusCode.TooManyRequests).Embedding(HttpStatusCode.OK);
        using var http = new HttpClient(transport);
        var grounding = await FaultTransport.Provider(http).RetrieveAsync("What is the delivery plan?", Oid, CancellationToken.None);
        Assert.Equal("grounded", grounding.Status);
        Assert.Equal(2, transport.EmbeddingCalls);
        Assert.Equal(1, transport.SearchCalls);
    }

    [Fact]
    public async Task RepeatedEmbeddingFailuresStopAfterTheProviderRetry()
    {
        // Every embedding call fails. Exactly two calls prove the SDK's own retries are off and only the provider retries once.
        using var transport = new FaultTransport().Embedding(HttpStatusCode.ServiceUnavailable);
        using var http = new HttpClient(transport);
        var failure = await Assert.ThrowsAsync<ProviderException>(() =>
            FaultTransport.Provider(http).RetrieveAsync("What is the delivery plan?", Oid, CancellationToken.None));
        Assert.Equal("grounding_unavailable", failure.Code);
        Assert.NotNull(failure.Retrieval);
        Assert.True(failure.Retrieval!.Retried);
        Assert.True(failure.Retrieval.Transient);
        Assert.Equal(503, failure.Retrieval.HttpStatus);
        Assert.Equal(2, transport.EmbeddingCalls);
        Assert.Equal(0, transport.SearchCalls);
    }

    [Fact]
    public async Task TwoServerErrorsFailAfterOneRetryWithAContentFreeCause()
    {
        using var transport = new FaultTransport().Search(HttpStatusCode.ServiceUnavailable);
        using var http = new HttpClient(transport);
        var failure = await Assert.ThrowsAsync<ProviderException>(() =>
            FaultTransport.Provider(http).RetrieveAsync("What is the delivery plan?", Oid, CancellationToken.None));
        Assert.Equal("grounding_unavailable", failure.Code);
        Assert.Equal(new RetrievalFailure("server_error", true, nameof(RequestFailedException), 503, "ServiceUnavailable", true), failure.Retrieval);
        Assert.Equal(2, transport.SearchCalls);
        Assert.DoesNotContain(FaultTransport.PrivateBody, failure.Message);
        Assert.DoesNotContain(FaultTransport.PrivateBody, failure.Retrieval!.ToString());
    }

    [Fact]
    public async Task QuotaFailureIsNotRetried()
    {
        using var transport = new FaultTransport().Search(HttpStatusCode.PaymentRequired);
        using var http = new HttpClient(transport);
        var failure = await Assert.ThrowsAsync<ProviderException>(() =>
            FaultTransport.Provider(http).RetrieveAsync("What is the delivery plan?", Oid, CancellationToken.None));
        Assert.Equal(new RetrievalFailure("quota", false, nameof(RequestFailedException), 402, "QuotaExceeded", false), failure.Retrieval);
        Assert.Equal(1, transport.SearchCalls);
    }

    [Fact]
    public async Task IncompleteSemanticRankingIsRetriedOnceThenFails()
    {
        using var transport = new FaultTransport().Search(HttpStatusCode.OK,
            """{"@search.semanticPartialResponseReason":"capacityOverloaded","@search.semanticPartialResponseType":"baseResults","value":[]}""");
        using var http = new HttpClient(transport);
        var failure = await Assert.ThrowsAsync<ProviderException>(() =>
            FaultTransport.Provider(http).RetrieveAsync("What is the delivery plan?", Oid, CancellationToken.None));
        Assert.Equal(new RetrievalFailure("partial_results", true, RetrievalFailure.ResultValidation, null, null, true), failure.Retrieval);
        Assert.Equal(2, transport.SearchCalls);
    }

    [Theory]
    [InlineData(0, "network", true)]
    [InlineData(408, "timeout", true)]
    [InlineData(429, "throttled", true)]
    [InlineData(500, "server_error", true)]
    [InlineData(502, "server_error", true)]
    [InlineData(503, "server_error", true)]
    [InlineData(504, "server_error", true)]
    [InlineData(400, "bad_request", false)]
    [InlineData(401, "auth", false)]
    [InlineData(402, "quota", false)]
    [InlineData(403, "auth", false)]
    [InlineData(404, "not_found", false)]
    public void HttpFailuresAreClassifiedAsTransientOrPermanent(int status, string kind, bool transient)
    {
        var failure = RetrievalFailure.Classify(new RequestFailedException(status, "private-cloud-body"));
        Assert.Equal(kind, failure?.Kind);
        Assert.Equal(transient, failure?.Transient);
        Assert.Equal(status == 0 ? (int?)null : status, failure?.HttpStatus);
    }

    [Fact]
    public void OtherFailuresAreClassifiedWithoutContent()
    {
        Assert.Equal(new RetrievalFailure("timeout", true, nameof(OperationCanceledException), null, null, false),
            RetrievalFailure.Classify(new OperationCanceledException("private")));
        Assert.Equal(new RetrievalFailure("network", true, nameof(ClientResultException), null, null, false),
            RetrievalFailure.Classify(new ClientResultException("private")));
        Assert.Equal(new RetrievalFailure("auth", false, nameof(CredentialUnavailableException), null, null, false),
            RetrievalFailure.Classify(new CredentialUnavailableException("private")));
        Assert.Null(RetrievalFailure.Classify(new InvalidOperationException("private")));
        Assert.Equal("unexpected", RetrievalFailure.From(new InvalidOperationException("private")).Kind);
    }

    [Fact]
    public void LogLineCarriesOnlyTheContentFreeCause()
    {
        var logger = new ListLogger();
        MeetingSession.LogRetrievalFailure(logger, new ProviderException("grounding_unavailable", "private query text")
        {
            Retrieval = new("server_error", true, nameof(RequestFailedException), 503, "ServiceUnavailable", true)
        });
        MeetingSession.LogRetrievalFailure(logger, new ProviderException("grounding_unavailable", "private query text"));
        Assert.Equal(2, logger.Lines.Count);
        Assert.Equal("Meeting retrieval unavailable: server_error, transient True, RequestFailedException, HTTP status 503, " +
            "search code ServiceUnavailable, retried True.", logger.Lines[0]);
        Assert.Equal("Meeting retrieval unavailable: unknown, transient False, ProviderException, HTTP status none, " +
            "search code none, retried False.", logger.Lines[1]);
        Assert.All(logger.Lines, line => Assert.DoesNotContain("private", line));
    }

    [Fact]
    public void ResponseFailureLogNeverReportsAMadeUpStatusZero()
    {
        Assert.Equal("503", MeetingSession.HttpStatusText(new RequestFailedException(503, "private")));
        Assert.Equal("none", MeetingSession.HttpStatusText(new RequestFailedException("private")));
        Assert.Equal("none", MeetingSession.HttpStatusText(new ClientResultException("private")));
        Assert.Equal("none", MeetingSession.HttpStatusText(new ProviderException("grounding_unavailable", "private")));
    }

    [Fact]
    public async Task PrefetchedFailureKeepsItsContentFreeCause()
    {
        const string question = "What is our customer delivery plan for Friday?";
        var cause = new RetrievalFailure("throttled", true, nameof(RequestFailedException), 429, null, true);
        var provider = new FailingRetrieval(cause);
        await using var prefetch = new PartialRetrieval(provider, Oid, SessionOptions.Legacy, CancellationToken.None);
        prefetch.Update(new("turn", 1, question, true));
        await provider.Called.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var taken = prefetch.Take("turn", question, "knowledge");
        Assert.NotNull(taken);
        var outcome = await taken;
        var failure = Assert.Throws<ProviderException>(() => outcome.RequireGrounding());
        Assert.Equal("grounding_unavailable", failure.Code);
        Assert.Equal(cause, failure.Retrieval);
    }

    private sealed class FailingRetrieval(RetrievalFailure cause) : IMeetingProvider
    {
        public TaskCompletionSource Called { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ISpeechStream> StartSpeechAsync(Action<Transcript> transcript, Action<ProviderException> error, CancellationToken cancellation) =>
            throw new NotSupportedException();
        public Task<Grounding> RetrieveAsync(string query, string objectId, CancellationToken cancellation)
        {
            Called.TrySetResult();
            return Task.FromException<Grounding>(new ProviderException("grounding_unavailable", "private") { Retrieval = cause });
        }
        public IAsyncEnumerable<string> AnswerAsync(IReadOnlyList<ConversationTurn> conversation, Grounding grounding, CancellationToken cancellation) =>
            throw new NotSupportedException();
    }

    private sealed class ListLogger : ILogger
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }
}

public sealed partial class ApiTests
{
    [Fact]
    public async Task TwoSearchServerErrorsStillGetAGeneralModelReplyAfterTheNonFatalError()
    {
        using var transport = new FaultTransport().Search(HttpStatusCode.ServiceUnavailable);
        var messages = await AnswerThroughAzureProviderAsync(transport);
        AssertGeneralReplyAfterSearchError(messages, transport);
        Assert.Equal(2, transport.SearchCalls);
    }

    [Fact]
    public async Task SearchQuotaFailureIsNotRetriedAndStillGetsAGeneralModelReply()
    {
        using var transport = new FaultTransport().Search(HttpStatusCode.PaymentRequired);
        var messages = await AnswerThroughAzureProviderAsync(transport);
        AssertGeneralReplyAfterSearchError(messages, transport);
        Assert.Equal(1, transport.SearchCalls);
    }

    [Fact]
    public async Task SearchAndModelFailuresThroughTheAzureProviderEndWithTheFixedCompletion()
    {
        using var transport = new FaultTransport { ChatStatus = HttpStatusCode.BadRequest }.Search(HttpStatusCode.ServiceUnavailable);
        var messages = await AnswerThroughAzureProviderAsync(transport);
        Assert.Equal("grounding_unavailable", Assert.Single(messages, message => TypeOf(message) == "error").GetProperty("code").GetString());
        var completed = messages[^1];
        Assert.Equal("unavailable", completed.GetProperty("grounding").GetString());
        Assert.Equal("Grounding is unavailable. Please try again before relying on a factual answer.", completed.GetProperty("text").GetString());
        Assert.Empty(completed.GetProperty("sources").EnumerateArray());
    }

    private static async Task<List<JsonElement>> AnswerThroughAzureProviderAsync(FaultTransport transport)
    {
        using var http = new HttpClient(transport);
        var provider = new AzureBackedProvider(FaultTransport.Provider(http));
        await using var host = await Host.StartAsync(provider: provider);
        using var socket = await host.ConnectAsync();
        await Send(socket, WithOptions(new { responseMode = "grounded" }));
        Assert.Equal("session.ready", TypeOf(await Receive(socket)));
        provider.Emit(new("question", 1, "What is our delivery plan for the rollout?", true));
        var messages = await ReceiveUntilAsync(socket, Completed);
        await Send(socket, """{"type":"session.stop"}""");
        return messages;
    }

    private static void AssertGeneralReplyAfterSearchError(List<JsonElement> messages, FaultTransport transport)
    {
        var error = messages.FindIndex(message => TypeOf(message) == "error");
        var firstDelta = messages.FindIndex(message => TypeOf(message) == "response.delta");
        Assert.InRange(error, 0, firstDelta - 1);
        Assert.Equal("grounding_unavailable", messages[error].GetProperty("code").GetString());
        Assert.True(messages[error].GetProperty("retryable").GetBoolean());
        Assert.Single(messages, message => TypeOf(message) == "error");
        var completed = messages[^1];
        Assert.Equal("unavailable", completed.GetProperty("grounding").GetString());
        Assert.Empty(completed.GetProperty("sources").EnumerateArray());
        Assert.Equal("A general reply for now.", completed.GetProperty("text").GetString());
        Assert.Equal(2, completed.GetProperty("suggestions").GetArrayLength());
        // The streamed answer request carried the general, non-committal variant and no documents.
        var answer = Assert.Single(transport.ChatBodies, body => body.Contains("\"stream\":true", StringComparison.Ordinal));
        Assert.Contains("Document search failed for this reply", answer);
        Assert.DoesNotContain("Fictional delivery plan", answer);
        Assert.Equal(1, transport.EmbeddingCalls);
    }
}

/// <summary>Fake Azure endpoints: queued Search and embedding answers (the last one repeats) and a fixed streamed chat answer.</summary>
internal sealed class FaultTransport : HttpMessageHandler
{
    internal const string PrivateBody = "private-cloud-body";
    private readonly object gate = new();
    private readonly Queue<(HttpStatusCode Status, string Body)> search = new();
    private readonly Queue<HttpStatusCode> embeddings = new();
    private readonly Queue<(HttpStatusCode Status, string? RetryAfter, string? RetryAfterMs)> chats = new();
    private readonly Queue<(HttpStatusCode Status, string? RetryAfter, string? RetryAfterMs)> answers = new();
    private int searchCalls;
    private int embeddingCalls;
    private int chatCalls;
    /// <summary>The status of every chat call once the queued <see cref="Chat"/> answers are used up.</summary>
    public HttpStatusCode ChatStatus { get; init; } = HttpStatusCode.OK;
    /// <summary>A <c>Retry-After</c> header (seconds) sent with every failing <see cref="ChatStatus"/>.</summary>
    public string? ChatRetryAfter { get; init; }
    /// <summary>Runs as each Search request arrives, for example to let a test clock show a slow retrieval.</summary>
    public Action? OnSearch { get; init; }
    /// <summary>Streamed answer chunks; the newline starts the alternative line.</summary>
    public string[] ChatChunks { get; init; } = ["A general reply for now.", "\nThe exact details still need to be confirmed."];
    /// <summary>Raw chunk JSON streamed after <see cref="ChatChunks"/>, before <c>[DONE]</c>: a finish reason or a filter verdict.</summary>
    public string[] ChatTrailer { get; init; } = [];
    public ConcurrentQueue<string> ChatBodies { get; } = new();
    public int SearchCalls => Volatile.Read(ref searchCalls);
    public int EmbeddingCalls => Volatile.Read(ref embeddingCalls);
    public int ChatCalls => Volatile.Read(ref chatCalls);

    public FaultTransport Search(HttpStatusCode status, string? body = null)
    {
        lock (gate) search.Enqueue((status, body ?? Error(status)));
        return this;
    }

    public FaultTransport Embedding(HttpStatusCode status)
    {
        lock (gate) embeddings.Enqueue(status);
        return this;
    }

    /// <summary>Queues one chat answer; a failing one may carry <c>Retry-After</c> (seconds) or <c>retry-after-ms</c>.</summary>
    public FaultTransport Chat(HttpStatusCode status, string? retryAfter = null, string? retryAfterMs = null)
    {
        lock (gate) chats.Enqueue((status, retryAfter, retryAfterMs));
        return this;
    }

    /// <summary>Like <see cref="Chat"/>, but only for the streamed answer request, so the classifier keeps succeeding.</summary>
    public FaultTransport Answer(HttpStatusCode status, string? retryAfter = null, string? retryAfterMs = null)
    {
        lock (gate) answers.Enqueue((status, retryAfter, retryAfterMs));
        return this;
    }

    /// <summary>The production setup: live chat with its own short retry; Search and embeddings without SDK retries.</summary>
    internal static AzureMeetingProvider Provider(HttpClient http, ServiceSettings? configured = null)
    {
        var settings = configured ?? new ServiceSettings { ChatDeployment = "chat", EmbeddingDeployment = "embedding" };
        var chatOptions = AzureMeetingProvider.LiveChatOptions();
        chatOptions.Transport = new HttpClientPipelineTransport(http);
        var chat = new AzureOpenAIClient(new Uri("https://example.openai.azure.com"), new ApiKeyCredential("test-only"), chatOptions);
        var embeddingOptions = AzureMeetingProvider.RetrievalOpenAIOptions();
        embeddingOptions.Transport = new HttpClientPipelineTransport(http);
        var embeddings = new AzureOpenAIClient(new Uri("https://example.openai.azure.com"), new ApiKeyCredential("test-only"), embeddingOptions);
        var searchOptions = AzureMeetingProvider.RetrievalSearchOptions();
        searchOptions.Transport = new HttpClientTransport(http);
        var searchClient = new SearchClient(new Uri("https://example.search.windows.net"), "meeting", new AzureKeyCredential("test-only"), searchOptions);
        return new(settings, chat, searchClient, embeddings);
    }

    internal static string Scored(double score) => JsonSerializer.Serialize(new
    {
        value = new[]
        {
            new Dictionary<string, object?>
            {
                ["@search.score"] = 1, ["@search.rerankerScore"] = score, ["title"] = "Fictional delivery plan",
                ["content"] = "The fictional rollout review is on Friday.", ["url"] = "https://example.test/plan",
                ["updatedAt"] = "2026-09-20T00:00:00Z"
            }
        }
    });

    private static string Error(HttpStatusCode status) => JsonSerializer.Serialize(new
    {
        error = new
        {
            code = status switch
            {
                HttpStatusCode.PaymentRequired => "QuotaExceeded",
                HttpStatusCode.TooManyRequests => "TooManyRequests",
                _ => "ServiceUnavailable"
            },
            message = PrivateBody
        }
    });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        if (request.RequestUri!.Host.Contains("search.windows.net", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref searchCalls);
            OnSearch?.Invoke();
            var (status, json) = Next(search, (HttpStatusCode.OK, Scored(3.2)));
            return Json(json, status);
        }
        if (request.RequestUri.AbsolutePath.EndsWith("/embeddings", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref embeddingCalls);
            var status = Next(embeddings, HttpStatusCode.OK);
            return status != HttpStatusCode.OK ? Json(Error(status), status) : Json(JsonSerializer.Serialize(new
            {
                @object = "list", model = "embedding",
                data = new[] { new { @object = "embedding", index = 0, embedding = Convert.ToBase64String(new byte[1536 * sizeof(float)]) } },
                usage = new { prompt_tokens = 1, total_tokens = 1 }
            }));
        }
        ChatBodies.Enqueue(body);
        Interlocked.Increment(ref chatCalls);
        var streamed = body.Contains("\"stream\":true", StringComparison.Ordinal);
        bool queuedAnswer;
        lock (gate) queuedAnswer = streamed && answers.Count > 0;
        var (chatStatus, retryAfter, retryAfterMs) = queuedAnswer ? Next(answers, (HttpStatusCode.OK, null, null))
            : Next(chats, (ChatStatus, ChatRetryAfter, null));
        if (chatStatus != HttpStatusCode.OK)
        {
            var failure = Json(Error(chatStatus), chatStatus);
            if (retryAfter is not null) failure.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
            if (retryAfterMs is not null) failure.Headers.TryAddWithoutValidation("retry-after-ms", retryAfterMs);
            return failure;
        }
        if (!streamed)
            return Json("""{"id":"test","object":"chat.completion","created":1,"model":"test","choices":[{"index":0,"message":{"role":"assistant","content":"LISTEN"},"finish_reason":"stop"}]}""");
        var sse = string.Concat(ChatChunks.Select(text => "data: " + JsonSerializer.Serialize(new
        {
            id = "test", @object = "chat.completion.chunk", created = 1, model = "test",
            choices = new[] { new { index = 0, delta = new { content = text }, finish_reason = (string?)null } }
        }) + "\n\n")) + string.Concat(ChatTrailer.Select(chunk => "data: " + chunk + "\n\n")) + "data: [DONE]\n\n";
        return new(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") };
    }

    private T Next<T>(Queue<T> queue, T fallback)
    {
        lock (gate) return queue.Count > 1 ? queue.Dequeue() : queue.Count == 1 ? queue.Peek() : fallback;
    }

    private static HttpResponseMessage Json(string value, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
}

/// <summary>Synthetic speech in front of the real Azure provider, so the socket session exercises its retrieval and chat code.</summary>
internal sealed class AzureBackedProvider(AzureMeetingProvider azure) : IMeetingProvider
{
    public Action<Transcript> Emit { get; private set; } = _ => throw new InvalidOperationException("Speech is not started.");
    public Task<ISpeechStream> StartSpeechAsync(Action<Transcript> transcript, Action<ProviderException> error, CancellationToken cancellation) =>
        StartSpeechAsync(SessionOptions.Legacy, transcript, error, cancellation);
    public Task<ISpeechStream> StartSpeechAsync(SessionOptions options, Action<Transcript> transcript, Action<ProviderException> error,
        CancellationToken cancellation)
    {
        Emit = transcript;
        return Task.FromResult<ISpeechStream>(new SilentStream());
    }
    public Task<Grounding> RetrieveAsync(string query, string objectId, CancellationToken cancellation) =>
        azure.RetrieveAsync(query, objectId, cancellation);
    public IAsyncEnumerable<string> AnswerAsync(IReadOnlyList<ConversationTurn> conversation, Grounding grounding, CancellationToken cancellation) =>
        azure.AnswerAsync(conversation, grounding, cancellation);
    public IAsyncEnumerable<string> AnswerAsync(IReadOnlyList<ConversationTurn> conversation, Grounding grounding, SessionOptions options,
        string responseRoute, CancellationToken cancellation) => azure.AnswerAsync(conversation, grounding, options, responseRoute, cancellation);
    public IAsyncEnumerable<ReplyUpdate> AnswerWithSuggestionsAsync(IReadOnlyList<ConversationTurn> conversation, Grounding grounding,
        SessionOptions options, string responseRoute, CancellationToken cancellation) =>
        azure.AnswerWithSuggestionsAsync(conversation, grounding, options, responseRoute, cancellation);
    public Task<bool> ShouldRespondAsync(IReadOnlyList<ConversationTurn> conversation, CancellationToken cancellation) =>
        azure.ShouldRespondAsync(conversation, cancellation);
    public Task<string?> TranslateReplyAsync(string english, TranslationContext context, CancellationToken cancellation) =>
        azure.TranslateReplyAsync(english, context, cancellation);

    private sealed class SilentStream : ISpeechStream
    {
        public void Write(byte[] audio) { }
        public Task CompleteInputAsync(CancellationToken cancellation) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
