using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;

namespace VoiceAssistant.WebOverlay;

/// <summary>
/// Transparent, always-on-top window that hosts the deployed web overlay (live.html) in Edge WebView2.
/// The page itself is unchanged; this host only makes the window background see-through.
/// </summary>
public partial class MainWindow : Window
{
    private const string DefaultUrl = "https://voice-web.gentlesky-d6ba12c8.koreacentral.azurecontainerapps.io/live.html";
    private static readonly double[] Opacities = [0.92, 0.8, 0.65, 0.5];
    private readonly Uri pageUrl;
    private readonly bool fakeMicrophone;
    private readonly string? fakeAudioFile;
    private readonly string dataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoiceAssistant", "WebOverlay");
    private ShellSettings settings;

    public MainWindow()
    {
        InitializeComponent();
        var args = Environment.GetCommandLineArgs();
        var urlIndex = Array.IndexOf(args, "--url");
        pageUrl = ValidateUrl(urlIndex >= 0 && urlIndex + 1 < args.Length ? args[urlIndex + 1] : DefaultUrl);
        fakeMicrophone = args.Contains("--fake-mic");
        var audioIndex = Array.IndexOf(args, "--audio-file");
        if (audioIndex >= 0 && audioIndex + 1 < args.Length && fakeMicrophone)
        {
            var path = Path.GetFullPath(args[audioIndex + 1]);
            if (!File.Exists(path) || !path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("--audio-file must be an existing .wav file.");
            fakeAudioFile = path;
        }
        settings = ShellSettings.Load(dataFolder);
        if (settings.Width is >= 360 and <= 1200) Width = settings.Width;
        if (settings.Height is >= 400) Height = Math.Min(settings.Height, SystemParameters.WorkArea.Height);
        else Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        if (settings.Left is { } left && settings.Top is { } top && IsOnScreen(left, top)) { Left = left; Top = top; }
        else { Left = SystemParameters.WorkArea.Right - Width - 24; Top = SystemParameters.WorkArea.Top + 24; }
        Loaded += async (_, _) => await InitializeWebAsync();
        Closing += (_, _) => SaveSettings();
    }

    /// <summary>Only HTTPS pages, or plain HTTP on loopback for local development, may be hosted.</summary>
    internal static Uri ValidateUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo) ||
            !(uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
            throw new ArgumentException("The overlay URL must be HTTPS, or HTTP on localhost for development.");
        return uri;
    }

    private async Task InitializeWebAsync()
    {
        try
        {
            Directory.CreateDirectory(dataFolder);
            var options = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = fakeMicrophone
                    ? "--use-fake-device-for-media-stream --use-fake-ui-for-media-stream --remote-debugging-port=9333" +
                      (fakeAudioFile is null ? "" : $" \"--use-file-for-fake-audio-capture={fakeAudioFile}%noloop\"") : ""
            };
            // A fixed profile folder keeps the Microsoft sign-in cache and microphone choice between runs.
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(dataFolder, "Profile"), options);
            await Web.EnsureCoreWebView2Async(environment);
            var core = Web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = fakeMicrophone;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.PermissionRequested += OnPermissionRequested;
            core.WebMessageReceived += OnWebMessage;
            core.NavigationStarting += (_, e) =>
            {
                // The overlay window stays on the meeting page; other sites (sign-in) open in their own popup.
                if (!SameOrigin(new Uri(e.Uri))) { e.Cancel = true; OpenExternal(e.Uri); }
            };
            core.NavigationCompleted += (_, e) =>
            {
                Loading.Visibility = Visibility.Collapsed;
                if (!e.IsSuccess) ShowLoadError(e.WebErrorStatus.ToString());
            };
            await core.AddScriptToExecuteOnDocumentCreatedAsync(ShellScript.Build(pageUrl.GetLeftPart(UriPartial.Authority),
                Opacities[Math.Clamp(settings.OpacityIndex, 0, Opacities.Length - 1)]));
            core.Navigate(pageUrl.AbsoluteUri);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ShowLoadError("Microsoft Edge WebView2 Runtime이 없습니다. Edge를 업데이트하거나 WebView2 Runtime을 설치하세요.");
        }
        catch (Exception ex) { ShowLoadError(ex.Message); }
    }

    private bool SameOrigin(Uri uri) =>
        Uri.Compare(uri, pageUrl, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;

    private void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        // Microphone only, and only for the meeting page itself.
        e.State = e.PermissionKind == CoreWebView2PermissionKind.Microphone && Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && SameOrigin(uri)
            ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!Uri.TryCreate(e.Source, UriKind.Absolute, out var source) || !SameOrigin(source)) return;
        string message;
        try { message = e.TryGetWebMessageAsString(); } catch (ArgumentException) { return; }
        switch (message)
        {
            case "drag":
                DragFromPage();
                break;
            case "opacity":
                settings = settings with { OpacityIndex = (settings.OpacityIndex + 1) % Opacities.Length };
                _ = Web.CoreWebView2.ExecuteScriptAsync(
                    $"window.__liveShell?.setOpacity({Opacities[settings.OpacityIndex].ToString(System.Globalization.CultureInfo.InvariantCulture)})");
                SaveSettings();
                break;
            case "minimize":
                WindowState = WindowState.Minimized;
                break;
            case "exit":
                Close();
                break;
        }
    }

    private void DragFromPage()
    {
        // Move the borderless window natively, exactly like dragging a title bar.
        var handle = new WindowInteropHelper(this).Handle;
        ReleaseCapture();
        SendMessage(handle, 0xA1 /* WM_NCLBUTTONDOWN */, (IntPtr)2 /* HTCAPTION */, IntPtr.Zero);
    }

    private void ShowLoadError(string detail)
    {
        Loading.Visibility = Visibility.Visible;
        Loading.Text = $"Live Coach 화면을 불러오지 못했습니다.\n{detail}\n인터넷 연결을 확인한 뒤 다시 실행하세요.";
        Loading.TextAlignment = TextAlignment.Center;
        Loading.TextWrapping = TextWrapping.Wrap;
        Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(230, 20, 29, 43));
    }

    private static void OpenExternal(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.Scheme == Uri.UriSchemeHttps)
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(parsed.AbsoluteUri) { UseShellExecute = true });
    }

    private static bool IsOnScreen(double left, double top) =>
        left >= SystemParameters.VirtualScreenLeft - 100 && top >= SystemParameters.VirtualScreenTop - 20 &&
        left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100 &&
        top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100;

    private void SaveSettings()
    {
        if (WindowState != WindowState.Normal) return;
        settings = settings with { Left = Left, Top = Top, Width = Width, Height = Height };
        settings.Save(dataFolder);
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}

internal sealed record ShellSettings(double? Left = null, double? Top = null, double Width = 550, double Height = 860, int OpacityIndex = 1)
{
    private const string FileName = "overlay-settings.json";
    public static ShellSettings Load(string folder)
    {
        try { return JsonSerializer.Deserialize<ShellSettings>(File.ReadAllText(Path.Combine(folder, FileName))) ?? new(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    public void Save(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, FileName), JsonSerializer.Serialize(this));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
