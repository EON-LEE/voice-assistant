using System.Windows;

namespace VoiceAssistant.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (e.Args.Length != 0)
            {
                if (e.Args.Length != 2 || e.Args[0] != "--settings")
                    throw new ArgumentException("Usage: VoiceAssistant.Desktop.exe [--settings <nonsecret-settings.json>]");
                path = Path.GetFullPath(e.Args[1]);
            }
            var settings = ClientSettings.Load(path);
            settings.Validate();
            MainWindow = new MainWindow(settings);
            MainWindow.Show();
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or ArgumentException or InvalidOperationException)
        {
            MessageBox.Show($"The assistant could not start. No audio was captured.\n\n{ex.Message}",
                "Configuration error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
