using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace VoiceAssistant.Desktop;

public partial class OverlayWindow : Window
{
    public OverlayWindow()
    {
        InitializeComponent();
    }

    public void ShowMeetingState(bool capturing, string question, string answer, string grounding, bool topmost,
        string korean = "", string reading = "", bool demo = false)
    {
        CaptureStatus.Text = demo && capturing ? "DEMO · NO MICROPHONE CAPTURE"
            : capturing ? "● ROOM MICROPHONE ACTIVE" : "MICROPHONE OFF";
        CaptureStatus.Foreground = capturing ? Brushes.LightGreen : Brushes.LightGray;
        QuestionText.Text = string.IsNullOrWhiteSpace(question) ? "Waiting for a room question…" : question;
        AnswerText.Text = string.IsNullOrWhiteSpace(answer) ? "Listening for a reply…" : answer;
        GroundingText.Text = grounding;
        KoreanText.Text = korean;
        ReadingText.Text = reading;
        Topmost = topmost;
    }

    private void DragPanel(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void Grow_Click(object sender, RoutedEventArgs e)
    {
        Width = Math.Min(800, Width + 80);
        Height = Math.Min(700, Height + 50);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Hide();

    private void Opacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        Panel.Background = new SolidColorBrush(Color.FromArgb((byte)Math.Clamp((int)Math.Round(e.NewValue / 100 * 255), 0, 255),
            26, 34, 48));
    }
}
