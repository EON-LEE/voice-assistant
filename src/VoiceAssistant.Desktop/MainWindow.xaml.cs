using System.ComponentModel;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using VoiceAssistant.Desktop.Audio;
using VoiceAssistant.Desktop.Protocol;

namespace VoiceAssistant.Desktop;

public sealed record MaterialItem(string Id, string Title, int Chunks, string UpdatedAt);

public partial class MainWindow : Window
{
    private readonly ClientSettings settings;
    private readonly NativeIdentity identity;
    private readonly AuthenticatedApiClient api;
    private readonly SemaphoreSlim stopGate = new(1, 1);
    private readonly ReplyState state = new();
    private readonly Dictionary<string, string> meetingTranslations = [];
    private readonly Dictionary<string, string[]> replyTranslations = [];
    private readonly HashSet<string> translatingResponses = [];
    private readonly HashSet<string> translatingTurns = [];
    private readonly OverlayWindow overlay = new();
    private readonly HashSet<Task> enrichmentTasks = [];
    private MainWindow? demoPreview;
    private MeetingClient? client;
    private CancellationTokenSource? lifetime;
    private Task? running;
    private IDisposable? audioLease;
    private bool testingMicrophone;
    private MemoryAudioPlayback? playback;
    private PracticeWindow? practice;
    private Task? closeTask;
    private bool changingPause;
    private bool reading;
    private bool stopping;
    private CancellationTokenSource? readingLifetime;
    private Task? readingTask;
    private CancellationTokenSource? microphoneTestLifetime;
    private Task? microphoneTestTask;
    private bool overlayPrimary;
    private bool connectingAccount;

    public MainWindow(ClientSettings settings, NativeIdentity identity, AuthenticatedApiClient api)
    {
        this.settings = settings;
        this.identity = identity;
        this.api = api;
        InitializeComponent();
        Title = "Live Coach — Settings and materials";
        SignInButton.Visibility = DeviceCodeButton.Visibility = Visibility.Collapsed;
        ConsentBox.Visibility = ProfileConfirmBox.Visibility = KoreanBox.Visibility =
            PauseBox.Visibility = TopmostBox.Visibility = Visibility.Collapsed;
        StartButton.ToolTip = "Start sends microphone audio to Azure. Inform participants and obtain their permission before starting.";
        overlay.SettingsRequested += ShowSettings;
        overlay.StartRequested += () =>
        {
            Start_Click(this, new RoutedEventArgs());
            UpdateOverlay();
        };
        overlay.PauseRequested += () => PauseBox.IsChecked = PauseBox.IsChecked != true;
        overlay.RetryRequested += () => Request_Click(this, new RoutedEventArgs());
        overlay.TranslationRequested += RetryTranslations;
        overlay.StopRequested += () => Stop_Click(this, new RoutedEventArgs());
        overlay.ExitRequested += async () => await CloseForOwnerAsync();
        if (IsDemo) DeviceBox.ItemsSource = Array.Empty<CaptureEndpoint>();
        else RefreshEndpoints();
        AuthStatus.Text = settings.Mode == ConnectionMode.Demo
            ? "OFFLINE DEMO — canned responses only; no sign-in, network, or microphone capture."
            : $"Configured service: {settings.Endpoint}";
        SignInButton.IsEnabled = settings.Mode == ConnectionMode.Production;
        DeviceCodeButton.IsEnabled = settings.Mode == ConnectionMode.Production;
        DemoPreviewButton.IsEnabled = !IsDemo;
        MaterialsTab.IsEnabled = !IsDemo;
        ConsentBox.IsEnabled = !IsDemo;
        DeviceBox.IsEnabled = !IsDemo;
        RefreshButton.IsEnabled = !IsDemo;
        MicTestButton.IsEnabled = !IsDemo;
        ReadButton.IsEnabled = !IsDemo;
        StartButton.Content = IsDemo ? "Start offline overlay preview" : "Start live — send microphone audio to Azure";
        StatusText.Text = IsDemo ? "OFFLINE DEMO — no sign-in, network, or microphone." : StatusText.Text;
        if (IsDemo) MaterialsStatus.Text = "Personal materials are available only in authenticated production mode.";
        if (settings.Mode == ConnectionMode.Production)
        {
            var mode = settings.ResponseMode switch { "grounded" => 1, "conversation" => 2, _ => 0 };
            ModeBox.SelectedIndex = mode;
            TopicBox.Text = settings.Topic;
        }
        KoreanBox.IsChecked = true;
        KoreanBox.IsEnabled = false;
    }

    public void ShowPrimaryOverlay()
    {
        overlayPrimary = true;
        UpdateOverlay();
        overlay.Show();
    }

    private void ShowSettings()
    {
        overlay.Topmost = false;
        Show();
        Activate();
    }

