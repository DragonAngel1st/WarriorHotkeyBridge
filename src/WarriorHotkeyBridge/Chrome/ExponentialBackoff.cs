namespace WarriorHotkeyBridge.Chrome;

/// <summary>
/// Bounded exponential backoff with jitter, used between reconnection attempts.
/// </summary>
/// <remarks>
/// <para>
/// Chrome being closed is a completely normal state for this application - the operator shuts
/// it at the end of the day and reopens it next morning. Retrying tightly for hours would burn
/// CPU on a machine that is meant to be running a trading platform, so the delay grows to a
/// ceiling and stays there.
/// </para>
/// <para>
/// Deterministic and free of ambient time so the sequence can be unit tested; the caller owns
/// the actual waiting.
/// </para>
/// </remarks>
internal sealed class ExponentialBackoff
{
    private readonly TimeSpan _initialDelay;
    private readonly TimeSpan _maxDelay;
    private readonly double _jitterFactor;
    private readonly Func<double> _random;

    private int _attempt;

    /// <param name="jitterFactor">
    /// Fraction of the delay that may be added as jitter, 0 to 1. Spreads out retries so a
    /// Chrome restart does not produce a synchronised reconnect burst.
    /// </param>
    /// <param name="random">Injected for deterministic tests; defaults to shared randomness.</param>
    public ExponentialBackoff(
        TimeSpan initialDelay,
        TimeSpan maxDelay,
        double jitterFactor = 0.2,
        Func<double>? random = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(initialDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDelay, initialDelay);
        ArgumentOutOfRangeException.ThrowIfNegative(jitterFactor);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(jitterFactor, 1.0);

        _initialDelay = initialDelay;
        _maxDelay = maxDelay;
        _jitterFactor = jitterFactor;
        _random = random ?? Random.Shared.NextDouble;
    }

    /// <summary>Number of failures recorded since the last <see cref="Reset"/>.</summary>
    public int Attempt => _attempt;

    /// <summary>Returns the delay before the next attempt and advances the sequence.</summary>
    public TimeSpan NextDelay()
    {
        // Doubled by repeated multiplication that stops at the ceiling rather than by shifting
        // the attempt count: Chrome can be closed overnight, and a shift wide enough to
        // overflow would produce a negative tick count and an ArgumentException deep inside a
        // reconnect loop. The loop runs at most log2(max/initial) times before breaking.
        long baseTicks = _initialDelay.Ticks;

        for (int i = 0; i < _attempt; i++)
        {
            if (baseTicks >= _maxDelay.Ticks)
            {
                break;
            }

            baseTicks *= 2;
        }

        baseTicks = Math.Min(baseTicks, _maxDelay.Ticks);

        if (_attempt < int.MaxValue)
        {
            _attempt++;
        }

        double jitter = _jitterFactor <= 0 ? 0 : baseTicks * _jitterFactor * _random();

        return TimeSpan.FromTicks(baseTicks + (long)jitter);
    }

    /// <summary>Called after a successful connection so the next outage starts fast again.</summary>
    public void Reset() => _attempt = 0;
}
