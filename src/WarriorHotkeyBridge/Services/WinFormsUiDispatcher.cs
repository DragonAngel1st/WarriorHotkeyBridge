using Microsoft.Extensions.Logging;
using WarriorHotkeyBridge.Diagnostics;

namespace WarriorHotkeyBridge.Services;

/// <inheritdoc cref="IUiDispatcher"/>
/// <remarks>
/// Must be constructed on the thread that will run <see cref="Application.Run(ApplicationContext)"/>.
/// </remarks>
internal sealed class WinFormsUiDispatcher : IUiDispatcher, IDisposable
{
    private readonly ILogger<WinFormsUiDispatcher> _logger;

    /// <summary>
    /// A never-shown, parentless control. Creating its handle binds it to the constructing
    /// thread's message queue, which is what gives us a target for BeginInvoke.
    /// NotifyIcon has no window handle of its own, so it cannot serve this purpose.
    /// </summary>
    private readonly Control _anchor;

    private volatile bool _disposed;

    public WinFormsUiDispatcher(ILogger<WinFormsUiDispatcher> logger)
    {
        _logger = logger;
        _anchor = new Control();

        // Reading Handle forces handle creation now, on this (UI) thread, rather than
        // lazily on whichever thread happens to post first.
        _ = _anchor.Handle;
    }

    public bool IsOnUiThread => !_disposed && !_anchor.InvokeRequired;

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (_disposed)
        {
            _logger.UiUpdateDroppedDisposed();
            return;
        }

        if (IsOnUiThread)
        {
            action();
            return;
        }

        try
        {
            _anchor.BeginInvoke(action);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            // Benign shutdown race: the message loop ended between the _disposed check and
            // the post. There is no UI left to update, so dropping the update is correct.
            _logger.UiUpdateDroppedNoMessageLoop(ex);
        }
    }

    public void Defer(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (_disposed)
        {
            _logger.UiUpdateDroppedDisposed();
            return;
        }

        try
        {
            // BeginInvoke unconditionally, with no IsOnUiThread shortcut: posting the message is
            // the whole point. Before Application.Run the message simply waits in the queue and
            // is dispatched when the loop starts.
            _anchor.BeginInvoke(action);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            _logger.UiUpdateDroppedNoMessageLoop(ex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _anchor.Dispose();
    }
}
