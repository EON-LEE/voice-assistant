using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace VoiceAssistant.Desktop.Tests;

public sealed class DesktopUiTests
{
    [Fact]
    public async Task WpfDemoStartsOnlyOnClickAndPinRemainsSeparateFromNewReply()
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(async () =>
            {
                MainWindow? window = null;
                try
                {
                    window = new MainWindow(new ClientSettings());
                    T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
                    void Click(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.True(Control<Button>("StartButton").IsEnabled);
                    Assert.False(Control<Button>("StopButton").IsEnabled);
                    Assert.Null(Control<ComboBox>("DeviceBox").ItemsSource);
                    Assert.False(window.Topmost);
                    Assert.Equal("", Control<TextBox>("TranscriptBox").Text);
                    Click("StartButton");
                    await Until(() => Control<Button>("RequestButton").IsEnabled);
                    Assert.Contains("[final]", Control<TextBox>("TranscriptBox").Text);
                    Click("RequestButton");
                    await Until(() => Control<TextBlock>("ReplyStatus").Text == "Complete");
                    Click("PinButton");
                    string pinned = Control<TextBox>("PinnedBox").Text;
                    Assert.NotEmpty(pinned);
                    Click("CancelButton");
                    Assert.Empty(Control<TextBox>("ReplyBox").Text);
                    Assert.Equal(pinned, Control<TextBox>("PinnedBox").Text);
                    Click("RequestButton");
                    await Until(() => Control<TextBlock>("ReplyStatus").Text == "Complete");
                    Assert.Equal(pinned, Control<TextBox>("PinnedBox").Text);
                    Control<CheckBox>("PauseBox").IsChecked = true;
                    await Until(() => !Control<Button>("RequestButton").IsEnabled);
                    Click("StopButton");
                    await Until(() => Control<Button>("StartButton").IsEnabled);
                    Assert.Equal(pinned, Control<TextBox>("PinnedBox").Text);
                    Assert.False(Control<Button>("StopButton").IsEnabled);
                    result.TrySetResult();
                }
                catch (Exception ex) { result.TrySetException(ex); }
                finally { window?.Close(); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        await result.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}
