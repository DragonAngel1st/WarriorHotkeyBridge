using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using WarriorHotkeyBridge.Commands;
using WarriorHotkeyBridge.Configuration;
using WarriorHotkeyBridge.Diagnostics;
using WarriorHotkeyBridge.Hotkeys;
using WarriorHotkeyBridge.Models;
using WarriorHotkeyBridge.Services;
using WarriorHotkeyBridge.Startup;

namespace WarriorHotkeyBridge.Tray;

/// <summary>
/// Owns the notification-area icon and its menu, and is the only component that renders state.
/// </summary>
internal sealed class TrayIconService : IDisposable
{
    /// <summary>
    /// Shell limit for a notification-area tooltip. Exceeding it throws, so text is truncated.
    /// </summary>
    private const int MaxTooltipLength = 63;

    /// <summary>
    /// The rehearsal the editor's Test button runs. Sends nothing, and raises the SIM window -
    /// see <see cref="HotkeyAction.ActivatesWindow"/> for why the raising is not optional.
    /// </summary>
    private static readonly HotkeyAction TargetingTest = new()
    {
        Kind = HotkeyActionKind.Test,
        Label = "from the hotkey editor",
    };

    private readonly IBridgeStateService _state;
    private readonly IUiDispatcher _ui;
    private readonly AppPaths _paths;
    private readonly IStartupManager _startup;
    private readonly IStartupPreferenceStore _preferences;
    private readonly TimeProvider _time;
    private readonly ILogger<TrayIconService> _logger;

    private readonly Dictionary<BridgeStatus, Icon> _icons = TrayIconFactory.CreateStatusIcons();

    private NotifyIcon? _notifyIcon;
    private ContextMenuStrip? _menu;
    private ToolStripMenuItem? _statusItem;
    private ToolStripMenuItem? _chromeItem;
    private ToolStripMenuItem? _warriorItem;
    private ToolStripMenuItem? _level2Item;
    private ToolStripMenuItem? _hotkeyItem;
    private ToolStripMenuItem? _lastErrorItem;
    private ToolStripMenuItem? _lastCommandItem;
    private ToolStripMenuItem? _startWithWindowsItem;
    private bool _disposed;

    /// <summary>
    /// Last error already shown as a balloon, so a persistent fault notifies once.
    /// </summary>
    /// <remarks>
    /// The watchdog re-reports the same condition every few seconds. Without this the operator
    /// would get a notification every three seconds for as long as Chrome stayed closed, which
    /// trains them to dismiss notifications without reading them.
    /// </remarks>
    private string? _notifiedError;

    private readonly IHotkeyBindingStore _bindings;
    private readonly IUserConfigurationWriter _configurationWriter;
    private readonly IHotkeyPresetProvider _presets;
    private readonly GlobalHotkeyService _hotkeys;
    private readonly CommandQueue _commands;

    /// <summary>The open editor, so a second click focuses it rather than opening another.</summary>
    private HotkeyEditorForm? _editor;

    public TrayIconService(
        IBridgeStateService state,
        IUiDispatcher ui,
        AppPaths paths,
        IStartupManager startup,
        IStartupPreferenceStore preferences,
        IHotkeyBindingStore bindings,
        IUserConfigurationWriter configurationWriter,
        IHotkeyPresetProvider presets,
        GlobalHotkeyService hotkeys,
        CommandQueue commands,
        TimeProvider time,
        ILogger<TrayIconService> logger)
    {
        _state = state;
        _ui = ui;
        _paths = paths;
        _startup = startup;
        _preferences = preferences;
        _bindings = bindings;
        _configurationWriter = configurationWriter;
        _presets = presets;
        _hotkeys = hotkeys;
        _commands = commands;
        _time = time;
        _logger = logger;
    }

    /// <summary>Raised when the user chooses Exit. Handled by the application context.</summary>
    public event EventHandler? ExitRequested;

    /// <summary>Raised when the user asks to rebuild the Chrome connection.</summary>
    public event EventHandler? ReconnectRequested;

    /// <summary>Raised when the user asks for a diagnostics report.</summary>
    public event EventHandler? DiagnosticsRequested;

