using System.Diagnostics;
using System.Windows;

namespace VoiceAssistant.Desktop;

public partial class DeviceCodeWindow : Window
{
    private readonly Uri verificationUri;

    public DeviceCodeWindow(string url, string code)
    {
        InitializeComponent();
        if (!Uri.TryCreate(url, UriKind.Absolute, out verificationUri!) || verificationUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("Microsoft supplied an invalid device verification URL.");
        CodeBox.Text = code;
        UrlText.Text = verificationUri.AbsoluteUri;
    }

    private void Open_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(verificationUri.AbsoluteUri) { UseShellExecute = true });
}
