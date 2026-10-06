using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using VoiceAssistant.Desktop.Protocol;

namespace VoiceAssistant.Desktop.Tests;

public sealed class PracticeDemoPreviewTests
{
    [Fact]
    public async Task OfflinePracticePreviewUsesCannedTextAndNeverOpensLiveFeatures()
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(new Action(async () =>
            {
                MainWindow? main = null;
                MainWindow? demoMain = null;
                PracticeWindow? practice = null;
                AuthenticatedApiClient? api = null;
                try
                {
                    var settings = new ClientSettings { Mode = ConnectionMode.Production };
                    var identity = new NativeIdentity(settings);
                    api = new AuthenticatedApiClient(settings, identity);
                    main = new MainWindow(settings, identity, api);
                    var livePractice = new PracticeWindow(settings, identity, api);
                    Assert.True(((CheckBox)livePractice.FindName("MaterialsBox")).IsChecked);
                    await livePractice.CloseForOwnerAsync();
                    main.Show();
                    ((Button)main.FindName("DemoPreviewButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    demoMain = (MainWindow)typeof(MainWindow).GetField("demoPreview",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(main)!;
                    Assert.Contains("OFFLINE DEMO", ((TextBlock)demoMain.FindName("AuthStatus")).Text);
                    ((Button)demoMain.FindName("PracticeButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    practice = (PracticeWindow)typeof(MainWindow).GetField("practice",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(demoMain)!;

                    T Control<T>(string name) where T : FrameworkElement => (T)practice.FindName(name);
                    Assert.Contains("OFFLINE DEMO", Control<TextBlock>("AuthText").Text);
                    Assert.False(Control<ComboBox>("DeviceBox").IsEnabled);
                    Assert.False(Control<Button>("MicTestButton").IsEnabled);
                    Assert.False(Control<CheckBox>("ConsentBox").IsEnabled);
                    Assert.False(Control<CheckBox>("MaterialsBox").IsEnabled);
                    Assert.False(Control<CheckBox>("MaterialsBox").IsChecked);

                    Control<Button>("StartButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Contains("OFFLINE DEMO", Control<TextBlock>("StatusText").Text);
                    Assert.Contains("Could you briefly introduce", Control<TextBlock>("QuestionText").Text);

                    Control<Button>("AnswerButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Contains("not recognized speech", Control<TextBox>("AnswerText").Text);
                    Assert.True(Control<Button>("DoneButton").IsEnabled);
                    Control<Button>("DoneButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    string feedback = Control<TextBlock>("FeedbackText").Text;
                    Assert.Contains("DEMO FEEDBACK", feedback);
                    Assert.False(Control<Button>("AnswerButton").IsEnabled);
                    Assert.False(Control<Button>("DoneButton").IsEnabled);
                    Assert.True(Control<Button>("NextButton").IsEnabled);

                    Control<Button>("DoneButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal(feedback, Control<TextBlock>("FeedbackText").Text);
                    Control<Button>("NextButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Contains("What is the main risk", Control<TextBlock>("QuestionText").Text);
                    Control<Button>("SkipButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Control<Button>("NextButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Contains("OFFLINE DEMO SUMMARY", Control<TextBlock>("RoundSummaryText").Text);
                    Assert.Contains("OFFLINE DEMO COMPLETE", Control<TextBlock>("StatusText").Text);

                    await practice.CloseForOwnerAsync();
                    await demoMain.CloseForOwnerAsync();
                    await main.CloseForOwnerAsync();
                    result.TrySetResult();
                }
                catch (Exception ex) { result.TrySetException(ex); }
                finally
                {
                    api?.Dispose();
                    if (practice?.IsVisible == true) await practice.CloseForOwnerAsync();
                    if (demoMain?.IsVisible == true) await demoMain.CloseForOwnerAsync();
                    if (main?.IsVisible == true) await main.CloseForOwnerAsync();
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        await result.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
    }
}