    private void RetryTranslations()
    {
        if (IsDemo || lifetime is not { IsCancellationRequested: false } active) return;
        if (ErrorText.Text.StartsWith("Korean assist unavailable:", StringComparison.Ordinal) ||
            ErrorText.Text.StartsWith("Meeting translation unavailable:", StringComparison.Ordinal))
            ErrorText.Text = "";
        foreach (var turn in state.Turns.TakeLast(3).Where(turn => turn.IsFinal))
            TrackEnrichment(EnrichTranscriptAsync(turn.TurnId, turn.Text, active.Token));
        if (state.Display is { Complete: true } reply && !translatingResponses.Contains(reply.ResponseId))
            TrackEnrichment(EnrichReplyAsync(reply, active.Token));
    }

    private bool IsDemo => settings.Mode == ConnectionMode.Demo;
    private bool HasActiveSession => lifetime is not null;

    private async void SignIn_Click(object sender, RoutedEventArgs e)
    {
        SignInButton.IsEnabled = false;
        ErrorText.Text = "";
        try
        {
            await identity.SignInAsync();
            AuthStatus.Text = "Account connected. Sign-in is retained in the Windows-user encrypted cache.";
        }
        catch (Exception ex) { ErrorText.Text = $"Sign-in did not finish: {ex.Message}"; }
        finally { SignInButton.IsEnabled = true; }
    }

    private async void DeviceCode_Click(object sender, RoutedEventArgs e)
    {
        DeviceCodeButton.IsEnabled = false;
        ErrorText.Text = "";
        try
        {
            await identity.SignInWithDeviceCodeAsync((url, code) => Dispatcher.BeginInvoke(() =>
            {
                var dialog = new DeviceCodeWindow(url, code) { Owner = this };
                dialog.ShowDialog();
            }));
            AuthStatus.Text = "Account connected. Sign-in is retained in the Windows-user encrypted cache.";
        }
        catch (Exception ex) { ErrorText.Text = $"Device-code sign-in did not finish: {ex.Message}"; }
        finally { DeviceCodeButton.IsEnabled = true; }
    }

