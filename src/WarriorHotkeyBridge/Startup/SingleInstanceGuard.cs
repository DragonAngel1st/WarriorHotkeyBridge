namespace WarriorHotkeyBridge.Startup;

/// <summary>
/// Ensures only one bridge runs per interactive Windows session.
/// </summary>
/// <remarks>
/// <para>
/// The mutex is session-scoped (<c>Local\</c>) rather than machine-scoped (<c>Global\</c>)
/// because that matches what is actually being protected: <c>RegisterHotKey</c> is per
/// session, so two sessions could each run their own bridge without conflict, while two
/// instances in one session would fight over the same hotkeys.
/// </para>
/// <para>
/// The guard applies to normal and debug mode alike. Allowing "just one debug instance
/// alongside" is exactly the case that silently breaks hotkeys: the second process's
/// <c>RegisterHotKey</c> calls fail and its keypresses go to the first process.
/// </para>
/// </remarks>
internal sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName = @"Local\WarriorHotkeyBridge.SingleInstance";

    private readonly Mutex _mutex;
    private bool _disposed;

    private SingleInstanceGuard(Mutex mutex, bool isPrimary)
    {
        _mutex = mutex;
        IsPrimary = isPrimary;
    }

    /// <summary>True when this process owns the instance slot and may continue starting.</summary>
    public bool IsPrimary { get; }

    public static SingleInstanceGuard Acquire()
    {
        var mutex = new Mutex(initiallyOwned: false, MutexName);

        bool isPrimary;
        try
        {
            isPrimary = mutex.WaitOne(TimeSpan.Zero, exitContext: false);
        }
        catch (AbandonedMutexException)
        {
            // The previous instance died without releasing (crash, or killed from Task
            // Manager). The wait still succeeded and we now own the mutex, so we are the
            // primary instance; there is no shared state left behind to repair.
            isPrimary = true;
        }

        return new SingleInstanceGuard(mutex, isPrimary);
    }

    /// <summary>
    /// Blocks until no bridge holds the instance slot, or the timeout expires.
    /// </summary>
    /// <returns>True when the slot is free, false on timeout.</returns>
    /// <remarks>
    /// <para>
    /// Used by <c>--quit</c> after signalling, so the caller learns when the old instance has
    /// actually gone rather than merely when it was asked to go. The mutex is released in the
    /// running instance's <c>finally</c>, after the hotkeys are unregistered, the queue is
    /// drained and the log is flushed - so acquiring it here is evidence that the orderly
    /// shutdown ran to completion, not just that the process is somewhere in the middle of one.
    /// </para>
    /// <para>
    /// This matters most to the installer: replacing a locked executable forces MSI into a
    /// files-in-use prompt or a reboot request. It also makes a Stream Deck stop button honest,
    /// since the button no longer reports done while the hotkeys are still registered.
    /// </para>
    /// </remarks>
    public static bool WaitUntilFree(TimeSpan timeout)
    {
        using var mutex = new Mutex(initiallyOwned: false, MutexName);

        bool acquired;
        try
        {
            acquired = mutex.WaitOne(timeout, exitContext: false);
        }
        catch (AbandonedMutexException)
        {
            // The holder died rather than exiting cleanly. The slot is free either way, which is
            // the only thing the caller asked about.
            acquired = true;
        }

        if (acquired)
        {
            mutex.ReleaseMutex();
        }

        return acquired;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (IsPrimary)
        {
            _mutex.ReleaseMutex();
        }

        _mutex.Dispose();
    }
}
