using VoiceAssistant.WebOverlay.HostAudio;

namespace VoiceAssistant.WebOverlay.Tests;

public sealed class TeamsProcessLocatorTests
{
    private static readonly DateTime T0 = new(2026, 10, 20, 9, 0, 0, DateTimeKind.Local);

    private static int? Find(IEnumerable<(int Pid, int Parent, string Exe)> processes, Func<int, bool>? session = null,
        Dictionary<int, DateTime>? started = null) =>
        TeamsProcessLocator.FindRoot(processes.Select(p => new ProcessEntry(p.Pid, p.Parent, p.Exe)).ToList(),
            session ?? (_ => true), pid => started is not null && started.TryGetValue(pid, out var time) ? time : null);

    [Fact]
    public void NoTeamsMeansNoRoot() =>
        Assert.Null(Find([(4, 0, "System"), (100, 4, "explorer.exe"), (200, 100, "Teams.exe"), (300, 100, "ms-teams_modulehost.exe")]));

    [Fact]
    public void ReturnsTheParentMostMsTeamsProcess()
    {
        var processes = new[]
        {
            (100, 1, "explorer.exe"),
            (500, 100, "MS-Teams.EXE"),
            (510, 500, "ms-teams.exe"),
            (520, 510, "ms-teams.exe"),
            (530, 500, "msedgewebview2.exe"),
            (540, 530, "msedgewebview2.exe"),
        };
        Assert.Equal(500, Find(processes));
    }

    [Fact]
    public void OtherWindowsSessionsAreNeverCandidates() =>
        Assert.Equal(700, Find([(500, 1, "ms-teams.exe"), (700, 2, "ms-teams.exe")], session: pid => pid == 700));

    [Fact]
    public void AReusedParentIdThatStartedLaterIsNotAParent()
    {
        // 600 was started after 500, so 500 cannot be its child even though 500's recorded parent id is 600.
        var started = new Dictionary<int, DateTime> { [500] = T0, [600] = T0.AddMinutes(5) };
        Assert.Equal(500, Find([(500, 600, "ms-teams.exe"), (600, 1, "ms-teams.exe")], started: started));
    }

    [Fact]
    public void IndependentRootsPreferTheLargestTreeThenTheOldest()
    {
        var processes = new List<(int, int, string)>
        {
            (500, 1, "ms-teams.exe"),
            (900, 1, "ms-teams.exe"),
            (910, 900, "msedgewebview2.exe"),
            (920, 910, "msedgewebview2.exe"),
            (930, 910, "msedgewebview2.exe"),
        };
        Assert.Equal(900, Find(processes));
        var started = new Dictionary<int, DateTime> { [500] = T0.AddMinutes(1), [800] = T0 };
        Assert.Equal(800, Find([(500, 1, "ms-teams.exe"), (800, 1, "ms-teams.exe")], started: started));
        Assert.Equal(500, Find([(800, 1, "ms-teams.exe"), (500, 1, "ms-teams.exe")]));
    }

    [Fact]
    public void ParentIdCyclesStillResolveOneRoot() =>
        Assert.Equal(500, Find([(500, 600, "ms-teams.exe"), (600, 500, "ms-teams.exe"), (700, 700, "ms-teams.exe")],
            session: pid => pid != 700));

    [Theory]
    [InlineData(10, 0, 19041, false)]
    [InlineData(10, 0, 20347, false)]
    [InlineData(10, 0, 20348, true)]
    [InlineData(10, 0, 22631, true)]
    [InlineData(10, 0, 26200, true)]
    [InlineData(6, 3, 9600, false)]
    public void ProcessLoopbackNeedsBuild20348(int major, int minor, int build, bool supported) =>
        Assert.Equal(supported, ProcessLoopbackCapture.IsSupported(new Version(major, minor, build)));
}
