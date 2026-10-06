using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using VoiceAssistant.Desktop.Audio;
using VoiceAssistant.Desktop.Protocol;

namespace VoiceAssistant.Desktop;

public partial class PracticeWindow : Window
{
    private readonly ClientSettings settings;
    private readonly NativeIdentity identity;
    private readonly AuthenticatedApiClient api;
    private readonly List<PracticeHistory> history = [];
    private readonly List<PracticeTurn> turns = [];
    private readonly PracticeAnswerCapture answerCapture = new();
    private MeetingClient? client;
    private CancellationTokenSource? lifetime;
    private Task? running;
    private IDisposable? audioLease;
    private MemoryAudioPlayback? playback;
    private PracticeScenario? scenario;
    private string topic = "";
    private int maxTurns;
    private string question = "";
    private string answer => answerCapture.Answer;
    private string correctedEnglish = "";
    private Func<Task>? retry;
    private DateTimeOffset retryAt;
    private bool starting;
    private bool answering => answerCapture.IsActive;
    private bool speaking;
    private bool closing;
    private bool done;
    private bool busy;
    private bool testingMicrophone;

    private sealed record PracticeHistory(string Role, string Text);
    private sealed record PracticeScenario(string Kind, string Description, int Difficulty);
    private sealed record PracticeTurn(string Question, string Answer, string CorrectedEnglish);

