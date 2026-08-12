using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WarriorHotkeyBridge.Chrome;
using WarriorHotkeyBridge.Diagnostics;
using WarriorHotkeyBridge.Services;

namespace WarriorHotkeyBridge.Tray;

/// <summary>
/// The WinForms application context: a tray icon and a message loop, with no main window.
/// </summary>
/// <remarks>
/// Shutdown always flows the same direction - something calls
/// <see cref="IHostApplicationLifetime.StopApplication"/>, the resulting
/// <c>ApplicationStopping</c> token ends the message loop, and <c>Program</c> then stops the
/// host. Keeping one direction avoids the classic tray-app deadlock where the UI waits on
/// host shutdown while host shutdown waits on the UI.
/// </remarks>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly TrayIconService _tray;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly IUiDispatcher _ui;
    private readonly IChromeConnectionManager _chrome;
    private readonly IDiagnosticsService _diagnostics;
    private readonly ILogger<TrayApplicationContext> _logger;
    private readonly CancellationTokenRegistration _stoppingRegistration;

    public TrayApplicationContext(
        TrayIconService tray,
        IHostApplicationLifetime lifetime,
        IUiDispatcher ui,
        IChromeConnectionManager chrome,
        IDiagnosticsService diagnostics,
        ILogger<TrayApplicationContext> logger)
    {
        _tray = tray;
        _lifetime = lifetime;
        _ui = ui;
        _chrome = chrome;
        _diagnostics = diagnostics;
        _logger = logger;

        _tray.ExitRequested += OnExitRequested;
        _tray.ReconnectRequested += OnReconnectRequested;
        _tray.DiagnosticsRequested += OnDiagnosticsRequested;
        _tray.Initialize();

        _stoppingRegistration = _lifetime.ApplicationStopping.Register(OnApplicationStopping);
    }

    private void OnExitRequested(object? sender, EventArgs e)
    {
        _logger.ExitRequestedFromTray();
        _lifetime.StopApplication();
    }

    /// <summary>
    /// Drops the Chrome connection; the watchdog rebuilds it on its next pass.
    /// </summary>
    /// <remarks>
    /// Fire-and-forget because this runs on the UI thread from a menu click, and blocking the
    /// message loop on a network operation would freeze the tray and delay hotkeys.
    /// </remarks>
    private void OnReconnectRequested(object? sender, EventArgs e) =>
        _ = Task.Run(async () =>
        {
            _logger.ReconnectRequestedFromTray();
            await _chrome.DisconnectAsync().ConfigureAwait(false);
            _tray.ShowInfo("Warrior Hotkey Bridge", "Reconnecting to Chrome...");
        });

    private void OnDiagnosticsRequested(object? sender, EventArgs e) =>
        _ = Task.Run(async () =>
        {
            string? path = await _diagnostics.WriteReportAsync(CancellationToken.None).ConfigureAwait(false);

            if (path is null)
            {
                _tray.ShowError("Warrior Hotkey Bridge", "The diagnostics report could not be written.");
                return;
            }

            _logger.DiagnosticsWritten(path);

            // Opened for the operator rather than only logged: the point of the menu item is to
            // hand them something they can read and paste.
            try
            {
                using Process? _ = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
            {
                _tray.ShowInfo("Warrior Hotkey Bridge", $"Diagnostics written to {path}");
            }
        });

    private void OnApplicationStopping()
    {
        // The callback runs on whichever thread requested shutdown; ExitThread must happen on
        // the thread that owns the message loop.
        _ui.Post(() =>
        {
            _logger.EndingMessageLoop();
            ExitThread();
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tray.ExitRequested -= OnExitRequested;
            _tray.ReconnectRequested -= OnReconnectRequested;
            _tray.DiagnosticsRequested -= OnDiagnosticsRequested;
            _stoppingRegistration.Dispose();
            _tray.Dispose();
        }

        base.Dispose(disposing);
    }
}
