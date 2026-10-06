using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using VoiceAssistant.Desktop.Protocol;

namespace VoiceAssistant.Desktop.Tests;

public sealed class DesktopUiTests
{
    [Fact]
    public async Task WpfStartsWithoutCaptureAndUsesSeparateTranslucentResizableOverlay()
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(() =>
            {
                MainWindow? window = null;
                try
                {
                    var settings = new ClientSettings();
                    var identity = new NativeIdentity(settings);
                    var api = new AuthenticatedApiClient(settings, identity);
                    window = new MainWindow(settings, identity, api);
                    T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
                    Assert.True(Control<Button>("StartButton").IsEnabled);
                    Assert.False(Control<Button>("StopButton").IsEnabled);
                    Assert.NotNull(Control<ComboBox>("DeviceBox").ItemsSource);
                    Assert.False(Control<CheckBox>("ConsentBox").IsEnabled);
                    Assert.False(Control<ComboBox>("DeviceBox").IsEnabled);
                    Assert.False(Control<Button>("MicTestButton").IsEnabled);
                    Assert.False(Control<TabItem>("MaterialsTab").IsEnabled);
                    Assert.Contains("OFFLINE DEMO", Control<TextBlock>("AuthStatus").Text);
                    Assert.Equal("Start offline overlay preview", Control<Button>("StartButton").Content);
                    Assert.False(window.Topmost);
                    Assert.Contains("OFFLINE DEMO", Control<TextBlock>("StatusText").Text);
                    Assert.False(Control<Button>("RequestButton").IsEnabled);
                    var overlay = (OverlayWindow)typeof(MainWindow).GetField("overlay",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
                    Assert.Equal(420, overlay.Width);
                    Assert.Equal(260, overlay.Height);
                    Assert.True(overlay.AllowsTransparency);
                    Assert.Equal(ResizeMode.CanResizeWithGrip, overlay.ResizeMode);
                    Assert.True(overlay.Topmost);
                    Assert.Equal(0, ((SolidColorBrush)overlay.Background).Color.A);
                    Assert.False(overlay.IsVisible);
                    overlay.ShowMeetingState(false, "", "", "Offline canned response", true, demo: true);
                    Assert.Contains("OFFLINE DEMO", ((TextBlock)overlay.FindName("CaptureStatus")).Text);
                    Control<Button>("ShowOverlayButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.True(overlay.IsVisible);
                    overlay.Hide();
                    Control<Button>("ShowOverlayButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.True(overlay.IsVisible);
                    overlay.ShowMeetingState(true, "Can you confirm?", "Yes, I can.", "Grounded", true,
                        "네, 확인하겠습니다.", "Yes (예) · I can (아이 캔)");
                    Assert.True(Control<Button>("StartButton").IsEnabled);
                    api.Dispose();
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

    [Fact]
    public async Task DemoMeetingAutoRepliesAndKeepsLiveControlsDisabledAfterStop()
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(async () =>
            {
                MainWindow? window = null;
                var settings = new ClientSettings();
                var identity = new NativeIdentity(settings);
                var api = new AuthenticatedApiClient(settings, identity);
                try
                {
                    window = new MainWindow(settings, identity, api);
                    T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
                    void AssertLiveControlsDisabled()
                    {
                        Assert.False(Control<Button>("SignInButton").IsEnabled);
                        Assert.False(Control<Button>("DeviceCodeButton").IsEnabled);
                        Assert.False(Control<ComboBox>("DeviceBox").IsEnabled);
                        Assert.False(Control<CheckBox>("ConsentBox").IsEnabled);
                        Assert.False(Control<Button>("RefreshButton").IsEnabled);
                        Assert.False(Control<Button>("MicTestButton").IsEnabled);
                    }
                    Control<Button>("StartButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var deadline = DateTime.UtcNow.AddSeconds(5);
                    while (Control<TextBox>("ReplyBox").Text != "Yes, I can share an update by Friday." && DateTime.UtcNow < deadline)
                        await Task.Delay(25);
                    Assert.Equal("Yes, I can share an update by Friday.", Control<TextBox>("ReplyBox").Text);
                    AssertLiveControlsDisabled();
                    Control<Button>("StopButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    deadline = DateTime.UtcNow.AddSeconds(5);
                    while (!Control<Button>("StartButton").IsEnabled && DateTime.UtcNow < deadline)
                        await Task.Delay(25);
                    Assert.True(Control<Button>("StartButton").IsEnabled);
                    AssertLiveControlsDisabled();
                    Assert.Contains("OFFLINE DEMO", Control<TextBlock>("StatusText").Text);
                    result.TrySetResult();
                }
                catch (Exception ex) { result.TrySetException(ex); }
                finally
                {
                    window?.Close();
                    api.Dispose();
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        await result.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
    }

}