    public PracticeWindow(ClientSettings settings, NativeIdentity identity, AuthenticatedApiClient api)
    {
        this.settings = settings;
        this.identity = identity;
        this.api = api;
        InitializeComponent();
        RefreshDevices();
        AuthText.Text = identity.IsSignedIn
            ? "Entra signed in. Captured speech is transcribed by the configured speech service."
            : "Sign in with Microsoft from the main window before starting practice.";
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshDevices();

    private async void MicTest_Click(object sender, RoutedEventArgs e)
    {
        if (testingMicrophone || ConsentBox.IsChecked != true || DeviceBox.SelectedItem is not CaptureEndpoint device)
        {
            ErrorText.Text = "Select a microphone and confirm practice consent before the local level check.";
            return;
        }
        testingMicrophone = true;
        MicTestButton.IsEnabled = StartButton.IsEnabled = false;
        try
        {
            await MicrophoneLevelCheck.RunLocallyAsync(device.Id, level => Dispatcher.BeginInvoke(() =>
            {
                LevelMeter.Value = level;
                LevelText.Text = $"Local input level: {(level < 0.015 ? "very low" : $"{level:P0}")}";
            }), CancellationToken.None);
            StatusText.Text = "Microphone test complete — no audio was sent.";
        }
        catch (Exception ex) { ErrorText.Text = $"Microphone test failed: {ex.Message}"; }
        finally
        {
            testingMicrophone = false;
            LevelMeter.Value = 0;
            LevelText.Text = "Input level: idle";
            MicTestButton.IsEnabled = StartButton.IsEnabled = true;
        }
    }

    private void RefreshDevices()
    {
        try
        {
            var selectedId = (DeviceBox.SelectedItem as CaptureEndpoint)?.Id;
            var devices = MicrophoneAudioSource.ListEndpoints();
            DeviceBox.ItemsSource = devices;
            DeviceBox.SelectedItem = devices.FirstOrDefault(device => device.Id == selectedId) ?? devices.FirstOrDefault();
            MicText.Text = devices.Count == 0 ? "No active microphone found" : "Practice microphone";
        }
        catch (Exception ex)
        {
            DeviceBox.ItemsSource = Array.Empty<CaptureEndpoint>();
            MicText.Text = $"Could not enumerate microphones: {ex.Message}";
        }
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (starting || lifetime is not null || testingMicrophone) return;
        if (!identity.IsSignedIn)
        {
            ErrorText.Text = "Sign in from the main window before starting practice.";
            return;
        }
        if (ConsentBox.IsChecked != true)
        {
            ErrorText.Text = "Confirm microphone consent before starting practice.";
            return;
        }
        if (DeviceBox.SelectedItem is not CaptureEndpoint selected)
        {
            ErrorText.Text = "Select an active physical microphone.";
            return;
        }
        var lease = AudioSessionCoordinator.TryAcquire();
        if (lease is null)
        {
            ErrorText.Text = "Another microphone session is active. Stop it before starting practice.";
            return;
        }
        try
        {
            var scenarioOption = (ComboBoxItem)ScenarioBox.SelectedItem;
            var difficultyOption = (ComboBoxItem)DifficultyBox.SelectedItem;
            var countOption = (ComboBoxItem)CountBox.SelectedItem;
            scenario = new((string)scenarioOption.Tag, DescriptionBox.Text.Trim(), int.Parse((string)difficultyOption.Tag));
            topic = TopicBox.Text.Trim();
            maxTurns = int.Parse((string)countOption.Tag);
            if (scenario.Kind == "custom" && string.IsNullOrWhiteSpace(scenario.Description))
                throw new InvalidOperationException("Enter a short description for the custom scenario.");
            if (topic.Length > 300 || scenario.Description.Length > 400)
                throw new InvalidOperationException("Shorten the topic or scenario description.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            lease.Dispose();
            ErrorText.Text = ex.Message;
            return;
        }

        audioLease = lease;
        history.Clear();
        turns.Clear();
        answerCapture.Reset();
        question = correctedEnglish = "";
        done = false;
        retry = null;
        starting = true;
        busy = true;
        lifetime = new CancellationTokenSource();
        var practiceSettings = settings with { TranscribeOnly = true, ResponseMode = "conversation" };
        client = new MeetingClient(new TicketedMeetingTransport(api));
        ConsentBox.IsEnabled = DeviceBox.IsEnabled = RefreshButton.IsEnabled = MicTestButton.IsEnabled = StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        ErrorText.Text = "";
        RoundSummaryText.Text = "";
        FeedbackText.Text = "";
        StatusText.Text = "Connecting and opening the selected microphone…";
        running = RunSessionAsync(practiceSettings, selected.Id, lifetime.Token);
        await running;
    }

    private async Task RunSessionAsync(ClientSettings practiceSettings, string endpointId, CancellationToken token)
    {
        try
        {
            await client!.RunAsync(practiceSettings, () =>
            {
                var source = new MicrophoneAudioSource(endpointId);
                source.LevelChanged += level => Dispatcher.BeginInvoke(() =>
                {
                    LevelMeter.Value = level;
                    LevelText.Text = $"Input level: {(level < 0.015 ? "very low" : $"{level:P0}")}";
                });
                return source;
            },
                message => Dispatcher.BeginInvoke(() => ApplyEvent(message)),
                status => Dispatcher.BeginInvoke(() =>
                {
                    if (status.StartsWith("Stopped", StringComparison.Ordinal))
                        StatusText.Text = status;
                }), token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { await Dispatcher.InvokeAsync(() => SetError(ex.Message)); }
        finally
        {
            await Dispatcher.InvokeAsync(() =>
            {
                client = null;
                lifetime?.Dispose();
                lifetime = null;
                audioLease?.Dispose();
                audioLease = null;
                LevelMeter.Value = 0;
                LevelText.Text = "Input level: idle";
                starting = busy = false;
                answerCapture.End();
                StopButton.IsEnabled = false;
                ConsentBox.IsEnabled = DeviceBox.IsEnabled = RefreshButton.IsEnabled = MicTestButton.IsEnabled = StartButton.IsEnabled = true;
                AnswerButton.IsEnabled = DoneButton.IsEnabled = SkipButton.IsEnabled = HintButton.IsEnabled = false;
                ListenButton.IsEnabled = false;
                if (!done)
                {
                    StatusText.Text = "Microphone session stopped. Start a new round to continue.";
                    ConsentBox.IsChecked = false;
                }
                running = null;
            });
        }
    }

    private void ApplyEvent(ServerEvent message)
    {
        if (message.Type == "session.ready")
        {
            starting = false;
            busy = false;
            StatusText.Text = "Microphone ready — choose Listen, then Answer when you are ready.";
            _ = LoadNextAsync();
            return;
        }
        if (message.Type is "transcript.partial" or "transcript.final")
        {
            var result = answerCapture.Add(message.TurnId!, message.Text!, message.Type == "transcript.final");
            if (result is PracticeTranscriptResult.Ignored or PracticeTranscriptResult.Duplicate) return;
            if (result is PracticeTranscriptResult.TooLong or PracticeTranscriptResult.Invalid)
            {
                SetError("This answer is too long or contains unsupported characters. Skip it or restart with a shorter answer.");
                return;
            }
            PartialText.Text = answerCapture.Partial.Length == 0 ? "" : $"Partial: {answerCapture.Partial}";
            AnswerText.Text = answer.Length == 0 ? "Listening for your answer…" : $"Final answer: {answer}";
            DoneButton.IsEnabled = answerCapture.CanSubmit && !busy;
        }
        if (message.Type == "error")
        {
            SetError($"{message.Code}: {message.Text}");
            if (!message.Retryable) _ = StopSessionAsync();
        }
    }

    private async Task LoadNextAsync()
    {
        if (client is null || lifetime is null || scenario is null || busy) return;
        await PerformAsync(async cancellation =>
        {
            using var response = await api.PostJsonAsync("/api/practice/turn", new
            {
                scenario,
                topic,
                useMaterials = MaterialsBox.IsChecked == true,
                maxTurns,
                history = history.Select(entry => new { role = entry.Role, text = entry.Text }).ToArray()
            }, cancellation);
            var root = response.RootElement;
            string text = RequiredString(root, "text", 800);
            bool final = root.GetProperty("done").GetBoolean();
            string grounding = RequiredString(root, "grounding", 32);
            var sources = ParseTitles(root.GetProperty("sources"));
            await Dispatcher.InvokeAsync(() =>
            {
                question = text;
                done = final;
                AnswerText.Text = "";
                PartialText.Text = "";
                FeedbackText.Text = "";
                PronunciationText.Text = "";
                HintText.Text = "";
                QuestionText.Text = text;
                GroundingText.Text = $"Question grounding: {GroundingDescription(grounding)}";
                SourceText.Text = sources.Count == 0 ? "" : $"References: {string.Join(", ", sources)}";
                answerCapture.Reset();
                StatusText.Text = final ? "Round complete — preparing your summary" : "Listen, then choose Answer to speak.";
                AnswerButton.IsEnabled = !final && client?.IsReady == true;
                ListenButton.IsEnabled = !final;
                HintButton.IsEnabled = !final;
                SkipButton.IsEnabled = !final;
                NextButton.IsEnabled = false;
                DoneButton.IsEnabled = false;
            });
            if (final) await LoadSummaryAsync(cancellation);
            else
            {
                history.Add(new("partner", text));
                _ = EnrichQuestionAsync(text, cancellation);
            }
        }, LoadNextAsync);
    }

    private async Task LoadSummaryAsync(CancellationToken cancellation)
    {
        if (scenario is null || turns.Count == 0) return;
        using var response = await api.PostJsonAsync("/api/practice/summary", new
        {
            scenario,
            topic,
            turns = turns.Select(turn => new
            {
                question = turn.Question,
                answer = turn.Answer,
                correctedEnglish = turn.CorrectedEnglish
            }).ToArray()
        }, cancellation);
        var root = response.RootElement;
        string headline = RequiredString(root, "headlineKo", 800);
        var strengths = ParseStringArray(root.GetProperty("strengthsKo"), 3, 120);
        var improvements = ParseStringArray(root.GetProperty("improveKo"), 3, 120);
        var phrases = root.GetProperty("phrases");
        if (phrases.ValueKind != JsonValueKind.Array || phrases.GetArrayLength() > 8)
            throw new InvalidDataException("The coach returned an invalid practice summary.");
        var phraseLines = phrases.EnumerateArray().Select(item =>
            $"{RequiredString(item, "en", 800)}\n{RequiredString(item, "ko", 400)}").ToArray();
        await Dispatcher.InvokeAsync(() =>
        {
            RoundSummaryText.Text = $"{headline}\n\nStrengths · 잘한 점\n{string.Join("\n", strengths)}\n\nNext time · 다음 연습\n{string.Join("\n", improvements)}\n\nReusable phrases · 다시 쓸 표현\n{string.Join("\n\n", phraseLines)}";
            StatusText.Text = "Practice round complete. No microphone is active.";
            done = true;
            ConsentBox.IsChecked = false;
            _ = StopSessionAsync();
        });
    }

    private async Task EnrichQuestionAsync(string text, CancellationToken cancellation)
    {
        try
        {
            using var response = await api.PostJsonAsync("/api/assist/enrich", new { kind = "question", text }, cancellation);
            var korean = RequiredString(response.RootElement, "korean", 400);
            await Dispatcher.InvokeAsync(() => QuestionKoText.Text = korean);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) { await Dispatcher.InvokeAsync(() => SetError($"Korean help unavailable: {ex.Message}")); }
    }

    private async Task PerformAsync(Func<CancellationToken, Task> operation, Func<Task> retryOperation)
    {
        if (lifetime is null || busy) return;
        if (DateTimeOffset.UtcNow < retryAt)
        {
            SetError($"Please wait {Math.Ceiling((retryAt - DateTimeOffset.UtcNow).TotalSeconds)} seconds before retrying.");
            retry = retryOperation;
            return;
        }
        busy = true;
        retry = retryOperation;
        RetryButton.Visibility = Visibility.Collapsed;
        ErrorText.Text = "";
        try { await operation(lifetime.Token); retry = null; }
        catch (OperationCanceledException) when (lifetime?.IsCancellationRequested == true) { }
        catch (Exception ex)
        {
            if (ex is ApiRequestException apiError && apiError.RetryAfter is { } after)
                retryAt = DateTimeOffset.UtcNow.Add(after);
            await Dispatcher.InvokeAsync(() => SetError(ex.Message));
        }
        finally
        {
            busy = false;
            if (!done && client is not null)
            {
                AnswerButton.IsEnabled = !answering && !speaking && question.Length > 0;
                DoneButton.IsEnabled = answering && answer.Length > 0;
                SkipButton.IsEnabled = question.Length > 0;
                HintButton.IsEnabled = question.Length > 0;
                NextButton.IsEnabled = !answering && FeedbackText.Text.Length > 0;
            }
        }
    }

    private async void Answer_Click(object sender, RoutedEventArgs e)
    {
        if (client is null || !client.IsReady || question.Length == 0 || speaking) return;
        playback?.Dispose();
        playback = null;
        try
        {
            await client.SetPausedAsync(false, lifetime!.Token);
            answerCapture.Begin();
            PartialText.Text = "";
            AnswerText.Text = "Listening for your answer…";
            StatusText.Text = "Listening to your answer — 말해 보세요";
            AnswerButton.IsEnabled = false;
            DoneButton.IsEnabled = false;
            SkipButton.IsEnabled = true;
        }
        catch (Exception ex) { SetError($"Could not start answer recognition: {ex.Message}"); }
    }

    private async void Done_Click(object sender, RoutedEventArgs e)
    {
        if (!answerCapture.CanSubmit || client is null) return;
        await client.SetPausedAsync(true, lifetime!.Token);
        string submitted = answerCapture.Finish();
        PartialText.Text = "";
        await GetFeedbackAsync(submitted);
    }

    private async Task GetFeedbackAsync(string submitted)
    {
        if (scenario is null) return;
        await PerformAsync(async cancellation =>
        {
            using var response = await api.PostJsonAsync("/api/practice/feedback", new
            {
                scenario,
                question,
                answer = submitted
            }, cancellation);
            var root = response.RootElement;
            string corrected = RequiredString(root, "correctedEnglish", 800);
            string easier = RequiredString(root, "easierEnglish", 800);
            string korean = RequiredString(root, "feedbackKo", 800);
            int clarity = root.GetProperty("clarity").GetInt32();
            var points = root.GetProperty("points");
            if (clarity is < 1 or > 5 || points.ValueKind != JsonValueKind.Array || points.GetArrayLength() > 3)
                throw new InvalidDataException("The coach returned invalid feedback.");
            string pointText = string.Join("\n", points.EnumerateArray().Select(point =>
                $"{RequiredString(point, "tag", 40)}: {RequiredString(point, "ko", 120)}"));
            correctedEnglish = corrected;
            history.Add(new("user", submitted));
            turns.Add(new(question, submitted, corrected));
            await Dispatcher.InvokeAsync(() =>
            {
                FeedbackText.Text = $"Feedback · 피드백\n{korean}\n\nCorrected English: {corrected}\nEasier English: {easier}\nClarity of recognized words: {clarity}/5\n{pointText}\n\nBased on recognized text only; this does not assess pronunciation or accent.";
                StatusText.Text = "Review your feedback, then continue when ready.";
                AnswerText.Text = submitted.Length == 0 ? "Skipped without an answer." : $"Your final answer: {submitted}";
                NextButton.IsEnabled = true;
                DoneButton.IsEnabled = false;
                SkipButton.IsEnabled = false;
                AnswerButton.IsEnabled = false;
            });
            _ = EnrichReadingAsync(corrected, cancellation);
        }, () => GetFeedbackAsync(submitted));
    }

    private async Task EnrichReadingAsync(string text, CancellationToken cancellation)
    {
        try
        {
            using var response = await api.PostJsonAsync("/api/assist/enrich", new { kind = "reply", text }, cancellation);
            var pronunciation = response.RootElement.GetProperty("pronunciation");
            if (pronunciation.ValueKind != JsonValueKind.Array) return;
            var reading = string.Join(" · ", pronunciation.EnumerateArray().Select(item =>
                $"{RequiredString(item, "en", 600)} ({RequiredString(item, "ko", 80)})"));
            await Dispatcher.InvokeAsync(() => PronunciationText.Text = $"Pronunciation guide (approximate): {reading}");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) { await Dispatcher.InvokeAsync(() => SetError($"Pronunciation guide unavailable: {ex.Message}")); }
    }

    private async void Skip_Click(object sender, RoutedEventArgs e)
    {
        if (client is null || question.Length == 0 || busy) return;
        try { await client.SetPausedAsync(true, lifetime!.Token); }
        catch (Exception ex) { SetError($"Could not pause microphone: {ex.Message}"); return; }
        answerCapture.End();
        await GetFeedbackAsync("");
    }

    private async void Next_Click(object sender, RoutedEventArgs e) => await LoadNextAsync();

    private async void Hint_Click(object sender, RoutedEventArgs e) => await RequestHintAsync();

    private async Task RequestHintAsync()
    {
        if (scenario is null || question.Length == 0) return;
        await PerformAsync(async cancellation =>
        {
            using var response = await api.PostJsonAsync("/api/practice/suggest", new
            {
                scenario,
                topic,
                useMaterials = MaterialsBox.IsChecked == true,
                question
            }, cancellation);
            string text = RequiredString(response.RootElement, "text", 800);
            string grounding = RequiredString(response.RootElement, "grounding", 32);
            var sources = ParseTitles(response.RootElement.GetProperty("sources"));
            await Dispatcher.InvokeAsync(() => HintText.Text =
                $"Optional suggestion: {text}\nGrounding: {GroundingDescription(grounding)}{(sources.Count == 0 ? "" : $"\nReferences: {string.Join(", ", sources)}")}");
        }, RequestHintAsync);
    }

    private async void Listen_Click(object sender, RoutedEventArgs e)
    {
        if (question.Length == 0 || client is null || lifetime is null) return;
        speaking = true;
        AnswerButton.IsEnabled = false;
        ListenButton.IsEnabled = false;
        try
        {
            await client.SetPausedAsync(true, lifetime.Token);
            byte[] audio = await api.PostAudioAsync("/api/assist/speak",
                new { text = question, voice = "partner", rate = "normal" }, lifetime.Token);
            playback?.Dispose();
            playback = new MemoryAudioPlayback(audio);
            await playback.PlayAsync(lifetime.Token);
            StatusText.Text = "Question audio complete — choose Answer by voice when ready.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SetError($"Question audio unavailable: {ex.Message}"); }
        finally
        {
            playback?.Dispose();
            playback = null;
            speaking = false;
            AnswerButton.IsEnabled = !done && question.Length > 0 && !answering;
            ListenButton.IsEnabled = !done && question.Length > 0;
        }
    }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (retry is null) return;
        if (DateTimeOffset.UtcNow < retryAt)
        {
            SetError($"Please wait {Math.Ceiling((retryAt - DateTimeOffset.UtcNow).TotalSeconds)} seconds before retrying.");
            return;
        }
        var current = retry;
        await current();
    }

    private void SetError(string message)
    {
        ErrorText.Text = message;
        RetryButton.Visibility = retry is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void Stop_Click(object sender, RoutedEventArgs e) => await StopSessionAsync();

    private async Task StopSessionAsync()
    {
        var active = client;
        if (active is null) return;
        playback?.Dispose();
        playback = null;
        try { await active.StopAsync(); }
        catch (Exception ex) { SetError($"Microphone stopped locally: {ex.Message}"); }
        finally { lifetime?.Cancel(); }
        if (running is not null) await running;
    }

    private static string RequiredString(JsonElement root, string property, int maxLength)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String ||
            value.GetString() is not { } text || text.Length > maxLength)
            throw new InvalidDataException($"The coach returned invalid {property} text.");
        return text;
    }

    private static IReadOnlyList<string> ParseTitles(JsonElement sources)
    {
        if (sources.ValueKind != JsonValueKind.Array || sources.GetArrayLength() > 5)
            throw new InvalidDataException("The coach returned an invalid source list.");
        return sources.EnumerateArray().Select(source => RequiredString(source, "title", 200)).ToArray();
    }

    private static IReadOnlyList<string> ParseStringArray(JsonElement values, int maxItems, int maxLength)
    {
        if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() > maxItems)
            throw new InvalidDataException("The coach returned an invalid summary list.");
        return values.EnumerateArray().Select(value =>
        {
            if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text || text.Length > maxLength)
                throw new InvalidDataException("The coach returned invalid summary text.");
            return text;
        }).ToArray();
    }

    private static string GroundingDescription(string value) => value switch
    {
        "grounded" => "personal materials matched",
        "no_matches" => "no relevant material matched",
        "unavailable" => "materials unavailable; no unfiltered fallback",
        "disabled" => "materials not requested",
        _ => "grounding status unknown"
    };

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (closing) return;
        if (client is not null)
        {
            e.Cancel = true;
            closing = true;
            Dispatcher.BeginInvoke(new Action(async () => await FinishCloseAsync()));
        }
    }

    private async Task FinishCloseAsync()
    {
        await StopSessionAsync();
        Closing -= Window_Closing;
        Close();
    }
}
