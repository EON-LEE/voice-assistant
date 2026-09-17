using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using VoiceAssistant.Desktop.Audio;
using VoiceAssistant.Desktop.Protocol;

namespace VoiceAssistant.Desktop;

public partial class MainWindow : Window
{
    private readonly ClientSettings settings;
    private readonly ReplyState state = new();
    private MeetingClient? client;
    private CancellationTokenSource? lifetime;
    private Task? running;
    private bool closing;
    private bool initialized;
    private bool changingPause;

    public MainWindow(ClientSettings settings)
    {
        this.settings = settings;
        InitializeComponent();
        ModeBox.ItemsSource = Enum.GetValues<ConnectionMode>();
        ModeBox.SelectedItem = settings.Mode;
        initialized = true;
        UpdateMode();
    }

    private ConnectionMode Mode => (ConnectionMode)(ModeBox.SelectedItem ?? ConnectionMode.Demo);

    private void UpdateMode()
    {
        if (!initialized) return;
        bool demo = Mode == ConnectionMode.Demo;
        SyntheticBox.IsEnabled = Mode == ConnectionMode.Development;
        bool synthetic = demo || Mode == ConnectionMode.Development && SyntheticBox.IsChecked == true;
        DeviceBox.IsEnabled = !synthetic;
        RefreshButton.IsEnabled = !synthetic;
        ConsentBox.IsEnabled = !synthetic;
        if (!synthetic && DeviceBox.ItemsSource is null) RefreshEndpoints();
        ModeDescription.Text = Mode switch
        {
            ConnectionMode.Demo => "OFFLINE DEMO: synthetic audio and canned events in memory. No network, sign-in, or audio devices are opened. Press Suggest after the sample transcript appears.",
            ConnectionMode.Development => "DEVELOPMENT: unauthenticated loopback-only WebSocket. Synthetic silence can exercise a local fake API without capturing any device.",
            _ => "PRODUCTION: Entra public-client sign-in and encrypted WSS. No embedded cloud credentials. Capture starts only after the server is ready."
        };
        EndpointText.Text = demo ? "No backend connection in Demo mode." : $"Endpoint: {settings.Endpoint} (configured in appsettings.json)";
    }

