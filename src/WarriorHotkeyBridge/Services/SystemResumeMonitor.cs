using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using WarriorHotkeyBridge.Diagnostics;

namespace WarriorHotkeyBridge.Services;

internal interface ISystemResumeMonitor
{
    /// <summary>Raised after the machine wakes from sleep or hibernation.</summary>
    event EventHandler? Resumed;
}

/// <summary>
/// Reports when Windows wakes from sleep.
/// </summary>
/// <remarks>
/// <para>
/// A suspended machine leaves the CDP WebSocket in an ambiguous state: Chrome is still running
/// and Playwright still reports the browser as connected, but the socket underneath may be
/// dead. The health check would eventually notice, several failures later; the power event
/// tells us immediately and for free.
/// </para>
/// <para>
/// <see cref="SystemEvents"/> needs a message pump, which this application has, and raises its
/// events on its own thread - so handlers must be thread safe and must not touch the tray
/// directly.
/// </para>
/// </remarks>
internal sealed class SystemResumeMonitor : ISystemResumeMonitor, IDisposable
{
    private readonly ILogger<SystemResumeMonitor> _logger;
    private bool _disposed;

    public SystemResumeMonitor(ILogger<SystemResumeMonitor> logger)
    {
        _logger = logger;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    public event EventHandler? Resumed;

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode is not PowerModes.Resume)
        {
            return;
        }

        _logger.SystemResumed();
        Resumed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // SystemEvents holds a static handler list, so failing to detach here would keep this
        // instance - and everything it captures - alive for the life of the process.
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
    }
}
