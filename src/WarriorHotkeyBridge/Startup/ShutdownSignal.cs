namespace WarriorHotkeyBridge.Startup;

/// <summary>
/// Lets another process ask a running bridge to shut down cleanly.
/// </summary>
/// <remarks>
/// <para>
/// Named events rather than a pipe or a socket: there is almost no information to convey, no
/// reply is needed, and the kernel already provides the primitive. They also pair naturally
/// with the single-instance mutex, which uses the same session-local namespace.
/// </para>
/// <para>
/// This exists so a Stream Deck button can end a trading session. Killing the process would
/// also release the hotkeys - Windows reclaims them when the owning window dies - but it would
/// skip flushing the log and skip the orderly drain of the command queue, so a command already
/// in flight could be abandoned halfway. Asking is better than killing.
/// </para>
/// <para>
/// There are two events rather than one event plus a shared flag, because a flag written by one
/// process and read by another needs synchronisation to be correct, whereas which handle was
/// signalled is unambiguous by construction.
/// </para>
/// </remarks>
internal sealed class ShutdownSignal : IDisposable
{
    /// <summary>Session-local, matching the single-instance mutex: hotkeys are per-session too.</summary>
    private const string StopEventName = @"Local\WarriorHotkeyBridge.Shutdown";

    private const string StopAndCloseChromeEventName = @"Local\WarriorHotkeyBridge.ShutdownAndCloseChrome";

    private readonly EventWaitHandle _stop;
    private readonly EventWaitHandle _stopAndClose;
    private readonly Lock _gate = new();
    private RegisteredWaitHandle? _stopRegistration;
    private RegisteredWaitHandle? _stopAndCloseRegistration;
    private Action<bool>? _handler;
    private bool _pending;
    private bool _pendingCloseChrome;
    private bool _disposed;

    private ShutdownSignal(EventWaitHandle stop, EventWaitHandle stopAndClose)
    {
        _stop = stop;
        _stopAndClose = stopAndClose;
    }

    /// <summary>
    /// Publishes the shutdown events. Call as early as possible; attach the handler later.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Creation is split from handling so this can run immediately after the instance mutex is
    /// taken, rather than after configuration, logging and the whole host have been built.
    /// </para>
    /// <para>
    /// The gap between those two points is what made the split necessary. A caller decides
    /// whether an instance is running by trying to open these events, so during startup - which
    /// includes reading configuration files and constructing Playwright - a fully live process
    /// would answer "not running". The installer stops the bridge that way before replacing its
    /// files, so an upgrade launched seconds after sign-in would be told there was nothing to
    /// stop and would then meet every one of those files locked. Publishing the events beside
    /// the mutex closes the window to the few instructions between them.
    /// </para>
    /// </remarks>
    public static ShutdownSignal Create()
    {
        var stop = new EventWaitHandle(initialState: false, EventResetMode.ManualReset, StopEventName);
        var stopAndClose = new EventWaitHandle(initialState: false, EventResetMode.ManualReset, StopAndCloseChromeEventName);
        var signal = new ShutdownSignal(stop, stopAndClose);

        // The callbacks land on thread-pool threads. That is fine: all they do is request
        // shutdown, which is thread safe and already marshals the message-loop exit through the
        // UI dispatcher.
        signal._stopRegistration = ThreadPool.RegisterWaitForSingleObject(
            stop, (_, _) => signal.OnSignalled(false), state: null, Timeout.Infinite, executeOnlyOnce: true);

        signal._stopAndCloseRegistration = ThreadPool.RegisterWaitForSingleObject(
            stopAndClose, (_, _) => signal.OnSignalled(true), state: null, Timeout.Infinite, executeOnlyOnce: true);

        return signal;
    }

    /// <summary>
    /// Supplies the handler. Runs it at once if a request already arrived while starting up.
    /// </summary>
    /// <param name="onSignalled">
    /// Invoked once, with true when the caller also asked for Chrome to be closed.
    /// </param>
    public void Attach(Action<bool> onSignalled)
    {
        ArgumentNullException.ThrowIfNull(onSignalled);

        bool runNow;
        bool closeChrome;

        lock (_gate)
        {
            _handler = onSignalled;
            runNow = _pending;
            closeChrome = _pendingCloseChrome;
            _pending = false;
        }

        // A stop that arrived mid-startup is honoured rather than lost. Without this the request
        // would be dropped and the caller - which has already been told the signal was
        // delivered - would wait out its timeout against a bridge that never intended to stop.
        if (runNow)
        {
            onSignalled(closeChrome);
        }
    }

    private void OnSignalled(bool closeChrome)
    {
        Action<bool> handler;

        lock (_gate)
        {
            if (_handler is null)
            {
                _pending = true;
                _pendingCloseChrome = closeChrome;
                return;
            }

            handler = _handler;
        }

        handler(closeChrome);
    }

    /// <summary>
    /// Asks a running instance to exit.
    /// </summary>
    /// <returns>False when no instance is running, which is not an error.</returns>
    public static bool TrySignal(bool closeChrome)
    {
        string name = closeChrome ? StopAndCloseChromeEventName : StopEventName;

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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopRegistration?.Unregister(waitObject: null);
        _stopAndCloseRegistration?.Unregister(waitObject: null);
        _stop.Dispose();
        _stopAndClose.Dispose();
    }
}