    private void RefreshEndpoints()
    {
        try
        {
            DeviceBox.ItemsSource = LoopbackAudioSource.ListEndpoints();
            DeviceBox.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            ErrorText.Text = $"Could not enumerate output devices: {ex.Message}. Demo/synthetic mode remains available.";
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshEndpoints();
    private void ModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateMode();
    private void Synthetic_Changed(object sender, RoutedEventArgs e) => UpdateMode();
    private void Topmost_Changed(object sender, RoutedEventArgs e) => Topmost = ((CheckBox)sender).IsChecked == true;

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (lifetime is not null) return;
        bool synthetic = Mode == ConnectionMode.Demo || Mode == ConnectionMode.Development && SyntheticBox.IsChecked == true;
        if (!synthetic && ConsentBox.IsChecked != true)
        {
            ErrorText.Text = "Confirm that you have permission before starting output capture.";
            return;
        }
        if (!synthetic && DeviceBox.SelectedItem is not RenderEndpoint)
        {
            ErrorText.Text = "Select an active output device or explicitly choose Demo / synthetic Development mode.";
            return;
        }
        var sessionSettings = settings with { Mode = Mode };
        try { sessionSettings.Validate(); }
        catch (InvalidOperationException ex) { ErrorText.Text = ex.Message; return; }
        string? endpointId = (DeviceBox.SelectedItem as RenderEndpoint)?.Id;
        state.ResetSession();
        ErrorText.Text = "";
        lifetime = new CancellationTokenSource();
        client = new MeetingClient(Mode == ConnectionMode.Demo ? new DemoMeetingTransport() : new WebSocketMeetingTransport());
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        ModeBox.IsEnabled = DeviceBox.IsEnabled = RefreshButton.IsEnabled = SyntheticBox.IsEnabled = ConsentBox.IsEnabled = false;
        running = RunMeetingAsync(sessionSettings, synthetic, endpointId, lifetime.Token);
        await running;
    }

    private async Task RunMeetingAsync(ClientSettings sessionSettings, bool synthetic, string? endpointId, CancellationToken token)
    {
        try
        {
            await client!.RunAsync(sessionSettings,
                () => synthetic ? new SyntheticAudioSource() : new LoopbackAudioSource(endpointId!),
                message => { state.Apply(message); Render(); },
                status => { StatusText.Text = status; Render(); }, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            ErrorText.Text = $"Session ended: {ex.Message} No automatic reconnect. Check the configuration / connection, then Start again.";
        }
        finally
        {
            client = null;
            lifetime?.Dispose();
            lifetime = null;
            changingPause = true;
            PauseBox.IsChecked = false;
            changingPause = false;
            state.Pause(true);
            ConsentBox.IsChecked = false;
            StartButton.IsEnabled = ModeBox.IsEnabled = true;
            StopButton.IsEnabled = false;
            UpdateMode();
            StatusText.Text = "Stopped - no audio capture";
            Render();
        }
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        await StopSessionAsync();
    }

    private async Task StopSessionAsync()
    {
        var activeClient = client;
        if (activeClient is null) return;
        StopButton.IsEnabled = PauseBox.IsEnabled = RequestButton.IsEnabled = CancelButton.IsEnabled = false;
        try { await activeClient.StopAsync(); }
        catch (Exception ex) { ErrorText.Text = $"Stopped locally; session.stop could not be sent: {ex.Message}"; }
        finally { lifetime?.Cancel(); }
        if (running is not null) await running;
    }

    private async void Pause_Changed(object sender, RoutedEventArgs e)
    {
        if (changingPause || client is null || lifetime is null) return;
        bool paused = PauseBox.IsChecked == true;
        state.Pause(paused);
        try
        {
            await client.SetPausedAsync(paused, lifetime.Token);
            StatusText.Text = paused ? "Paused - dropping audio locally; suggestion cancelled" : "Connected - audio streaming";
        }
        catch (OperationCanceledException) when (lifetime?.IsCancellationRequested != false) { }
        catch (Exception ex) { ErrorText.Text = $"Pause command failed: {ex.Message}"; lifetime?.Cancel(); }
        Render();
    }

    private async void Request_Click(object sender, RoutedEventArgs e)
    {
        if (client is null || lifetime is null) return;
        try { state.BeginRequest(); ErrorText.Text = ""; await client.RequestResponseAsync(lifetime.Token); }
        catch (OperationCanceledException) when (lifetime?.IsCancellationRequested != false) { }
        catch (Exception ex) { ErrorText.Text = $"Reply request failed: {ex.Message}"; lifetime?.Cancel(); }
        Render();
    }

    private async void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (client is null || lifetime is null) return;
        state.CancelCurrent();
        try { await client.CancelResponseAsync(lifetime.Token); }
        catch (OperationCanceledException) when (lifetime?.IsCancellationRequested != false) { }
        catch (Exception ex) { ErrorText.Text = $"Cancel failed: {ex.Message}"; lifetime?.Cancel(); }
        Render();
    }

    private void Pin_Click(object sender, RoutedEventArgs e) { state.Pin(); Render(); }
    private void Unpin_Click(object sender, RoutedEventArgs e) { state.Unpin(); Render(); }

    private void Render()
    {
        TranscriptBox.Text = string.Join(Environment.NewLine + Environment.NewLine,
            state.Turns.Select(t => $"[{(t.IsFinal ? "final" : "partial")}] {t.Text}"));
        ReplyBox.Text = state.Current?.Text ?? "";
        ReplyStatus.Text = state.Current is null ? "" : state.Current.Complete ? "Complete" : "Streaming...";
        SourcesBox.Text = FormatSources(state.Current);
        PinnedBox.Text = state.Pinned?.Text ?? "";
        PinnedSourcesBox.Text = FormatSources(state.Pinned);
        PinButton.IsEnabled = !string.IsNullOrWhiteSpace(state.Current?.Text);
        PauseBox.IsEnabled = client?.IsReady == true;
        RequestButton.IsEnabled = client?.IsReady == true && PauseBox.IsChecked != true && state.Turns.Any(t => t.IsFinal);
        CancelButton.IsEnabled = client?.IsReady == true;
        if (state.Error is not null) ErrorText.Text = state.Error;
    }

    private static string FormatSources(ReplySnapshot? reply) => reply is null ? "" :
        string.Join(Environment.NewLine, reply.Sources.Select(s => $"{s.Title} | {s.Url} | updated: {s.UpdatedAt ?? "not supplied"}"));

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (closing || lifetime is null) return;
        e.Cancel = true;
        closing = true;
        await StopSessionAsync();
        Close();
    }
}
