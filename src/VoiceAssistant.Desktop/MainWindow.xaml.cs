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

    public MainWindow(ClientSettings settings, NativeIdentity identity, AuthenticatedApiClient api)
    {
        this.settings = settings;
        this.identity = identity;
        this.api = api;
        InitializeComponent();
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
        KoreanBox.IsEnabled = !IsDemo;
        ReadButton.IsEnabled = !IsDemo;
        StartButton.Content = IsDemo ? "Start offline overlay preview" : "Start meeting overlay";
        StatusText.Text = IsDemo ? "OFFLINE DEMO — no sign-in, network, or microphone." : StatusText.Text;
        if (IsDemo) MaterialsStatus.Text = "Personal materials are available only in authenticated production mode.";
        if (settings.Mode == ConnectionMode.Production)
        {
            var mode = settings.ResponseMode switch { "grounded" => 1, "conversation" => 2, _ => 0 };
            ModeBox.SelectedIndex = mode;
            TopicBox.Text = settings.Topic;
        }
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
            AuthStatus.Text = "Signed in with the configured Entra tenant. Tokens remain in memory.";
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
            AuthStatus.Text = "Signed in with the configured Entra tenant. Tokens remain in memory.";
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
        if (testingMicrophone || ConsentBox.IsChecked != true || DeviceBox.SelectedItem is not CaptureEndpoint device)
        {
            ErrorText.Text = "Select a microphone and confirm participant consent before the local level check.";
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
        if (HasActiveSession || testingMicrophone) return;
        if (!IsDemo && !identity.IsSignedIn)
        {
            ErrorText.Text = "Sign in with Microsoft before starting the meeting session.";
            return;
        }
        if (!IsDemo && ConsentBox.IsChecked != true)
        {
            ErrorText.Text = "Confirm participant consent before starting microphone capture.";
            return;
        }
        if (!IsDemo && DeviceBox.SelectedItem is not CaptureEndpoint)
        {
            ErrorText.Text = "Select an active physical microphone.";
            return;
        }
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
        ErrorText.Text = "";
        lifetime = new CancellationTokenSource();
        client = IsDemo ? new MeetingClient(new DemoMeetingTransport())
            : new MeetingClient(new TicketedMeetingTransport(api));
        StartButton.IsEnabled = SignInButton.IsEnabled = DeviceBox.IsEnabled = false;
        RefreshButton.IsEnabled = ConsentBox.IsEnabled = false;
        StopButton.IsEnabled = true;
        overlay.ShowMeetingState(true, "", "",
            IsDemo ? "Offline canned preview — no Search" : "Grounding pending",
            TopmostBox.IsChecked == true, demo: IsDemo);
        if (!overlay.IsVisible) overlay.Show();
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
                    overlay.ShowMeetingState(client?.IsReady == true, LatestQuestion(), state.Current?.Text ?? state.Pinned?.Text ?? "",
                        GroundingLabel(state.Current), TopmostBox.IsChecked == true, KoreanText.Text, ReadingText.Text,
                        IsDemo, PauseBox.IsChecked == true);
                }), token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { await Dispatcher.InvokeAsync(() => ErrorText.Text = $"Session ended: {ex.Message} Start again to reconnect."); }
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
                StartButton.IsEnabled = DeviceBox.IsEnabled = SignInButton.IsEnabled = true;
                RefreshButton.IsEnabled = ConsentBox.IsEnabled = true;
                StopButton.IsEnabled = PauseBox.IsEnabled = RequestButton.IsEnabled = CancelButton.IsEnabled = false;
                StatusText.Text = IsDemo ? "OFFLINE DEMO — no sign-in, network, or microphone."
                    : "Stopped — no audio capture";
                overlay.ShowMeetingState(false, LatestQuestion(), state.Pinned?.Text ?? "",
                    IsDemo ? "Offline canned response — not a live Search result" : "Microphone released",
                    TopmostBox.IsChecked == true, demo: IsDemo);
                running = null;
            });
        }
    }

    private void ApplyMeetingEvent(ServerEvent message)
    {
        if (message.Type is "response.started" or "transcript.partial" or "transcript.final")
        {
            KoreanText.Text = "";
            ReadingText.Text = "";
        }
        state.Apply(message);
        Render();
        overlay.ShowMeetingState(client?.IsReady == true, LatestQuestion(),
            state.Current?.Text ?? state.Pinned?.Text ?? "Listening for a reply…",
            GroundingLabel(state.Current), TopmostBox.IsChecked == true, KoreanText.Text, ReadingText.Text,
            IsDemo, PauseBox.IsChecked == true);
        if (message.Type == "response.completed" && !IsDemo && closeTask is null &&
            lifetime is { IsCancellationRequested: false } activeLifetime &&
            KoreanBox.IsChecked == true && state.Current is { Complete: true } reply)
            enrichmentTasks.Add(EnrichReplyAsync(reply.Text, activeLifetime.Token));
    }

    private async Task EnrichReplyAsync(string text, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await api.PostJsonAsync("/api/assist/enrich", new { kind = "reply", text }, cancellationToken);
            await Dispatcher.InvokeAsync(() =>
            {
                if (state.Current?.Text != text) return;
                KoreanText.Text = document.RootElement.GetProperty("korean").GetString() ?? "";
                var pronunciation = document.RootElement.GetProperty("pronunciation");
                ReadingText.Text = pronunciation.ValueKind == JsonValueKind.Array
                    ? string.Join(" · ", pronunciation.EnumerateArray().Select(item =>
                        $"{item.GetProperty("en").GetString()} ({item.GetProperty("ko").GetString()})"))
                    : "";
                UpdateOverlay();
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) { await Dispatcher.InvokeAsync(() => ErrorText.Text = $"Korean assist unavailable: {ex.Message}"); }
    }

    private void Render()
    {
        TranscriptBox.Text = string.Join(Environment.NewLine + Environment.NewLine,
            state.Turns.Select(turn => $"[{(turn.IsFinal ? "final" : "partial")}] {turn.Text}"));
        ReplyBox.Text = state.Current?.Text ?? "";
        GroundingText.Text = GroundingLabel(state.Current);
        SourcesText.Text = FormatSources(state.Current);
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

    private void UpdateOverlay() => overlay.ShowMeetingState(client?.IsReady == true, LatestQuestion(),
        state.Current?.Text ?? state.Pinned?.Text ?? "Listening for a reply…", GroundingLabel(state.Current),
        TopmostBox.IsChecked == true, KoreanText.Text, ReadingText.Text, IsDemo, PauseBox.IsChecked == true);

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

    private string LatestQuestion() => state.Turns.LastOrDefault()?.Text ?? "";

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
        UpdateOverlay();
        if (!overlay.IsVisible) overlay.Show();
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

    private void Practice_Click(object sender, RoutedEventArgs e)
    {
        if (HasActiveSession)
        {
            ErrorText.Text = "Stop the meeting microphone before opening practice.";
            return;
        }
        if (!identity.IsSignedIn && !IsDemo)
        {
            ErrorText.Text = "Sign in before starting a practice round.";
            return;
        }
        if (practice is { IsVisible: true }) { practice.Activate(); return; }
        practice = new PracticeWindow(settings, identity, api);
        practice.Closed += (_, _) => practice = null;
        practice.Show();
    }

    private async void MaterialsRefresh_Click(object sender, RoutedEventArgs e) => await RefreshMaterialsAsync();

    private async Task RefreshMaterialsAsync()
    {
        if (IsDemo)
        {
            MaterialsStatus.Text = "Personal materials are unavailable in offline demo mode.";
            return;
        }
        if (!identity.IsSignedIn && !IsDemo)
        {
            MaterialsStatus.Text = "Sign in before accessing your personal materials.";
            return;
        }
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
        overlay.Close();
        api.Dispose();
        Closing -= Window_Closing;
        Close();
    }
}
