namespace VoiceAssistant.WebOverlay.Tests;

public sealed class ShellSettingsTests : IDisposable
{
    private readonly string folder = Path.Combine(AppContext.BaseDirectory, "shell-settings-" + Guid.NewGuid().ToString("N"));

    public ShellSettingsTests() => Directory.CreateDirectory(folder);

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private ShellSettings Load(string json)
    {
        File.WriteAllText(Path.Combine(folder, "overlay-settings.json"), json);
        return ShellSettings.Load(folder);
    }

    [Fact]
    public void TheOverlayIsHiddenFromScreenCaptureByDefaultAndForSettingsSavedBeforeTheOption()
    {
        Assert.True(ShellSettings.Load(folder).ExcludeFromCapture);
        var older = Load("""{"Left":10,"Top":20,"Width":600,"Height":700,"OpacityIndex":2}""");
        Assert.Equal(new ShellSettings(10, 20, 600, 700, 2, ExcludeFromCapture: true), older);
    }

    [Fact]
    public void TurningItOffForADemoLastsUntilTurnedOnAgain()
    {
        var off = Load("""{"Width":600,"Height":700,"ExcludeFromCapture":false}""");
        Assert.False(off.ExcludeFromCapture);
        (off with { Left = 5, Top = 6 }).Save(folder);
        Assert.Equal(new ShellSettings(5, 6, 600, 700, ExcludeFromCapture: false), ShellSettings.Load(folder));
        Assert.True(Load("""{"ExcludeFromCapture":true}""").ExcludeFromCapture);
    }

    [Theory]
    [InlineData("""{"ExcludeFromCapture":"no"}""")]
    [InlineData("""{"ExcludeFromCapture":0}""")]
    [InlineData("not json")]
    public void UnreadableSettingsKeepTheOverlayHidden(string json) => Assert.True(Load(json).ExcludeFromCapture);
}
