using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using VoiceAssistant.Desktop.Protocol;

namespace VoiceAssistant.Desktop.Tests;

public sealed class DesktopUiTests
{
    [Fact]
    public async Task WpfStartsWithoutCaptureAndUsesFixedSeparateTranslucentOverlay()
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
                    Assert.True(Control<CheckBox>("KoreanBox").IsChecked);
                    var overlay = (OverlayWindow)typeof(MainWindow).GetField("overlay",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
                    Assert.Equal(550, overlay.Width);
                    Assert.True(overlay.Height <= 860);
                    Assert.True(overlay.AllowsTransparency);
                    Assert.Equal(ResizeMode.NoResize, overlay.ResizeMode);
                    Assert.True(overlay.Topmost);
                    Assert.Equal(0, ((SolidColorBrush)overlay.Background).Color.A);
                    Assert.DoesNotContain(VisualChildren(overlay), element => element is Button or Slider);
                    Assert.False(overlay.IsVisible);
                    overlay.ShowSessionState(false, "Offline canned response", true, demo: true);
                    Assert.Contains("OFFLINE DEMO", ((TextBlock)overlay.FindName("CaptureStatus")).Text);
                    Control<Button>("ShowOverlayButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.True(overlay.IsVisible);
                    Control<Button>("ShowOverlayButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.False(overlay.IsVisible);
                    Control<Button>("ShowOverlayButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.True(overlay.IsVisible);
                    overlay.ShowSessionState(true, "Grounded", true);
                    void Apply(ServerEvent message) => typeof(MainWindow).GetMethod("ApplyMeetingEvent",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                        .Invoke(window, [message]);
                    Apply(new("transcript.final", "turn-1", 1, "Can you confirm the delivery date?"));
                    Apply(new("response.started", "turn-1", ResponseId: "response-1"));
                    Apply(new("response.delta", "turn-1", Text: "Yes, we expect Friday.", ResponseId: "response-1"));
                    Apply(new("response.completed", "turn-1", Text: "Yes, we expect Friday.", ResponseId: "response-1", Sources: []));
                    var bubbles = Control<StackPanel>("ConversationPanel");
                    Assert.Single(bubbles.Children);
                    var bubble = Assert.IsType<Border>(bubbles.Children[0]);
                    var bubbleContent = Assert.IsType<StackPanel>(bubble.Child);
                    var transcriptText = Assert.IsType<TextBlock>(bubbleContent.Children[1]);
                    Assert.Equal("Can you confirm the delivery date?", transcriptText.Text);
                    Assert.Contains("RECOGNIZED ENGLISH", Assert.IsType<TextBlock>(bubbleContent.Children[0]).Text);
                    Assert.Equal("Yes, we expect Friday.", Control<TextBox>("ReplyBox").Text);
                    Apply(new("response.started", "turn-1", ResponseId: "response-2"));
                    Apply(new("response.completed", "turn-1", Text: "Review first.", ResponseId: "response-2",
                        Sources: [], Suggestions: ["Review first.", "Let's review before launch."]));
                    Assert.Equal(Visibility.Visible, ((Border)overlay.FindName("AlternativeCard")).Visibility);
                    Assert.Equal("Let's review before launch.", ((TextBlock)overlay.FindName("AlternativeText")).Text);
                    Assert.Equal("01 · 바로 답하기", ((TextBlock)overlay.FindName("PrimaryLabel")).Text);
                    overlay.ShowSessionState(true, "Grounded", true);
                    Assert.Equal("Review first.", ((TextBlock)overlay.FindName("AnswerText")).Text);
                    Assert.Equal("Let's review before launch.", ((TextBlock)overlay.FindName("AlternativeText")).Text);
                    Apply(new("transcript.partial", "turn-2", 1, "And when?"));
                    Assert.Equal("Review first.", ((TextBlock)overlay.FindName("AnswerText")).Text);
                    Assert.Contains("이전", ((TextBlock)overlay.FindName("PrimaryLabel")).Text);
                    var overlayConversation = (StackPanel)overlay.FindName("ConversationPanel");
                    Assert.Equal(2, overlayConversation.Children.Count);
                    Assert.All(overlayConversation.Children.Cast<object>(), child => Assert.IsType<StackPanel>(child));
                    Assert.Equal(240, ((ScrollViewer)overlay.FindName("ConversationScroll")).Height);
                    var firstLine = overlayConversation.Children[0];
                    Apply(new("response.started", "turn-2", ResponseId: "response-3"));
                    Apply(new("response.delta", "turn-2", Text: "After review.", ResponseId: "response-3"));
                    Assert.Same(firstLine, overlayConversation.Children[0]);
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
                    var overlay = (OverlayWindow)typeof(MainWindow).GetField("overlay",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
                    Assert.Equal(Visibility.Visible, ((Border)overlay.FindName("AlternativeCard")).Visibility);
                    Assert.Contains("금요일", ((TextBlock)overlay.FindName("KoreanText")).Text);
                    Assert.Contains("금요일", ((TextBlock)overlay.FindName("AlternativeKoreanText")).Text);
                    overlay.UpdateLayout();
                    var suggestionScroll = (ScrollViewer)overlay.FindName("SuggestionsScroll");
                    var alternative = (Border)overlay.FindName("AlternativeCard");
                    var bottom = alternative.TransformToAncestor(suggestionScroll)
                        .Transform(new Point(0, alternative.ActualHeight)).Y;
                    Assert.True(bottom <= suggestionScroll.ActualHeight,
                        $"Both sample suggestions must fit without scrolling: bottom={bottom}, viewport={suggestionScroll.ActualHeight}.");
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

    private static IEnumerable<DependencyObject> VisualChildren(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in VisualChildren(child)) yield return descendant;
        }
    }
}
