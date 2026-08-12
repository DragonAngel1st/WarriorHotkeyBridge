using WarriorHotkeyBridge.Chrome;

namespace WarriorHotkeyBridge.Tests;

public class ExponentialBackoffTests
{
    private static ExponentialBackoff NoJitter(double initialSeconds = 1, double maxSeconds = 30) =>
        new(TimeSpan.FromSeconds(initialSeconds), TimeSpan.FromSeconds(maxSeconds), jitterFactor: 0);

    [Fact]
    public void NextDelay_DoublesUntilItReachesTheCeiling()
    {
        ExponentialBackoff backoff = NoJitter();

        double[] seconds = [.. Enumerable.Range(0, 8).Select(_ => backoff.NextDelay().TotalSeconds)];

        Assert.Equal([1, 2, 4, 8, 16, 30, 30, 30], seconds);
    }

    [Fact]
    public void NextDelay_NeverExceedsTheCeilingOverAVeryLongOutage()
    {
        // Chrome can be closed overnight. A naive shift-based implementation overflows here and
        // throws deep inside the reconnect loop.
        ExponentialBackoff backoff = NoJitter();

        for (int i = 0; i < 10_000; i++)
        {
            TimeSpan delay = backoff.NextDelay();

            Assert.InRange(delay, TimeSpan.Zero, TimeSpan.FromSeconds(30));
        }
    }

    [Fact]
    public void Reset_ReturnsToTheFastFirstRetry()
    {
        ExponentialBackoff backoff = NoJitter();

        backoff.NextDelay();
        backoff.NextDelay();
        backoff.NextDelay();
        Assert.Equal(3, backoff.Attempt);

        backoff.Reset();

        Assert.Equal(0, backoff.Attempt);
        Assert.Equal(TimeSpan.FromSeconds(1), backoff.NextDelay());
    }

    [Fact]
    public void NextDelay_AddsJitterWithinTheConfiguredFraction()
    {
        // Deterministic "random" pinned to its extremes.
        var atMax = new ExponentialBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), 0.2, () => 1.0);
        var atMin = new ExponentialBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), 0.2, () => 0.0);

        Assert.Equal(TimeSpan.FromSeconds(1.2), atMax.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(1.0), atMin.NextDelay());
    }

    [Fact]
    public void Constructor_RejectsNonsensicalConfiguration()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ExponentialBackoff(TimeSpan.Zero, TimeSpan.FromSeconds(30)));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ExponentialBackoff(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1)));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ExponentialBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), jitterFactor: 1.5));
    }
}
