namespace WarriorHotkeyBridge.Services;

/// <summary>
/// Marshals work onto the WinForms UI thread.
/// </summary>
/// <remarks>
/// Chrome events, the watchdog timer and hotkey handling all publish state from background
/// threads, but <see cref="System.Windows.Forms.NotifyIcon"/> may only be touched from the
/// thread that runs the message loop.
/// </remarks>
internal interface IUiDispatcher
{
    bool IsOnUiThread { get; }

    /// <summary>
    /// Queues <paramref name="action"/> on the UI thread and returns immediately; runs it
    /// inline when already on the UI thread. Never blocks the caller, so it is safe to call
    /// from the command path.
    /// </summary>
    void Post(Action action);

    /// <summary>
    /// Queues <paramref name="action"/> to run when the message loop next pumps - never inline,
    /// even when called from the UI thread.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Post"/> runs inline on the UI thread, which is right for publishing state: it
    /// keeps the tray current with no scheduling latency. It is wrong for anything that blocks,
    /// because hosted services start on the UI thread <em>before</em>
    /// <see cref="System.Windows.Forms.Application.Run(System.Windows.Forms.ApplicationContext)"/>
    /// is reached. Anything modal posted from there runs immediately, stalls host start, and so
    /// appears before the tray icon exists and before the hotkeys are registered - with no owner
    /// window, so it can sit behind whatever the operator is looking at.
    /// </para>
    /// <para>
    /// Deferring makes the ordering explicit: the work is queued now and runs once the loop is
    /// live, by which point the tray icon is up and the hotkeys are registered.
    /// </para>
    /// </remarks>
    void Defer(Action action);
}