    private void DemoPreview_Click(object sender, RoutedEventArgs e)
    {
        if (IsDemo) return;
        if (demoPreview is { IsVisible: true })
        {
            demoPreview.Activate();
            return;
        }
        var previewSettings = settings with { Mode = ConnectionMode.Demo, TranscribeOnly = false };
        var previewIdentity = new NativeIdentity(previewSettings);
        var previewApi = new AuthenticatedApiClient(previewSettings, previewIdentity);
        demoPreview = new MainWindow(previewSettings, previewIdentity, previewApi)
        {
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Title = "OFFLINE DEMO — In-person English Coach"
        };
        demoPreview.Closed += (_, _) => demoPreview = null;
        demoPreview.Show();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshEndpoints();

    private async void MicTest_Click(object sender, RoutedEventArgs e)
    {
        if (testingMicrophone || DeviceBox.SelectedItem is not CaptureEndpoint device)
        {
            ErrorText.Text = "Select a microphone before the local level check. No audio is sent.";
            return;
        }
        testingMicrophone = true;
        MicTestButton.IsEnabled = StartButton.IsEnabled = false;
        StatusText.Text = "Testing microphone locally — audio is not sent to the service.";
        microphoneTestLifetime = new CancellationTokenSource();
        microphoneTestTask = RunMicrophoneTestAsync(device.Id, microphoneTestLifetime.Token);
        await microphoneTestTask;
    }

    private async Task RunMicrophoneTestAsync(string endpointId, CancellationToken token)
    {
        try
        {
            await MicrophoneLevelCheck.RunLocallyAsync(endpointId, level => Dispatcher.BeginInvoke(() =>
            {
                LevelMeter.Value = level;
                LevelText.Text = $"Local input level: {(level < 0.015 ? "very low — check mic placement" : $"{level:P0}")}";
            }), token);
            StatusText.Text = "Microphone test complete — no audio was sent.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { ErrorText.Text = $"Microphone test failed: {ex.Message}"; }
        finally
        {
            testingMicrophone = false;
            LevelMeter.Value = 0;
            LevelText.Text = "Input level: idle";
            MicTestButton.IsEnabled = StartButton.IsEnabled = true;
            microphoneTestTask = null;
            microphoneTestLifetime?.Dispose();
            microphoneTestLifetime = null;
        }
    }

    private void RefreshEndpoints()
    {
        try
        {
            var selected = (DeviceBox.SelectedItem as CaptureEndpoint)?.Id;
            DeviceBox.ItemsSource = MicrophoneAudioSource.ListEndpoints();
            var devices = (IReadOnlyList<CaptureEndpoint>)DeviceBox.ItemsSource;
            DeviceBox.SelectedItem = devices.FirstOrDefault(device => device.Id == selected) ?? devices.FirstOrDefault();
            ErrorText.Text = devices.Count == 0 ? "No active microphone is available. Connect one and refresh." : "";
        }
        catch (Exception ex)
        {
            DeviceBox.ItemsSource = Array.Empty<CaptureEndpoint>();
            ErrorText.Text = $"Could not enumerate physical microphone endpoints: {ex.Message}";
        }
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (HasActiveSession || testingMicrophone || connectingAccount) return;
        if (!IsDemo && DeviceBox.SelectedItem is not CaptureEndpoint)
        {
            ErrorText.Text = "Select an active physical microphone.";
            UpdateOverlay();
            return;
        }
        if (!await ConnectAccountForFeatureAsync()) return;
        var lease = AudioSessionCoordinator.TryAcquire();
        if (lease is null)
        {
            ErrorText.Text = "Another meeting or practice microphone session is active. Stop it before starting this one.";
            return;
        }
        ClientSettings meetingSettings;
        try
        {
            var selected = (ComboBoxItem)ModeBox.SelectedItem;
            meetingSettings = settings with
            {
                ResponseMode = (string)selected.Tag,
                Topic = TopicBox.Text.Trim(),
                ProfileName = ProfileConfirmBox.IsChecked == true ? ProfileNameBox.Text.Trim() : "",
                ProfileRole = ProfileConfirmBox.IsChecked == true ? ProfileRoleBox.Text.Trim() : "",
                ProfileProject = ProfileConfirmBox.IsChecked == true ? ProfileProjectBox.Text.Trim() : "",
                ProfileConfirmed = ProfileConfirmBox.IsChecked == true
            };
            meetingSettings.Validate();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            lease.Dispose();
            ErrorText.Text = ex.Message;
            return;
        }

        audioLease = lease;
        state.ResetSession();
        meetingTranslations.Clear();
        replyTranslations.Clear();
        translatingResponses.Clear();
        translatingTurns.Clear();
        ErrorText.Text = "";
        lifetime = new CancellationTokenSource();
        client = IsDemo ? new MeetingClient(new DemoMeetingTransport())
            : new MeetingClient(new TicketedMeetingTransport(api));
        StartButton.IsEnabled = SignInButton.IsEnabled = DeviceBox.IsEnabled = false;
        RefreshButton.IsEnabled = ConsentBox.IsEnabled = false;
        StopButton.IsEnabled = true;
        overlay.ShowSessionState(true,
            IsDemo ? "Offline canned preview — no Search" : "Grounding pending",
            TopmostBox.IsChecked == true, demo: IsDemo);
        if (!overlay.IsVisible) overlay.Show();
        ShowOverlayButton.Content = "Hide overlay";
        if (overlayPrimary) Hide();
        running = RunMeetingAsync(meetingSettings, (DeviceBox.SelectedItem as CaptureEndpoint)?.Id, lifetime.Token);
        await running;
    }

    private async Task RunMeetingAsync(ClientSettings meetingSettings, string? endpointId, CancellationToken token)
    {
        try
        {
            await client!.RunAsync(meetingSettings,
                () =>
                {
                    if (IsDemo) return new SyntheticAudioSource();
                    var source = new MicrophoneAudioSource(endpointId!);
                    source.LevelChanged += level => Dispatcher.BeginInvoke(() =>
                    {
                        LevelMeter.Value = level;
                        LevelText.Text = $"Input level: {(level < 0.015 ? "very low — check mic placement" : $"{level:P0}")}";
                    });
                    return source;
                },
                message => Dispatcher.BeginInvoke(() => ApplyMeetingEvent(message)),
                status => Dispatcher.BeginInvoke(() =>
                {
                    StatusText.Text = status;
                    UpdateOverlay();
                }), token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                ErrorText.Text = $"Session ended: {ex.Message} Start again to reconnect.";
                UpdateOverlay();
            });
        }
        finally
        {
            await Dispatcher.InvokeAsync(() =>
            {
                client = null;
                lifetime?.Cancel();
                readingLifetime?.Cancel();
                lifetime?.Dispose();
                lifetime = null;
                stopping = false;
                audioLease?.Dispose();
                audioLease = null;
                LevelMeter.Value = 0;
                LevelText.Text = "Input level: idle";
                changingPause = true;
                PauseBox.IsChecked = false;
                changingPause = false;
                state.Pause(true);
                ConsentBox.IsChecked = false;
                StartButton.IsEnabled = true;
                SignInButton.IsEnabled = settings.Mode == ConnectionMode.Production;
                DeviceBox.IsEnabled = RefreshButton.IsEnabled = ConsentBox.IsEnabled = !IsDemo;
                StopButton.IsEnabled = PauseBox.IsEnabled = RequestButton.IsEnabled = CancelButton.IsEnabled = false;
                StatusText.Text = IsDemo ? "OFFLINE DEMO — no sign-in, network, or microphone."
                    : "Stopped — no audio capture";
                UpdateOverlay();
                running = null;
            });
        }
    }

    private void ApplyMeetingEvent(ServerEvent message)
    {
        state.Apply(message);
        if (message.Type == "response.started" && state.Current?.ResponseId == message.ResponseId)
            ErrorText.Text = "";
        if (IsDemo && message.Type == "transcript.final" && message.TurnId == "demo-turn")
            meetingTranslations[message.TurnId] = "금요일까지 진행 상황을 공유해 주실 수 있나요? (고정 데모 예시)";
        if (message.Type == "transcript.final" && !IsDemo && KoreanBox.IsChecked == true &&
            !meetingTranslations.ContainsKey(message.TurnId!) &&
            lifetime is { IsCancellationRequested: false } transcriptLifetime)
            TrackEnrichment(EnrichTranscriptAsync(message.TurnId!, message.Text!, transcriptLifetime.Token));
        if (message.Type == "response.completed" && state.Current is { Complete: true } completed &&
            completed.ResponseId == message.ResponseId)
        {
            if (IsDemo && completed.TurnId == "demo-turn")
                replyTranslations[completed.ResponseId] =
                [
                    "네, 금요일까지 진행 상황을 공유할 수 있습니다. (고정 데모 예시)",
                    "짧은 진행 상황을 준비해서 금요일까지 보내겠습니다. (고정 데모 예시)"
                ];
            StatusText.Text = "Suggested reply ready — listening for the next meeting utterance.";
        }
        else if (message.Type == "transcript.partial")
            StatusText.Text = "Hearing the meeting — preparing a suggestion when this utterance ends.";
        else if (message.Type == "transcript.final")
            StatusText.Text = "Meeting utterance recognized — preparing your suggested reply.";
        Render();
        if (message.Type == "response.completed" && !IsDemo && closeTask is null &&
            lifetime is { IsCancellationRequested: false } activeLifetime &&
            KoreanBox.IsChecked == true && state.Current is { Complete: true } reply &&
            reply.ResponseId == message.ResponseId && !replyTranslations.ContainsKey(reply.ResponseId))
            TrackEnrichment(EnrichReplyAsync(reply, activeLifetime.Token));
    }

    private void TrackEnrichment(Task task)
    {
        enrichmentTasks.Add(task);
        _ = task.ContinueWith(_ => Dispatcher.BeginInvoke(new Action(() => enrichmentTasks.Remove(task))),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task EnrichTranscriptAsync(string turnId, string text, CancellationToken cancellationToken)
    {
        if (!translatingTurns.Add(turnId)) return;
        try
        {
            var korean = await TranslateAsync("question", text, cancellationToken);
            await Dispatcher.InvokeAsync(() =>
            {
                if (cancellationToken.IsCancellationRequested || !state.Turns.Any(turn => turn.TurnId == turnId)) return;
                meetingTranslations[turnId] = korean;
                RenderConversation();
                UpdateOverlay();
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (!state.Turns.Any(turn => turn.TurnId == turnId)) return;
                meetingTranslations[turnId] = "번역 취소됨 · 세션 종료";
                RenderConversation();
                UpdateOverlay();
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (cancellationToken.IsCancellationRequested) return;
                meetingTranslations[turnId] = TranslationFailure(ex);
                ErrorText.Text = $"Meeting translation unavailable: {ex.Message}";
                RenderConversation();
                UpdateOverlay();
            });
        }
        finally { translatingTurns.Remove(turnId); }
    }

    private async Task EnrichReplyAsync(ReplySnapshot reply, CancellationToken cancellationToken)
    {
        if (!translatingResponses.Add(reply.ResponseId)) return;
        var answers = reply.Answers;
        replyTranslations[reply.ResponseId] = answers.Select(_ => "한국어 번역 중…").ToArray();
        UpdateOverlay();
        try
        {
            await Task.WhenAll(answers.Select(async (answer, index) =>
            {
                try
                {
                    var korean = await TranslateAsync("reply", answer, cancellationToken);
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (cancellationToken.IsCancellationRequested ||
                            !replyTranslations.TryGetValue(reply.ResponseId, out var translations)) return;
                        translations[index] = korean;
                        Render();
                    });
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (!replyTranslations.TryGetValue(reply.ResponseId, out var translations)) return;
                        translations[index] = "번역 취소됨 · 세션 종료";
                        Render();
                    });
                }
                catch (Exception ex)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (cancellationToken.IsCancellationRequested ||
                            !replyTranslations.TryGetValue(reply.ResponseId, out var translations)) return;
                        translations[index] = TranslationFailure(ex) + " · 우클릭으로 재시도";
                        ErrorText.Text = $"Korean assist unavailable: {ex.Message}";
                        Render();
                    });
                }
            }));
        }
        finally
        {
            translatingResponses.Remove(reply.ResponseId);
            var retained = new[] { state.Current?.ResponseId, state.LastCompleted?.ResponseId, state.Pinned?.ResponseId };
            foreach (var stale in replyTranslations.Keys.Where(id => !retained.Contains(id) &&
                         !translatingResponses.Contains(id)).ToArray())
                replyTranslations.Remove(stale);
        }
    }

    private async Task<string> TranslateAsync(string kind, string text, CancellationToken cancellationToken)
    {
        var translations = new List<string>();
        foreach (var chunk in TranslationChunks(text))
        {
            using var document = await api.PostJsonAsync("/api/assist/enrich",
                new { kind, text = chunk, translationOnly = true }, cancellationToken);
            var korean = document.RootElement.GetProperty("korean").GetString();
            if (string.IsNullOrWhiteSpace(korean))
                throw new InvalidDataException("The translation response is empty.");
            translations.Add(korean);
        }
        return string.Join(" ", translations);
    }

    private static string TranslationFailure(Exception exception) => exception switch
    {
        ApiRequestException { Code: "busy" } => "번역 요청 제한 · 잠시 후 재시도",
        ApiRequestException { Code: "provider_timeout" } => "한국어 번역 시간 초과",
        ApiRequestException { Code: "unauthorized" or "forbidden" } => "한국어 번역 인증 오류",
        _ => "한국어 번역 실패"
    };

    private static IEnumerable<string> TranslationChunks(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidDataException("The translation input is empty.");
        var remaining = text.Trim();
        while (remaining.Length > 600)
        {
            var boundary = remaining.LastIndexOfAny([' ', '\n', '\r', '\t'], 599, 600);
            if (boundary <= 0) boundary = 600;
            yield return remaining[..boundary];
            remaining = remaining[boundary..].TrimStart();
        }
        if (remaining.Length > 0) yield return remaining;
    }

    private void Render()
    {
        RenderConversation();
        ReplyBox.Text = state.Display?.Text ?? "";
        GroundingText.Text = GroundingLabel(state.Display);
        SourcesText.Text = FormatSources(state.Display);
        KoreanText.Text = state.Display is { } displayed &&
            replyTranslations.TryGetValue(displayed.ResponseId, out var translations) ? translations[0] :
            state.Display is null ? "" : IsDemo ? "오프라인 예시 · 실제 번역 요청 없음" : "한국어 번역 준비 중…";
        PinnedBox.Text = state.Pinned?.Text ?? "";
        PinnedSourcesText.Text = FormatSources(state.Pinned);
        PinButton.IsEnabled = !string.IsNullOrWhiteSpace(state.Current?.Text);
        RequestButton.IsEnabled = client?.IsReady == true && PauseBox.IsChecked != true && state.Turns.Any(turn => turn.IsFinal);
        CancelButton.IsEnabled = client?.IsReady == true;
        PauseBox.IsEnabled = client?.IsReady == true;
        ReadButton.IsEnabled = !IsDemo && !reading && state.Current is { Complete: true };
        if (state.Error is not null) ErrorText.Text = state.Error;
        UpdateOverlay();
    }

    private void RenderConversation()
    {
        ConversationPanel.Children.Clear();
        var turns = state.Turns.TakeLast(12).ToArray();
        var retained = turns.Select(turn => turn.TurnId).ToHashSet(StringComparer.Ordinal);
        foreach (var stale in meetingTranslations.Keys.Where(turnId => !retained.Contains(turnId)).ToArray())
            meetingTranslations.Remove(stale);
        foreach (var turn in turns)
        {
            var content = new StackPanel();
            content.Children.Add(new TextBlock
            {
                Text = turn.IsFinal ? "MEETING · RECOGNIZED ENGLISH" : "MEETING · LIVE TRANSCRIPT",
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#536579"))
            });
            content.Children.Add(new TextBlock
            {
                Text = turn.Text,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 15,
                Margin = new Thickness(0, 4, 0, 0)
            });
            if (KoreanBox.IsChecked == true && !IsDemo && turn.IsFinal)
                content.Children.Add(new TextBlock
                {
                    Text = meetingTranslations.TryGetValue(turn.TurnId, out var translation)
                        ? translation : "한국어 번역 중…",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 14,
                    Foreground = new System.Windows.Media.SolidColorBrush(
                        (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#174A7E")),
                    Margin = new Thickness(0, 4, 0, 0)
                });
            ConversationPanel.Children.Add(new Border
            {
                Background = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F1F4F8")),
                BorderBrush = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#D8DFE9")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 0, 8),
                Child = content
            });
        }
    }

    private void UpdateOverlay()
    {
        var displayed = state.Display;
        overlay.ShowSessionState(client?.IsReady == true,
            IsDemo ? "고정 데모 예시 · 실제 문서 검색 아님" : displayed?.Grounding switch
            {
                "grounded" => "근거 있음 · 내 업로드 문서",
                "no_matches" => "문서 근거 없음 · 사실 확인 필요",
                "unavailable" => "문서 검색 실패 · 사실 답변에 의존하지 마세요",
                "disabled" => "이번 답변은 문서 검색을 사용하지 않음",
                _ => "문서 근거 상태는 답변 완료 후 표시됩니다"
            },
            !IsVisible && TopmostBox.IsChecked == true, IsDemo, PauseBox.IsChecked == true);
        overlay.ShowConversation(state.Turns, meetingTranslations, IsDemo);
        var translations = displayed is not null && replyTranslations.TryGetValue(displayed.ResponseId, out var values)
            ? values : IsDemo && displayed is not null
                ? displayed.Answers.Select(_ => "오프라인 예시 · 실제 번역 요청 없음").ToArray() : [];
        var question = state.Turns.FirstOrDefault(turn => turn.TurnId == displayed?.TurnId)?.Text ??
            (displayed is null ? "" : "이전 대화");
        overlay.ShowSuggestions(displayed, translations, state.Turns.LastOrDefault()?.TurnId ?? "", question,
            client?.IsReady == true && (state.Current is { Complete: false } ||
                state.Turns.LastOrDefault()?.IsFinal == true && displayed?.TurnId != state.Turns.LastOrDefault()?.TurnId),
            ErrorText.Text, "이전 대화 맥락 기본 연결 · 추천은 실제 발화로 기억하지 않음");
        overlay.SetSessionControls(HasActiveSession, client?.IsReady == true, PauseBox.IsChecked == true,
            !IsDemo && lifetime is { IsCancellationRequested: false });
    }

    private string GroundingLabel(ReplySnapshot? reply)
    {
        if (IsDemo)
            return "OFFLINE DEMO: canned reply; no personal materials or Azure AI Search.";
        if (reply is null) return "Personal-material grounding status appears when the answer completes.";
        return reply.Grounding switch
        {
            "grounded" => "Grounded: related owner-authorized material was retrieved. Sources are references, not a guarantee.",
            "no_matches" => "No relevant personal material matched. This answer is transcript-only; verify any factual claim.",
            "unavailable" => "Search is unavailable. Do not rely on this answer for factual details.",
            "disabled" => "Search was not used for this response route.",
            _ => "Grounding status was not supplied."
        };
    }

    private static string FormatSources(ReplySnapshot? reply) => reply is null ? "" :
        string.Join(Environment.NewLine, reply.Sources.Select(source =>
            $"{source.Title} — {source.Url}{(source.UpdatedAt is null ? "" : $" · updated {source.UpdatedAt}")}"));

    private async void Stop_Click(object sender, RoutedEventArgs e) => await StopSessionAsync();

    private async Task StopSessionAsync()
    {
        await stopGate.WaitAsync();
        try
        {
            var active = client;
            stopping = true;
            readingLifetime?.Cancel();
            StopButton.IsEnabled = PauseBox.IsEnabled = RequestButton.IsEnabled = CancelButton.IsEnabled = ReadButton.IsEnabled = false;
            var activePlayback = playback;
            playback = null;
            activePlayback?.Dispose();
            if (active is not null)
            {
                try { await active.StopAsync(); }
                catch (Exception ex) { ErrorText.Text = $"Microphone stopped locally; session.stop failed: {ex.Message}"; }
                finally { lifetime?.Cancel(); }
                if (running is not null) await running;
            }
            if (readingTask is not null) await readingTask;
            if (enrichmentTasks.Count > 0) await Task.WhenAll(enrichmentTasks);
            enrichmentTasks.Clear();
            stopping = false;
        }
        finally { stopGate.Release(); }
    }

    private async void Pause_Changed(object sender, RoutedEventArgs e)
    {
        if (changingPause || client is null || lifetime is null) return;
        try
        {
            bool paused = PauseBox.IsChecked == true;
            state.Pause(paused);
            await client.SetPausedAsync(paused, lifetime.Token);
            StatusText.Text = paused ? "Paused — dropping microphone audio locally" : "Connected — room microphone streaming";
            Render();
        }
        catch (Exception ex) { ErrorText.Text = $"Could not change microphone state: {ex.Message}"; lifetime?.Cancel(); }
    }

    private async void Request_Click(object sender, RoutedEventArgs e)
    {
        if (client is null || lifetime is null) return;
        try { state.BeginRequest(); await client.RequestResponseAsync(lifetime.Token); }
        catch (Exception ex) { ErrorText.Text = $"Reply request failed: {ex.Message}"; }
        Render();
    }

    private async void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (client is null || lifetime is null) return;
        state.CancelCurrent();
        try { await client.CancelResponseAsync(lifetime.Token); }
        catch (Exception ex) { ErrorText.Text = $"Cancel failed: {ex.Message}"; }
        Render();
    }

    private void Pin_Click(object sender, RoutedEventArgs e) { state.Pin(); Render(); }
    private void Unpin_Click(object sender, RoutedEventArgs e) { state.Unpin(); Render(); }
    private void Topmost_Changed(object sender, RoutedEventArgs e) => overlay.Topmost = TopmostBox.IsChecked == true;

    private void ShowOverlay_Click(object sender, RoutedEventArgs e)
    {
        if (overlay.IsVisible)
        {
            overlay.Hide();
            ShowOverlayButton.Content = "Show overlay";
            return;
        }
        UpdateOverlay();
        overlay.Show();
        ShowOverlayButton.Content = "Hide overlay";
    }

    private async void Read_Click(object sender, RoutedEventArgs e)
    {
        if (IsDemo || reading) return;
        if (state.Current is not { Complete: true } reply) return;
        reading = true;
        ReadButton.IsEnabled = false;
        readingLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime?.Token ?? CancellationToken.None);
        readingTask = ReadAnswerAsync(reply, readingLifetime.Token);
        await readingTask;
    }

    private async Task ReadAnswerAsync(ReplySnapshot reply, CancellationToken token)
    {
        bool resume = client?.IsReady == true && PauseBox.IsChecked != true;
        try
        {
            if (resume)
            {
                changingPause = true;
                PauseBox.IsChecked = true;
                changingPause = false;
                state.Pause(true);
                await client!.SetPausedAsync(true, lifetime!.Token);
            }
            byte[] audio = await api.PostAudioAsync("/api/assist/speak",
                new { text = reply.Text, voice = "coach", rate = "normal" }, token);
            playback?.Dispose();
            playback = new MemoryAudioPlayback(audio);
            await playback.PlayAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { ErrorText.Text = $"Read-aloud unavailable: {ex.Message}"; }
        finally
        {
            var activePlayback = playback;
            playback = null;
            activePlayback?.Dispose();
            reading = false;
            readingLifetime?.Dispose();
            readingLifetime = null;
            readingTask = null;
            if (resume && !stopping && client is not null && lifetime is not null && !lifetime.IsCancellationRequested)
            {
                changingPause = true;
                PauseBox.IsChecked = false;
                changingPause = false;
                state.Pause(false);
                try { await client.SetPausedAsync(false, lifetime.Token); }
                catch (Exception ex) { ErrorText.Text = $"Microphone could not resume after read-aloud: {ex.Message}"; }
            }
            Render();
        }
    }

    private async void Practice_Click(object sender, RoutedEventArgs e)
    {
        if (HasActiveSession)
        {
            ErrorText.Text = "Stop the meeting microphone before opening practice.";
            return;
        }
        if (!await ConnectAccountForFeatureAsync()) return;
        if (practice is { IsVisible: true }) { practice.Activate(); return; }
        practice = new PracticeWindow(settings, identity, api);
        practice.Closed += (_, _) => practice = null;
        practice.Show();
    }

    private async void MaterialsRefresh_Click(object sender, RoutedEventArgs e) => await RefreshMaterialsAsync();

    private async Task<bool> ConnectAccountForFeatureAsync()
    {
        if (closeTask is not null) return false;
        if (IsDemo || identity.IsSignedIn) return true;
        if (connectingAccount)
        {
            ErrorText.Text = "Account connection is already in progress.";
            UpdateOverlay();
            return false;
        }
        connectingAccount = true;
        bool restoreStart = StartButton.IsEnabled;
        StartButton.IsEnabled = false;
        ErrorText.Text = "";
        StatusText.Text = "Connecting your Microsoft account — no microphone capture yet.";
        try
        {
            await identity.SignInAsync();
            AuthStatus.Text = "Account connected. Uploaded materials remain protected under your account.";
            return closeTask is null;
        }
        catch (Exception ex)
        {
            ErrorText.Text = $"Account connection did not finish: {ex.Message}";
            MaterialsStatus.Text = ErrorText.Text;
            UpdateOverlay();
            return false;
        }
        finally
        {
            connectingAccount = false;
            StartButton.IsEnabled = restoreStart;
        }
    }

    private async Task RefreshMaterialsAsync()
    {
        if (IsDemo)
        {
            MaterialsStatus.Text = "Personal materials are unavailable in offline demo mode.";
            return;
        }
        if (!await ConnectAccountForFeatureAsync()) return;
        try
        {
            using var response = await api.SendKnowledgeAsync(HttpMethod.Get, "/api/knowledge", null, CancellationToken.None);
            response.EnsureSuccessStatusCode();
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            var documents = document.RootElement.GetProperty("documents").EnumerateArray().Select(item =>
                new MaterialItem(item.GetProperty("id").GetString()!, item.GetProperty("title").GetString()!,
                    item.GetProperty("chunks").GetInt32(), item.GetProperty("updatedAt").GetString()!)).ToArray();
            MaterialsList.ItemsSource = documents;
            MaterialsList.DisplayMemberPath = nameof(MaterialItem.Title);
            MaterialsStatus.Text = $"{documents.Length} personal document(s); {documents.Sum(item => item.Chunks)} chunks.";
        }
        catch (Exception ex) { MaterialsStatus.Text = $"Could not load your personal materials: {ex.Message}"; }
    }

    private async void Upload_Click(object sender, RoutedEventArgs e)
    {
        if (IsDemo) { MaterialsStatus.Text = "Uploads are unavailable in offline demo mode."; return; }
        if (!await ConnectAccountForFeatureAsync()) return;
        var picker = new OpenFileDialog { Title = "Select a material to upload", CheckFileExists = true, Multiselect = false };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            var file = new FileInfo(picker.FileName);
            if (file.Length > 5 * 1024 * 1024) throw new InvalidDataException("Material files must be 5 MiB or smaller.");
            var form = new MultipartFormDataContent();
            var bytes = await File.ReadAllBytesAsync(file.FullName);
            form.Add(new ByteArrayContent(bytes), "file", file.Name);
            using var response = await api.SendKnowledgeAsync(HttpMethod.Post, "/api/knowledge", form, CancellationToken.None);
            if (response.StatusCode != System.Net.HttpStatusCode.Created) throw new ApiRequestException("knowledge_unavailable");
            MaterialsStatus.Text = "Material uploaded and indexed for your account.";
            await RefreshMaterialsAsync();
        }
        catch (Exception ex) { MaterialsStatus.Text = $"Upload failed: {ex.Message}"; }
    }

    private async void AddNotes_Click(object sender, RoutedEventArgs e)
    {
        if (IsDemo) { MaterialsStatus.Text = "Notes are unavailable in offline demo mode."; return; }
        if (!await ConnectAccountForFeatureAsync()) return;
        var dialog = new NotesWindow { Owner = this };
        if (dialog.ShowDialog() != true) return;
        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(new { title = dialog.NoteTitle, text = dialog.NoteText }));
            content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
            using var response = await api.SendKnowledgeAsync(HttpMethod.Post, "/api/knowledge", content, CancellationToken.None);
            if (response.StatusCode != System.Net.HttpStatusCode.Created) throw new ApiRequestException("knowledge_unavailable");
            MaterialsStatus.Text = "Notes uploaded and indexed for your account.";
            await RefreshMaterialsAsync();
        }
        catch (Exception ex) { MaterialsStatus.Text = $"Notes upload failed: {ex.Message}"; }
    }

    private async void DeleteMaterial_Click(object sender, RoutedEventArgs e)
    {
        if (IsDemo) { MaterialsStatus.Text = "Delete is unavailable in offline demo mode."; return; }
        if (MaterialsList.SelectedItem is not MaterialItem material) return;
        if (MessageBox.Show(this, $"Delete '{material.Title}' from your personal materials?",
                "Delete material", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        try
        {
            using var response = await api.SendKnowledgeAsync(HttpMethod.Delete, $"/api/knowledge/{material.Id}", null, CancellationToken.None);
            if (response.StatusCode != System.Net.HttpStatusCode.NoContent) throw new ApiRequestException("knowledge_unavailable");
            await RefreshMaterialsAsync();
        }
        catch (Exception ex) { MaterialsStatus.Text = $"Could not delete this material: {ex.Message}"; }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (closeTask is not null) { e.Cancel = true; return; }
        e.Cancel = true;
        if (overlayPrimary)
        {
            Hide();
            UpdateOverlay();
            return;
        }
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            closeTask ??= FinishCloseAsync();
            await closeTask;
        }));
    }

    public Task CloseForOwnerAsync()
    {
        if (closeTask is null) closeTask = FinishCloseAsync();
        return closeTask;
    }

    private async Task FinishCloseAsync()
    {
        if (demoPreview is { IsVisible: true })
            await demoPreview.CloseForOwnerAsync();
        microphoneTestLifetime?.Cancel();
        if (microphoneTestTask is not null) await microphoneTestTask;
        if (practice is { IsVisible: true })
            await practice.CloseForOwnerAsync();
        await StopSessionAsync();
        overlay.CloseAfterCleanup();
        api.Dispose();
        Closing -= Window_Closing;
        Close();
    }
}
