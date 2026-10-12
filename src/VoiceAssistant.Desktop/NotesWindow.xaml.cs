using System.Windows;

namespace VoiceAssistant.Desktop;

public partial class NotesWindow : Window
{
    public NotesWindow() => InitializeComponent();
    public string NoteTitle => TitleBox.Text.Trim();
    public string NoteText => NotesBox.Text;

    private void Upload_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TitleBox.Text) || string.IsNullOrWhiteSpace(NotesBox.Text))
        {
            MessageBox.Show(this, "Enter a title and some notes.", "Notes required", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
    }
}
