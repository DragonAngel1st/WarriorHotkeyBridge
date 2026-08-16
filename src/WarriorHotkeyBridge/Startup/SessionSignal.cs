namespace WarriorHotkeyBridge.Startup;

/// <summary>What another process is asking a running bridge to do.</summary>
internal enum SessionRequest
{
    /// <summary>Register the hotkeys and bring Chrome up.</summary>
    Arm,

    /// <summary>Release the hotkeys and close Chrome, staying resident.</summary>
    Park,
}

/// <summary>
/// Lets a Stream Deck button switch a running bridge on or off.
/// </summary>
/// <remarks>
/// <para>
/// Two named events with one handler, following <see cref="ShutdownSignal"/> exactly: which
/// handle was signalled is unambiguous by construction, whereas a shared flag written by one
/// process and read by another would need synchronisation to be correct.
/// </para>
/// <para>
/// This replaces the old activation event, which could only ever mean "ready the session". A stop
/// button had nothing to send: the only way to release the hotkeys was to end the process, which
/// is why stopping used to take the tray icon with it.
/// </para>
/// <para>
/// Auto-reset and registered to fire repeatedly, because unlike shutdown these are not
/// once-per-process events - every press of a deck key is a fresh request, and pressing an
/// already-on button must remain harmless.
/// </para>
/// </remarks>
internal sealed class SessionSignal : IDisposable
{
    /// <summary>Session-local, matching the instance mutex: hotkeys are per-session too.</summary>
    private const string ArmEventName = @"Local\WarriorHotkeyBridge.Activate";

    private const string ParkEventName = @"Local\WarriorHotkeyBridge.Park";

    private readonly EventWaitHandle _arm;
    private readonly EventWaitHandle _park;
    private readonly Lock _gate = new();
    private RegisteredWaitHandle? _armRegistration;
    private RegisteredWaitHandle? _parkRegistration;
    private Action<SessionRequest>? _handler;
    private SessionRequest? _pending;
    private bool _disposed;

    private SessionSignal(EventWaitHandle arm, EventWaitHandle park)
    {
        _arm = arm;
        _park = park;
    }

    /// <summary>
    /// Publishes both events. Call immediately after the instance slot is taken, so a request
    /// arriving while this instance is still starting is answered rather than lost.
    /// </summary>
    public static SessionSignal Create()
    {
        var arm = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, ArmEventName);
        var park = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, ParkEventName);
        var signal = new SessionSignal(arm, park);

        signal._armRegistration = ThreadPool.RegisterWaitForSingleObject(
            arm, (_, _) => signal.OnSignalled(SessionRequest.Arm), state: null, Timeout.Infinite, executeOnlyOnce: false);

        signal._parkRegistration = ThreadPool.RegisterWaitForSingleObject(
            park, (_, _) => signal.OnSignalled(SessionRequest.Park), state: null, Timeout.Infinite, executeOnlyOnce: false);

        return signal;
    }

    /// <summary>Supplies the handler, running it at once if a request already arrived.</summary>
    public void Attach(Action<SessionRequest> onSignalled)
    {
        ArgumentNullException.ThrowIfNull(onSignalled);

        SessionRequest? runNow;

        lock (_gate)
        {
            _handler = onSignalled;
            runNow = _pending;
            _pending = null;
        }

        if (runNow is { } request)
        {
            onSignalled(request);
        }
    }

    /// <summary>Asks a running instance to arm or park.</summary>
    /// <returns>False when no instance is running, which is not an error.</returns>
    public static bool TrySignal(SessionRequest request)
    {
        string name = request is SessionRequest.Arm ? ArmEventName : ParkEventName;

        if (!EventWaitHandle.TryOpenExisting(name, out EventWaitHandle? handle))
        {
            return false;
        }

        using (handle)
        {
            handle.Set();
            return true;
        }
    }

    private void OnSignalled(SessionRequest request)
    {
        Action<SessionRequest> handler;

        lock (_gate)
        {
            if (_handler is null)
            {
                // Arrived during startup. Remembered rather than dropped: the operator pressed the
                // button, and the request outlives whichever millisecond it landed in. The latest
                // wins, because pressing stop after start means stop.
                _pending = request;
                return;
            }

            handler = _handler;
        }

        handler(request);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _armRegistration?.Unregister(waitObject: null);
        _parkRegistration?.Unregister(waitObject: null);
        _arm.Dispose();
        _park.Dispose();
    }
}
