using VoiceAssistant.Api;
using Xunit;

namespace VoiceAssistant.Api.Tests;

public sealed class SessionSlotTests
{
    [Fact]
    public async Task ReconnectSupersedesStaleSameIdentitySessionInsteadOfRejecting()
    {
        var slots = new SessionSlots();
        var stale = await slots.AcquireAsync("user-a", CancellationToken.None);
        Assert.NotNull(stale);
        Assert.False(stale.Superseded.IsCancellationRequested);
        var reconnect = slots.AcquireAsync("user-a", CancellationToken.None);
        Assert.True(stale.Superseded.IsCancellationRequested);
        Assert.False(reconnect.IsCompleted);
        stale.Dispose();
        var fresh = await reconnect.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(fresh);
        Assert.False(fresh.Superseded.IsCancellationRequested);
        stale.Dispose();
        Assert.Null(slots.TryAcquire("user-a"));
        fresh.Dispose();
        using var after = slots.TryAcquire("user-a");
        Assert.NotNull(after);
    }

    [Fact]
    public async Task DifferentIdentitiesNeverSupersedeEachOther()
    {
        var slots = new SessionSlots();
        using var a = await slots.AcquireAsync("user-a", CancellationToken.None);
        using var b = await slots.AcquireAsync("user-b", CancellationToken.None);
        Assert.NotNull(b);
        Assert.False(a!.Superseded.IsCancellationRequested);
    }
}