    /// <summary>Creates the icon and menu. Must be called on the UI thread.</summary>
    public void Initialize()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _menu = BuildMenu();

        _notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = _menu,
            Visible = true,
        };

        // Double-click is the fastest route to the logs when something looks wrong.
        _notifyIcon.DoubleClick += (_, _) => OpenLogFolder();

        _state.Changed += OnStateChanged;

        Render(_state.Current);

        _logger.TrayIconCreated();
    }

    /// <summary>
    /// Shows a balloon notification. Reserved for faults the operator must know about; the
    /// successful command path never notifies.
    /// </summary>
    public void ShowError(string title, string message) =>
        _ui.Post(() => ShowBalloon(title, message, ToolTipIcon.Error));

    /// <summary>Shows a notification. Must be called on the UI thread.</summary>
    private void ShowBalloon(string title, string message, ToolTipIcon icon)
    {
        if (_notifyIcon is null)
        {
            return;
        }

        _notifyIcon.BalloonTipIcon = icon;
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = Truncate(message, 240);
        _notifyIcon.ShowBalloonTip(5_000);
    }

    /// <summary>Reports the outcome of a tray-initiated action.</summary>
    public void ShowInfo(string title, string message) =>
        _ui.Post(() => ShowBalloon(title, message, ToolTipIcon.Info));

    private ContextMenuStrip BuildMenu()
    {
        var header = new ToolStripMenuItem($"{AppInfo.ProductName} {AppInfo.DisplayVersion}")
        {
            Enabled = false,
            Font = new Font(SystemFonts.MenuFont ?? SystemFonts.DefaultFont, FontStyle.Bold),
        };

        _statusItem = CreateInfoItem();
        _chromeItem = CreateInfoItem();
        _warriorItem = CreateInfoItem();
        _level2Item = CreateInfoItem();
        _hotkeyItem = CreateInfoItem();
        _lastErrorItem = CreateInfoItem();
        _lastErrorItem.Available = false;

        _lastCommandItem = CreateInfoItem();
        _lastCommandItem.Available = false;

        _startWithWindowsItem = new ToolStripMenuItem("Start with Windows") { CheckOnClick = false };
        _startWithWindowsItem.Click += (_, _) => ToggleStartWithWindows();

        var configureHotkeys = new ToolStripMenuItem("Configure Hotkeys...");
        configureHotkeys.Click += (_, _) => ShowHotkeyEditor();

        var reconnect = new ToolStripMenuItem("Reconnect to Chrome");
        reconnect.Click += (_, _) => ReconnectRequested?.Invoke(this, EventArgs.Empty);

        var diagnostics = new ToolStripMenuItem("Run Diagnostics...");
        diagnostics.Click += (_, _) => DiagnosticsRequested?.Invoke(this, EventArgs.Empty);

        var openLogs = new ToolStripMenuItem("Open Log Folder");
        openLogs.Click += (_, _) => OpenLogFolder();

        var copyStatus = new ToolStripMenuItem("Copy Status to Clipboard");
        copyStatus.Click += (_, _) => CopyStatusToClipboard();

        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(
        [
            header,
            new ToolStripSeparator(),
            _statusItem,
            _chromeItem,
            _warriorItem,
            _level2Item,
            _hotkeyItem,
            _lastCommandItem,
            _lastErrorItem,
            new ToolStripSeparator(),
            configureHotkeys,
            _startWithWindowsItem,
            reconnect,
            diagnostics,
            openLogs,
            copyStatus,
            new ToolStripSeparator(),
            exit,
        ]);

        // Re-read on every open rather than caching: the user can change this in Task Manager's
        // Startup apps at any time, and a stale tick would claim the bridge starts at sign-in
        // when Windows has been told otherwise.
        menu.Opening += (_, _) => RefreshStartWithWindows();

        return menu;
    }

    /// <summary>A non-interactive menu row used purely to display state.</summary>
    private static ToolStripMenuItem CreateInfoItem() => new(string.Empty) { Enabled = false };

    /// <summary>
    /// Opens the mapping editor and, if the operator saves, persists and re-registers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Order matters here: the file is written before the keys are re-registered. If the write
    /// fails - a read-only file, a full disk - nothing is applied, so what the bridge is doing and
    /// what the file says never diverge. Re-registering first would leave the operator with keys
    /// that work until the next restart and then silently revert.
    /// </para>
    /// <para>
    /// Shown non-modally so the operator can keep using the SIM while editing, but tracked so a
    /// second click on the menu item brings the existing window forward rather than opening a
    /// second editor whose Save would overwrite the first.
    /// </para>
    /// </remarks>
    private void ShowHotkeyEditor()
    {
        if (_editor is { IsDisposed: false })
        {
            _editor.WindowState = FormWindowState.Normal;
            _editor.Activate();
            return;
        }

        var editor = new HotkeyEditorForm(
            _bindings.Current,
            _presets,
            _hotkeys.CaptureRegisteredPresses,
            () => _commands.EnqueueAsync(TargetingTest, "the hotkey editor's Test button"));

        _editor = editor;

        editor.FormClosed += (_, _) =>
        {
            _editor = null;

            if (editor.DialogResult is not DialogResult.OK)
            {
                editor.Dispose();
                return;
            }

            ApplyBindings(editor.Result);
            editor.Dispose();
        };

        editor.Show();
        editor.Activate();
    }

    private void ApplyBindings(IReadOnlyDictionary<string, HotkeyBindingConfig> bindings)
    {
        string? error = _configurationWriter.TryWriteBindings(bindings);

        if (error is not null)
        {
            _logger.HotkeyConfigurationSaveFailed(error);

            MessageBox.Show(
                $"Your hotkeys could not be saved, so nothing has been changed.\n\n{error}",
                AppInfo.ProductName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);

            return;
        }

        _bindings.Replace(bindings);

        // Already on the UI thread - this runs from a menu click - which is where Win32 requires
        // hotkey registration to happen.
        _hotkeys.Reapply();

        _logger.HotkeyConfigurationSaved(bindings.Count);
        ShowBalloon(AppInfo.ProductName, $"{bindings.Count} hotkey(s) applied.", ToolTipIcon.Info);
    }

    private void OnStateChanged(object? sender, BridgeStateChangedEventArgs e) =>
        // Always render the authoritative current snapshot rather than the event payload:
        // notifications from different threads may arrive out of order, but the newest state
        // is always the right thing to show.
        _ui.Post(() => Render(_state.Current));

    private void Render(BridgeState state)
    {
        if (_notifyIcon is null || _disposed)
        {
            return;
        }

        _notifyIcon.Icon = _icons[state.Status];
        _notifyIcon.Text = Truncate($"Warrior Bridge - {state.Status.Describe()}", MaxTooltipLength);

        _statusItem!.Text = $"Status:       {state.Status.Describe()}";
        _chromeItem!.Text = $"Chrome:       {state.Chrome.Describe()}";
        _warriorItem!.Text = $"Warrior SIM:  {state.WarriorPage.Describe()}";
        _level2Item!.Text = $"Level 2:      {state.Level2.Describe()}";
        _hotkeyItem!.Text = $"Hotkeys:      {state.Hotkeys.Describe()}";

        if (state.LastAction is null)
        {
            _lastCommandItem!.Available = false;
        }
        else
        {
            _lastCommandItem!.Available = true;
            string outcome = state.LastCommandResult?.ToString() ?? "?";
            string latency = state.LastCommandLatency is { } l ? $" {l.TotalMilliseconds:0}ms" : string.Empty;
            _lastCommandItem.Text = $"Last:         {Truncate(state.LastAction, 34)} [{outcome}{latency}]";
        }

        if (string.IsNullOrWhiteSpace(state.LastError))
        {
            _lastErrorItem!.Available = false;
            _notifiedError = null;
        }
        else
        {
            _lastErrorItem!.Available = true;
            _lastErrorItem.Text = $"Last Error:   {Truncate(state.LastError, 80)}";

            // Notify once per distinct fault, and only for a genuine error state - a yellow
            // "Chrome not connected yet" is visible on the icon and does not warrant interrupting.
            if (state.Status is BridgeStatus.Error
                && !string.Equals(state.LastError, _notifiedError, StringComparison.Ordinal))
            {
                _notifiedError = state.LastError;
                ShowBalloon("Warrior Hotkey Bridge", state.LastError, ToolTipIcon.Error);
            }
        }
    }

    private void RefreshStartWithWindows()
    {
        if (_startWithWindowsItem is null)
        {
            return;
        }

        StartupStatus status = _startup.GetStatus();

        _startWithWindowsItem.Checked = status.WillStartAtSignIn;
        _startWithWindowsItem.Enabled = status.State is not StartupState.Unknown;

        _startWithWindowsItem.Text = status.State switch
        {
            StartupState.BlockedByWindows => "Start with Windows (blocked in Task Manager)",
            StartupState.PointsElsewhere => "Start with Windows (points at another copy)",
            StartupState.Unknown => "Start with Windows (registry unavailable)",
            _ => "Start with Windows",
        };
    }

    private void ToggleStartWithWindows()
    {
        StartupStatus status = _startup.GetStatus();

        // Anything other than a clean "enabled" is repaired by writing our own value: that fixes
        // an entry left behind pointing at an old install location, which the user cannot
        // reasonably be expected to diagnose.
        bool enabling = status.State is not StartupState.Enabled;

        bool ok = enabling
            ? _startup.TryEnable(out string? error)
            : _startup.TryDisable(out error);

        if (!ok)
        {
            ShowBalloon("Warrior Hotkey Bridge", $"Could not change the startup setting: {error}", ToolTipIcon.Error);
            return;
        }

        // Recorded so the first-run registration never overrides a deliberate choice, including
        // after an upgrade.
        //
        // AppVersion has to be stamped here too, not just where the service writes it. Omitting
        // it leaves null, which the post-update offer reads as "recorded by a build that did not
        // track versions, so say nothing" - and the update immediately after the operator
        // switched startup off is precisely the one where being asked matters most.
        _preferences.Write(new StartupPreference
        {
            StartWithWindows = enabling,
            DecidedAt = _time.GetUtcNow(),
            AppVersion = AppInfo.Version,
        });

        RefreshStartWithWindows();

        if (status.State is StartupState.BlockedByWindows)
        {
            // Re-registering cannot override Task Manager; only the user can switch it back on.
            ShowBalloon(
                "Warrior Hotkey Bridge",
                "Startup is switched off for this app in Task Manager > Startup apps. Enable it there as well.",
                ToolTipIcon.Warning);
        }
    }

    private void OpenLogFolder()
    {
        try
        {
            // UseShellExecute is required to hand a directory to Explorer.
            using Process? _ = Process.Start(new ProcessStartInfo(_paths.Logs) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            _logger.LogFolderOpenFailed(ex, _paths.Logs);
        }
    }

    private void CopyStatusToClipboard()
    {
        string report = BuildStatusReport(_state.Current);

        try
        {
            Clipboard.SetText(report);
            _logger.StatusCopiedToClipboard();
        }
        catch (ExternalException ex)
        {
            // Another process can hold the clipboard open; this is transient and harmless.
            _logger.ClipboardUnavailable(ex);
        }
    }

    private string BuildStatusReport(BridgeState state)
    {
        var report = new StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture, $"{AppInfo.ProductName} {AppInfo.DisplayVersion}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Runtime:      {AppInfo.FrameworkDescription}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Status:       {state.Status.Describe()}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Chrome:       {state.Chrome.Describe()}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Warrior SIM:  {state.WarriorPage.Describe()}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Level 2:      {state.Level2.Describe()}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Hotkeys:      {state.Hotkeys.Describe()}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Logs:         {_paths.Logs}");

        if (!string.IsNullOrWhiteSpace(state.LastError))
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"Last Error:   {state.LastError}");
        }

        return report.ToString();
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : string.Concat(value.AsSpan(0, maxLength - 1), "...");

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _state.Changed -= OnStateChanged;

        if (_notifyIcon is not null)
        {
            // Hide before disposing, otherwise the icon can linger in the notification area
            // until the user hovers over it.
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }

        _menu?.Dispose();
        _menu = null;

        foreach (Icon icon in _icons.Values)
        {
            icon.Dispose();
        }

        _icons.Clear();
    }
}
