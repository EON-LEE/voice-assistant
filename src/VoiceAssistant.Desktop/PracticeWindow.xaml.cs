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
    private readonly SemaphoreSlim stopGate = new(1, 1);
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
    private bool done;
    private bool busy;
    private bool testingMicrophone;
    private Task? closeTask;
    private bool feedbackReady;
    private bool demoPreviewRunning;
    private bool demoAnswerReady;
    private bool shuttingDown;
    private int demoQuestionIndex;
    private int apiOperations;
    private TaskCompletionSource? apiOperationsIdle;
    private Task? speakingTask;
    private CancellationTokenSource? speakingLifetime;
    private CancellationTokenSource? microphoneTestLifetime;
    private Task? microphoneTestTask;

    private bool IsDemo => settings.Mode == ConnectionMode.Demo;
    private static readonly (string Question, string Answer)[] DemoQuestions =
    [
        ("Could you briefly introduce the project update?",
            "We have finished the first phase, and we expect to share the results on Friday."),
        ("What is the main risk, and how are you reducing it?",
            "The main risk is a short delay, so we are checking progress twice a week.")
    ];

    private sealed record PracticeHistory(string Role, string Text);
    private sealed record PracticeScenario(string Kind, string Description, int Difficulty);
    private sealed record PracticeTurn(string Question, string Answer, string CorrectedEnglish);

    public PracticeWindow(ClientSettings settings, NativeIdentity identity, AuthenticatedApiClient api)
    {
        this.settings = settings;
        this.identity = identity;
        this.api = api;
        InitializeComponent();
        if (IsDemo)
        {
            Title = "Practice preview — offline demo";
            AuthText.Text = "OFFLINE DEMO PREVIEW — canned questions and sample feedback only. No sign-in, network, Search, TTS, or microphone.";
            MicText.Text = "No microphone used in offline demo";
            DeviceBox.IsEnabled = ConsentBox.IsEnabled = MicTestButton.IsEnabled = RefreshButton.IsEnabled = MaterialsBox.IsEnabled = false;
            StartButton.Content = "Start offline practice preview";
            ListenButton.Content = "Live TTS only";
            AnswerButton.Content = "Show sample answer";
            HintButton.Content = "Live suggestions only";
            DeviceBox.ItemsSource = Array.Empty<CaptureEndpoint>();
            StatusText.Text = "OFFLINE DEMO — no microphone, sign-in, or network.";
        }

        else
        {
            RefreshDevices();
            AuthText.Text = identity.IsSignedIn
                ? "Entra signed in. Captured speech is transcribed by the configured speech service."
                : "Sign in with Microsoft from the main window before starting practice.";
        }
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
                LevelText.Text = $"Local input level: {(level < 0.015 ? "very low" : $"{level:P0}")}";
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
        if (IsDemo) { StartDemoPreview(); return; }
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
        feedbackReady = false;
        demoPreviewRunning = false;
        demoAnswerReady = false;
        retry = null;
        starting = true;
        busy = true;
        UpdatePracticeActions();
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
                lifetime?.Cancel();
                speakingLifetime?.Cancel();
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
                UpdatePracticeActions();
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
            UpdatePracticeActions();
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
                QuestionKoText.Text = "";
                done = final;
                feedbackReady = false;
                demoAnswerReady = false;
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
                UpdatePracticeActions();
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
        if (shuttingDown || cancellation.IsCancellationRequested) return;
        BeginApiOperation();
        try
        {
            using var response = await api.PostJsonAsync("/api/assist/enrich", new { kind = "question", text }, cancellation);
            var korean = RequiredString(response.RootElement, "korean", 400);
            await Dispatcher.InvokeAsync(() => QuestionKoText.Text = korean);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) { await Dispatcher.InvokeAsync(() => SetError($"Korean help unavailable: {ex.Message}")); }
        finally { EndApiOperation(); }
    }

    private async Task PerformAsync(Func<CancellationToken, Task> operation, Func<Task> retryOperation)
    {
        if (shuttingDown || lifetime is null || busy) return;
        if (DateTimeOffset.UtcNow < retryAt)
        {
            SetError($"Please wait {Math.Ceiling((retryAt - DateTimeOffset.UtcNow).TotalSeconds)} seconds before retrying.");
            retry = retryOperation;
            return;
        }
        BeginApiOperation();
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
            UpdatePracticeActions();
            EndApiOperation();
        }
    }

    private async void Answer_Click(object sender, RoutedEventArgs e)
    {
        if (IsDemo)
        {
            if (!demoPreviewRunning || question.Length == 0 || feedbackReady) return;
            demoAnswerReady = true;
            AnswerText.Text = $"Sample answer (not recognized speech): {DemoAnswer()}";
            StatusText.Text = "OFFLINE DEMO — sample text only; no microphone was opened.";
            UpdatePracticeActions();
            return;
        }
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
            UpdatePracticeActions();
        }
        catch (Exception ex) { SetError($"Could not start answer recognition: {ex.Message}"); }
    }

    private async void Done_Click(object sender, RoutedEventArgs e)
    {
        if (IsDemo)
        {
            if (!demoPreviewRunning || !demoAnswerReady || feedbackReady) return;
            feedbackReady = true;
            FeedbackText.Text = "DEMO FEEDBACK · Example only\nClear, direct answer. Try adding one concrete detail and a polite closing.";
            PronunciationText.Text = "Approximate reading guide · Demo content only";
            StatusText.Text = "Offline sample feedback ready — continue to the next canned question.";
            UpdatePracticeActions();
            return;
        }
        if (!answerCapture.CanSubmit || client is null) return;
        try
        {
            await client.SetPausedAsync(true, lifetime!.Token);
            string submitted = answerCapture.Finish();
            PartialText.Text = "";
            UpdatePracticeActions();
            await GetFeedbackAsync(submitted);
        }
        catch (OperationCanceledException) when (lifetime?.IsCancellationRequested == true) { }
        catch (Exception ex)
        {
            SetError($"Could not finish answer capture: {ex.Message}");
            await StopSessionAsync();
        }
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
                feedbackReady = true;
                FeedbackText.Text = $"Feedback · 피드백\n{korean}\n\nCorrected English: {corrected}\nEasier English: {easier}\nClarity of recognized words: {clarity}/5\n{pointText}\n\nBased on recognized text only; this does not assess pronunciation or accent.";
                StatusText.Text = "Review your feedback, then continue when ready.";
                AnswerText.Text = submitted.Length == 0 ? "Skipped without an answer." : $"Your final answer: {submitted}";
                UpdatePracticeActions();
            });
            _ = EnrichReadingAsync(corrected, cancellation);
        }, () => GetFeedbackAsync(submitted));
    }

    private async Task EnrichReadingAsync(string text, CancellationToken cancellation)
    {
        if (shuttingDown || cancellation.IsCancellationRequested) return;
        BeginApiOperation();
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
        finally { EndApiOperation(); }
    }

    private async void Skip_Click(object sender, RoutedEventArgs e)
    {
        if (IsDemo)
        {
            if (!demoPreviewRunning || feedbackReady) return;
            demoAnswerReady = false;
            feedbackReady = true;
            FeedbackText.Text = "OFFLINE DEMO — skipped without microphone input.";
            StatusText.Text = "Skip preview ready — continue to the next canned question.";
            UpdatePracticeActions();
            return;
        }
        if (client is null || question.Length == 0 || busy) return;
        try { await client.SetPausedAsync(true, lifetime!.Token); }
        catch (Exception ex) { SetError($"Could not pause microphone: {ex.Message}"); return; }
        answerCapture.End();
        await GetFeedbackAsync("");
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        if (IsDemo) { AdvanceDemoPreview(); return; }
        await LoadNextAsync();
    }

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
        if (IsDemo || shuttingDown || speaking) return;
        if (question.Length == 0 || client is null || lifetime is null) return;
        speaking = true;
        UpdatePracticeActions();
        speakingLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        speakingTask = PlayQuestionAsync(speakingLifetime.Token);
        await speakingTask;
    }

    private async Task PlayQuestionAsync(CancellationToken token)
    {
        try
        {
            if (client is null || lifetime is null) return;
            await client.SetPausedAsync(true, lifetime.Token);
            byte[] audio = await api.PostAudioAsync("/api/assist/speak",
                new { text = question, voice = "partner", rate = "normal" }, token);
            playback?.Dispose();
            playback = new MemoryAudioPlayback(audio);
            await playback.PlayAsync(token);
            StatusText.Text = "Question audio complete — choose Answer by voice when ready.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { SetError($"Question audio unavailable: {ex.Message}"); }
        finally
        {
            playback?.Dispose();
            playback = null;
            speaking = false;
            speakingLifetime?.Dispose();
            speakingLifetime = null;
            speakingTask = null;
            UpdatePracticeActions();
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

    private void BeginApiOperation()
    {
        if (apiOperations++ == 0)
            apiOperationsIdle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private void EndApiOperation()
    {
        if (--apiOperations == 0)
        {
            apiOperationsIdle?.TrySetResult();
            apiOperationsIdle = null;
        }
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (IsDemo) { StopDemoPreview(); return; }
        await StopSessionAsync();
    }

    private void StartDemoPreview()
    {
        if (demoPreviewRunning) return;
        demoPreviewRunning = true;
        demoQuestionIndex = 0;
        done = feedbackReady = demoAnswerReady = false;
        RoundSummaryText.Text = "";
        StartButton.IsEnabled = false;
        StopButton.Content = "Stop preview";
        StopButton.IsEnabled = true;
        StatusText.Text = "OFFLINE DEMO — canned content only; no microphone, sign-in, or network.";
        ShowDemoQuestion();
    }

    private void ShowDemoQuestion()
    {
        question = DemoQuestions[demoQuestionIndex].Question;
        QuestionText.Text = question;
        QuestionKoText.Text = "질문 예시 · This Korean note is fixed demo text, not a live translation.";
        GroundingText.Text = "OFFLINE DEMO — canned question; personal materials and Azure AI Search are not used.";
        SourceText.Text = "Sources: none (offline demo).";
        AnswerText.Text = "No microphone input. Choose “Show sample answer” to preview finalized-answer controls.";
        FeedbackText.Text = "";
        PronunciationText.Text = "";
        PartialText.Text = "";
        HintText.Text = "";
        demoAnswerReady = false;
        feedbackReady = false;
        UpdatePracticeActions();
    }

    private string DemoAnswer() => DemoQuestions[demoQuestionIndex].Answer;

    private void AdvanceDemoPreview()
    {
        if (!demoPreviewRunning || !feedbackReady) return;
        demoQuestionIndex++;
        if (demoQuestionIndex < DemoQuestions.Length)
        {
            ShowDemoQuestion();
            StatusText.Text = "OFFLINE DEMO — second canned question; no microphone or network.";
            return;
        }
        demoPreviewRunning = false;
        done = true;
        question = "";
        RoundSummaryText.Text = "OFFLINE DEMO SUMMARY · Example only\nYou practiced concise updates and explaining a project risk. Add one specific next step in a live practice round.";
        StatusText.Text = "OFFLINE DEMO COMPLETE — no microphone, sign-in, or network was used.";
        StartButton.Content = "Restart offline preview";
        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
        UpdatePracticeActions();
    }

    private void StopDemoPreview()
    {
        demoPreviewRunning = false;
        done = feedbackReady = demoAnswerReady = false;
        question = "";
        QuestionText.Text = "";
        QuestionKoText.Text = "";
        GroundingText.Text = "OFFLINE DEMO — no personal materials or Search.";
        SourceText.Text = "";
        AnswerText.Text = "";
        FeedbackText.Text = "";
        PronunciationText.Text = "";
        RoundSummaryText.Text = "";
        StatusText.Text = "OFFLINE DEMO — no microphone, sign-in, or network.";
        StartButton.Content = "Start offline practice preview";
        StartButton.IsEnabled = true;
        StopButton.Content = "Stop round";
        StopButton.IsEnabled = false;
        UpdatePracticeActions();
    }

    private void UpdatePracticeActions()
    {
        bool active = IsDemo ? demoPreviewRunning : client?.IsReady == true;
        bool canAnswer = active && !done && !busy && !speaking && !feedbackReady && question.Length > 0;
        AnswerButton.IsEnabled = canAnswer && (IsDemo ? !demoAnswerReady : !answering);
        DoneButton.IsEnabled = active && !done && !busy && !feedbackReady &&
            (IsDemo ? demoAnswerReady : answering && answerCapture.CanSubmit);
        SkipButton.IsEnabled = canAnswer && (IsDemo ? !demoAnswerReady : true);
        HintButton.IsEnabled = !IsDemo && canAnswer;
        ListenButton.IsEnabled = !IsDemo && canAnswer;
        NextButton.IsEnabled = active && !done && !busy && feedbackReady;
    }

    private async Task StopSessionAsync()
    {
        await stopGate.WaitAsync();
        try
        {
            speakingLifetime?.Cancel();
            var active = client;
            if (active is null)
            {
                if (speakingTask is not null) await speakingTask;
                return;
            }
            playback?.Dispose();
            playback = null;
            try { await active.StopAsync(); }
            catch (Exception ex) { SetError($"Microphone stopped locally: {ex.Message}"); }
            finally { lifetime?.Cancel(); }
            if (running is not null) await running;
            if (speakingTask is not null) await speakingTask;
        }
        finally { stopGate.Release(); }
    }

    public async Task StopForOwnerAsync()
    {
        shuttingDown = true;
        microphoneTestLifetime?.Cancel();
        if (microphoneTestTask is not null) await microphoneTestTask;
        if (IsDemo) StopDemoPreview();
        else await StopSessionAsync();
        if (speakingTask is not null) await speakingTask;
        if (apiOperations > 0) await apiOperationsIdle!.Task;
    }

    public Task CloseForOwnerAsync()
    {
        if (closeTask is null) closeTask = FinishCloseAsync();
        return closeTask;
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
        if (closeTask is not null)
        {
            e.Cancel = true;
            return;
        }
        if (client is not null || testingMicrophone)
        {
            e.Cancel = true;
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                closeTask ??= FinishCloseAsync();
                await closeTask;
            }));
        }
    }

    private async Task FinishCloseAsync()
    {
        await StopForOwnerAsync();
        Closing -= Window_Closing;
        Close();
    }
}
