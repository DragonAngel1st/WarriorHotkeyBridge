namespace WarriorHotkeyBridge.Startup;

/// <summary>
/// Lets a second launch ask the running bridge to make the trading session ready.
/// </summary>
/// <remarks>
/// <para>
/// A "go trading" button means "put me in a state where I can trade". Whether a bridge happens
/// to be resident already is an implementation detail the operator should never have to think
/// about, so pressing it twice must be harmless and must still do something useful. Without this
/// the second press hit the single-instance guard, reported that two instances are not allowed,
/// and exited having achieved nothing - so a session whose Chrome had been closed stayed dead
/// until a watchdog got round to it.
/// </para>
/// <para>
/// Auto-reset rather than manual, and registered to fire repeatedly, because unlike shutdown
/// this is not a once-per-process event: every press is a fresh request.
/// </para>
/// </remarks>
internal sealed class ActivationSignal : IDisposable
{
    /// <summary>Session-local, matching the instance mutex and the shutdown events.</summary>
    private const string EventName = @"Local\WarriorHotkeyBridge.Activate";

    private readonly EventWaitHandle _activate;
    private readonly Lock _gate = new();
    private RegisteredWaitHandle? _registration;
    private Action? _handler;
    private bool _pending;
    private bool _disposed;

    private ActivationSignal(EventWaitHandle activate) => _activate = activate;

    /// <summary>
    /// Publishes the event. Call immediately after the instance slot is taken, so a launch that
    /// arrives while this one is still starting is answered rather than lost.
    /// </summary>
    public static ActivationSignal Create()
    {
        var activate = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, EventName);
        var signal = new ActivationSignal(activate);

        signal._registration = ThreadPool.RegisterWaitForSingleObject(
            activate, (_, _) => signal.OnSignalled(), state: null, Timeout.Infinite, executeOnlyOnce: false);

        return signal;
    }

    /// <summary>Supplies the handler, running it at once if a request already arrived.</summary>
    public void Attach(Action onSignalled)
    {
        ArgumentNullException.ThrowIfNull(onSignalled);

        bool runNow;

        lock (_gate)
        {
            _handler = onSignalled;
            runNow = _pending;
            _pending = false;
        }

        if (runNow)
        {
            onSignalled();
        }
    }

    /// <summary>Asks a running instance to ready the session.</summary>
    /// <returns>False when no instance is running, which is not an error.</returns>
    public static bool TrySignal()
    {
        if (!EventWaitHandle.TryOpenExisting(EventName, out EventWaitHandle? handle))
        {
            return false;
        }

        using (handle)
        {
            handle.Set();
            return true;
        }
    }

    private void OnSignalled()
    {
        Action handler;

        lock (_gate)
        {
            if (_handler is null)
            {
                // Arrived during startup. Remembered rather than dropped: the operator pressed
                // the button, and the request outlives whichever millisecond it landed in.
                _pending = true;
                return;
            }

            handler = _handler;
        }

        handler();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _registration?.Unregister(waitObject: null);
        _activate.Dispose();
    }
}
