using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using VoiceAssistant.Desktop.Protocol;

namespace VoiceAssistant.Desktop.Tests;

public sealed class OverlayTranslationTests
{
    [Fact]
    public async Task OptionsTranslateIndependentlyAndLateResultsDoNotReplaceCurrentAnswer()
    {
        await OnUiThread(async () =>
        {
            var delayed = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var api = new AuthenticatedApiClient(new ClientSettings { Mode = ConnectionMode.Production },
                new TestIdentity(), new TranslationHandler(async text =>
                    text == "Old answer." ? await delayed.Task : JsonResponse(new { korean = $"한국어: {text}" })));
            var window = new MainWindow(new ClientSettings { Mode = ConnectionMode.Production },
                new NativeIdentity(new ClientSettings { Mode = ConnectionMode.Production }), api);
            try
            {
                void Apply(ServerEvent message) => Invoke(window, "ApplyMeetingEvent", message);
                var state = (ReplyState)typeof(MainWindow).GetField("state",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                var overlay = (OverlayWindow)typeof(MainWindow).GetField("overlay",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                Apply(new("transcript.final", "one", 1, "First question?"));
                Apply(new("response.started", "one", ResponseId: "r1"));
                Apply(new("response.completed", "one", Text: "Old answer.", ResponseId: "r1", Sources: []));
                var oldTask = (Task)Invoke(window, "EnrichReplyAsync", state.Current!, CancellationToken.None)!;
                Assert.Contains("번역 중", ((TextBlock)overlay.FindName("KoreanText")).Text);
                Apply(new("transcript.final", "two", 1, "Next question?"));
                Apply(new("response.started", "two", ResponseId: "r2"));
                Apply(new("response.completed", "two", Text: "New answer.", ResponseId: "r2", Sources: [],
                    Suggestions: ["New answer.", "Alternative answer."]));
                await (Task)Invoke(window, "EnrichReplyAsync", state.Current!, CancellationToken.None)!;
                Assert.Equal("한국어: New answer.", ((TextBlock)overlay.FindName("KoreanText")).Text);
                Assert.Equal("한국어: Alternative answer.", ((TextBlock)overlay.FindName("AlternativeKoreanText")).Text);
                delayed.SetResult(JsonResponse(new { korean = "이전 답변 번역" }));
                await oldTask;
                Assert.Equal("한국어: New answer.", ((TextBlock)overlay.FindName("KoreanText")).Text);
                Assert.Equal("New answer.", ((TextBlock)overlay.FindName("AnswerText")).Text);
                Assert.Equal(2, state.Turns.Count);
            }
            finally { await window.CloseForOwnerAsync(); }
        });
    }

    [Fact]
    public async Task EmptyTranslationIsVisibleFailureRatherThanBlankOrSuccess()
    {
        await OnUiThread(async () =>
        {
            var settings = new ClientSettings { Mode = ConnectionMode.Production };
            using var api = new AuthenticatedApiClient(settings, new TestIdentity(),
                new TranslationHandler(_ => Task.FromResult(JsonResponse(new { korean = "" }))));
            var window = new MainWindow(settings, new NativeIdentity(settings), api);
            try
            {
                Invoke(window, "ApplyMeetingEvent", new ServerEvent("transcript.final", "t", 1, "Question?"));
                Invoke(window, "ApplyMeetingEvent", new ServerEvent("response.started", "t", ResponseId: "r"));
                Invoke(window, "ApplyMeetingEvent", new ServerEvent("response.completed", "t", Text: "Answer.",
                    ResponseId: "r", Sources: []));
                var state = (ReplyState)typeof(MainWindow).GetField("state",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                await (Task)Invoke(window, "EnrichReplyAsync", state.Current!, CancellationToken.None)!;
                var overlay = (OverlayWindow)typeof(MainWindow).GetField("overlay",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                Assert.Contains("번역 실패", ((TextBlock)overlay.FindName("KoreanText")).Text);
                Assert.Contains("empty", ((TextBlock)overlay.FindName("SessionError")).Text);
                Assert.Equal(Visibility.Visible, ((TextBlock)overlay.FindName("SessionError")).Visibility);
                Assert.Equal("Answer.", ((TextBlock)overlay.FindName("AnswerText")).Text);
            }
            finally { await window.CloseForOwnerAsync(); }
        });
    }

    [Fact]
    public async Task RecognizedSpeechGetsKoreanInOverlayAndCancelledReplyHasVisibleState()
    {
        await OnUiThread(async () =>
        {
            var settings = new ClientSettings { Mode = ConnectionMode.Production };
            using var api = new AuthenticatedApiClient(settings, new TestIdentity(),
                new TranslationHandler(_ => Task.FromResult(JsonResponse(new { korean = "출시 조건은 무엇인가요?" })), "question"));
            var window = new MainWindow(settings, new NativeIdentity(settings), api);
            try
            {
                Invoke(window, "ApplyMeetingEvent", new ServerEvent("transcript.final", "t", 1, "What are the launch conditions?"));
                await (Task)Invoke(window, "EnrichTranscriptAsync", "t", "What are the launch conditions?",
                    CancellationToken.None)!;
                var overlay = (OverlayWindow)typeof(MainWindow).GetField("overlay",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                var conversation = (StackPanel)overlay.FindName("ConversationPanel");
                var bubble = Assert.IsType<StackPanel>(Assert.IsType<Border>(conversation.Children[0]).Child);
                Assert.Equal("출시 조건은 무엇인가요?", Assert.IsType<TextBlock>(bubble.Children[1]).Text);
                Invoke(window, "ApplyMeetingEvent", new ServerEvent("response.started", "t", ResponseId: "r"));
                Invoke(window, "ApplyMeetingEvent", new ServerEvent("response.completed", "t", Text: "Review first.",
                    ResponseId: "r", Sources: []));
                var state = (ReplyState)typeof(MainWindow).GetField("state",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                await (Task)Invoke(window, "EnrichReplyAsync", state.Current!, cancelled.Token)!;
                Assert.Contains("번역 취소됨", ((TextBlock)overlay.FindName("KoreanText")).Text);
                Assert.Equal("Review first.", ((TextBlock)overlay.FindName("AnswerText")).Text);
            }
            finally { await window.CloseForOwnerAsync(); }
        });
    }

    [Fact]
    public async Task PrimaryStartupOnlyShowsOverlayAndSettingsCloseDoesNotExit()
    {
        await OnUiThread(async () =>
        {
            var settings = new ClientSettings();
            using var api = new AuthenticatedApiClient(settings, new TestIdentity());
            var window = new MainWindow(settings, new NativeIdentity(settings), api);
            try
            {
                window.ShowPrimaryOverlay();
                Assert.False(window.IsVisible);
                var overlay = (OverlayWindow)typeof(MainWindow).GetField("overlay",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                Assert.True(overlay.IsVisible);
                var menu = ((Border)overlay.FindName("Panel")).ContextMenu;
                Assert.NotNull(menu);
                ((MenuItem)menu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Assert.True(window.IsVisible);
                window.Close();
                Assert.False(window.IsVisible);
                Assert.True(overlay.IsVisible);
                Assert.False(((Button)window.FindName("StopButton")).IsEnabled);
                Assert.True(((CheckBox)window.FindName("KoreanBox")).IsChecked);
                Assert.False(((CheckBox)window.FindName("KoreanBox")).IsEnabled);
                Assert.Equal(Visibility.Collapsed, ((CheckBox)window.FindName("ConsentBox")).Visibility);
                Assert.Equal(Visibility.Collapsed, ((CheckBox)window.FindName("KoreanBox")).Visibility);
                Assert.Equal(Visibility.Collapsed, ((Button)window.FindName("SignInButton")).Visibility);
                Assert.Equal(Visibility.Collapsed, ((Button)window.FindName("DeviceCodeButton")).Visibility);
            }
            finally { await window.CloseForOwnerAsync(); }
        });
    }

    [Fact]
    public async Task ClosingWindowDoesNotStartNewAccountConnection()
    {
        await OnUiThread(async () =>
        {
            var settings = new ClientSettings { Mode = ConnectionMode.Production };
            using var api = new AuthenticatedApiClient(settings, new TestIdentity());
            var window = new MainWindow(settings, new NativeIdentity(settings), api);
            await window.CloseForOwnerAsync();
            Assert.False(await (Task<bool>)Invoke(window, "ConnectAccountForFeatureAsync")!);
        });
    }

    private static object? Invoke(MainWindow window, string name, params object[] arguments) =>
        typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, arguments);

    private static HttpResponseMessage JsonResponse(object value)
    {
        var content = new StringContent(JsonSerializer.Serialize(value));
        content.Headers.ContentType = new("application/json");
        return new(HttpStatusCode.OK) { Content = content };
    }

    private sealed class TestIdentity : IAccessTokenProvider
    {
        public Task<string> AcquireTokenAsync(bool interactive, CancellationToken cancellationToken = default) =>
            Task.FromResult("test-only");
    }

    private sealed class TranslationHandler(Func<string, Task<HttpResponseMessage>> reply, string expectedKind = "reply") : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("/api/assist/enrich", request.RequestUri!.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal(expectedKind, body.RootElement.GetProperty("kind").GetString());
            return await reply(body.RootElement.GetProperty("text").GetString()!);
        }
    }

    private static async Task OnUiThread(Func<Task> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(async () =>
            {
                try { await action(); done.TrySetResult(); }
                catch (Exception ex) { done.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
    }
}
