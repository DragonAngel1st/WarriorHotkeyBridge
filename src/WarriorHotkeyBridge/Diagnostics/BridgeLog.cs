using Microsoft.Extensions.Logging;

namespace WarriorHotkeyBridge.Diagnostics;

/// <summary>
/// Source-generated log messages.
/// </summary>
/// <remarks>
/// <para>
/// Every message the application emits through <see cref="ILogger"/> is declared here rather
/// than written inline. The <c>[LoggerMessage]</c> generator emits a strongly typed, cached
/// delegate per message with the level check inlined, so a disabled Debug message costs a
/// branch instead of an <c>object[]</c> allocation and a boxing conversion per argument.
/// That matters on the hotkey path, which is measured in milliseconds.
/// </para>
/// <para>
/// Event id ranges: 1xx services, 2xx tray, 3xx process-level faults, 4xx hotkeys. Later
/// phases add 5xx Chrome/CDP, 6xx Warrior page and Level 2, 7xx command execution.
/// </para>
/// </remarks>
internal static partial class BridgeLog
{
    [LoggerMessage(EventId = 100, Level = LogLevel.Debug, Message = "State changed: {Previous} -> {Next}")]
    public static partial void StateChanged(this ILogger logger, string previous, string next);

    [LoggerMessage(EventId = 110, Level = LogLevel.Debug, Message = "UI update dropped: dispatcher disposed (shutting down).")]
    public static partial void UiUpdateDroppedDisposed(this ILogger logger);

    [LoggerMessage(EventId = 111, Level = LogLevel.Debug, Message = "UI update dropped: message loop no longer running.")]
    public static partial void UiUpdateDroppedNoMessageLoop(this ILogger logger, Exception exception);

    [LoggerMessage(EventId = 200, Level = LogLevel.Information, Message = "Tray icon created.")]
    public static partial void TrayIconCreated(this ILogger logger);

    [LoggerMessage(EventId = 201, Level = LogLevel.Error, Message = "Could not open the log folder {LogFolder}.")]
    public static partial void LogFolderOpenFailed(this ILogger logger, Exception exception, string logFolder);

    [LoggerMessage(EventId = 202, Level = LogLevel.Information, Message = "Status copied to clipboard.")]
    public static partial void StatusCopiedToClipboard(this ILogger logger);

    [LoggerMessage(EventId = 204, Level = LogLevel.Information, Message = "Reconnect requested from the tray menu.")]
    public static partial void ReconnectRequestedFromTray(this ILogger logger);

    [LoggerMessage(EventId = 205, Level = LogLevel.Information, Message = "[DIAG] Diagnostics report written to {Path}")]
    public static partial void DiagnosticsWritten(this ILogger logger, string path);

    [LoggerMessage(EventId = 206, Level = LogLevel.Information, Message = "[STARTUP] Start with Windows enabled for {Executable}")]
    public static partial void StartupEnabled(this ILogger logger, string executable);

    [LoggerMessage(EventId = 207, Level = LogLevel.Information, Message = "[STARTUP] Start with Windows disabled.")]
    public static partial void StartupDisabled(this ILogger logger);

    [LoggerMessage(EventId = 208, Level = LogLevel.Warning, Message = "[STARTUP] Startup registry access failed: {Reason}")]
    public static partial void StartupRegistryFailed(this ILogger logger, string reason);

    [LoggerMessage(EventId = 209, Level = LogLevel.Information, Message = "[STARTUP] First run: enabled Start with Windows. Turn it off from the tray if you would rather launch it yourself.")]
    public static partial void StartupAutoEnabled(this ILogger logger);

    [LoggerMessage(EventId = 210, Level = LogLevel.Information, Message = "[STARTUP] Repaired a startup entry that pointed at {OldCommand}")]
    public static partial void StartupRepaired(this ILogger logger, string oldCommand);

    [LoggerMessage(EventId = 213, Level = LogLevel.Information, Message = "[STARTUP] Start with Windows was recorded as on but no registration was present; restored it.")]
    public static partial void StartupRestored(this ILogger logger);

    [LoggerMessage(EventId = 214, Level = LogLevel.Warning, Message = "[STARTUP] The startup preference file exists but could not be read; leaving startup untouched rather than guessing. Use the tray toggle to set it again.")]
    public static partial void StartupPreferenceUnreadable(this ILogger logger);

    [LoggerMessage(EventId = 215, Level = LogLevel.Information, Message = "[HOTKEY] Saved and applied {Count} hotkey binding(s) from the editor.")]
    public static partial void HotkeyConfigurationSaved(this ILogger logger, int count);

    [LoggerMessage(EventId = 216, Level = LogLevel.Error, Message = "[HOTKEY] Could not save the hotkey configuration; nothing was applied: {Reason}")]
    public static partial void HotkeyConfigurationSaveFailed(this ILogger logger, string reason);

    [LoggerMessage(EventId = 211, Level = LogLevel.Information, Message = "[STARTUP] Updated from {PreviousVersion} to {CurrentVersion} with Start with Windows off; asking whether to re-enable it.")]
    public static partial void StartupOfferingAfterUpdate(this ILogger logger, string previousVersion, string currentVersion);

    [LoggerMessage(EventId = 212, Level = LogLevel.Information, Message = "[STARTUP] Post-update prompt answered: re-enable = {ReEnabled}.")]
    public static partial void StartupOfferAnswered(this ILogger logger, bool reEnabled);

    [LoggerMessage(EventId = 203, Level = LogLevel.Warning, Message = "Clipboard was unavailable; status not copied.")]
    public static partial void ClipboardUnavailable(this ILogger logger, Exception exception);

    [LoggerMessage(EventId = 210, Level = LogLevel.Information, Message = "Exit requested from the tray menu.")]
    public static partial void ExitRequestedFromTray(this ILogger logger);

    [LoggerMessage(EventId = 211, Level = LogLevel.Debug, Message = "Ending the WinForms message loop.")]
    public static partial void EndingMessageLoop(this ILogger logger);

    [LoggerMessage(EventId = 300, Level = LogLevel.Error, Message = "Unhandled exception on the UI thread.")]
    public static partial void UiThreadException(this ILogger logger, Exception exception);

    [LoggerMessage(EventId = 301, Level = LogLevel.Error, Message = "Unobserved task exception.")]
    public static partial void UnobservedTaskException(this ILogger logger, Exception exception);

    [LoggerMessage(EventId = 400, Level = LogLevel.Warning, Message = "Hotkey configuration problem: {Problem}")]
    public static partial void HotkeyConfigurationProblem(this ILogger logger, string problem);

    [LoggerMessage(EventId = 401, Level = LogLevel.Information, Message = "[HOTKEY] Registered {Gesture} -> {Action}")]
    public static partial void HotkeyRegistered(this ILogger logger, string gesture, string action);

    [LoggerMessage(EventId = 402, Level = LogLevel.Error, Message = "[HOTKEY] Could not register {Gesture}: {Error}")]
    public static partial void HotkeyRegistrationFailed(this ILogger logger, string gesture, string error);

    [LoggerMessage(EventId = 403, Level = LogLevel.Error, Message = "[HOTKEY] {Reason}")]
    public static partial void HotkeyRegistrationFailedEntirely(this ILogger logger, string reason);

    [LoggerMessage(EventId = 404, Level = LogLevel.Information, Message = "[HOTKEY] {Succeeded} of {Attempted} hotkeys registered.")]
    public static partial void HotkeyRegistrationSummary(this ILogger logger, int succeeded, int attempted);

    /// <remarks>
    /// Starts with a blank line and a rule on purpose. This is the line an operator scans for
    /// while watching a live console, and it needs to be findable at a glance among the
    /// background health-check chatter.
    /// </remarks>
    [LoggerMessage(EventId = 405, Level = LogLevel.Information, Message = "\n>>>>>>>>>> HOTKEY {Gesture} -> {Action}")]
    public static partial void HotkeyReceived(this ILogger logger, string gesture, string action);

    [LoggerMessage(EventId = 406, Level = LogLevel.Warning, Message = "[HOTKEY] Received unknown hotkey id {HotkeyId}.")]
    public static partial void HotkeyUnknownId(this ILogger logger, int hotkeyId);

    [LoggerMessage(EventId = 407, Level = LogLevel.Warning, Message = "[HOTKEY] Could not release {Gesture}: {Error}")]
    public static partial void HotkeyUnregisterFailed(this ILogger logger, string gesture, string error);

    [LoggerMessage(EventId = 408, Level = LogLevel.Information, Message = "[HOTKEY] All hotkeys released.")]
    public static partial void HotkeysUnregistered(this ILogger logger);

    [LoggerMessage(EventId = 409, Level = LogLevel.Information, Message = "[HOTKEY] Reclaimed {Gesture} -> {Action}; the application holding it has gone.")]
    public static partial void HotkeyReclaimed(this ILogger logger, string gesture, string action);

    [LoggerMessage(EventId = 410, Level = LogLevel.Information, Message = "[POWER] System resumed from sleep; revalidating the Chrome connection and hotkeys.")]
    public static partial void SystemResumed(this ILogger logger);

    [LoggerMessage(EventId = 411, Level = LogLevel.Warning, Message = "[CHROME] {Failures} consecutive health checks failed; the connection looks dead, reconnecting.")]
    public static partial void ChromeConnectionPresumedDead(this ILogger logger, int failures);

    [LoggerMessage(EventId = 500, Level = LogLevel.Information, Message = "[CHROME] Connecting to CDP endpoint {Endpoint}")]
    public static partial void ChromeConnecting(this ILogger logger, string endpoint);

    [LoggerMessage(EventId = 501, Level = LogLevel.Information, Message = "[CHROME] Connected to {Version}; {ContextCount} browser context(s).")]
    public static partial void ChromeConnected(this ILogger logger, string version, int contextCount);

    [LoggerMessage(EventId = 502, Level = LogLevel.Warning, Message = "[CHROME] Not reachable at {Endpoint}: {Reason}")]
    public static partial void ChromeConnectFailed(this ILogger logger, string endpoint, string reason);

    [LoggerMessage(EventId = 503, Level = LogLevel.Error, Message = "[CHROME] Unexpected failure while connecting.")]
    public static partial void ChromeConnectFaulted(this ILogger logger, Exception exception);

    [LoggerMessage(EventId = 504, Level = LogLevel.Warning, Message = "[CHROME] DevTools connection lost.")]
    public static partial void ChromeDisconnected(this ILogger logger);

    [LoggerMessage(EventId = 505, Level = LogLevel.Debug, Message = "[CHROME] Releasing the old connection reported: {Reason}")]
    public static partial void ChromeDiscardFailed(this ILogger logger, string reason);

    [LoggerMessage(EventId = 506, Level = LogLevel.Information, Message = "[CHROME] Retrying in {Seconds:0.0}s (attempt {Attempt}).")]
    public static partial void ChromeReconnectScheduled(this ILogger logger, double seconds, int attempt);

    [LoggerMessage(EventId = 507, Level = LogLevel.Information, Message = "[CHROME] Launching the dedicated Chrome instance with profile {Profile}")]
    public static partial void ChromeLaunching(this ILogger logger, string profile);

    [LoggerMessage(EventId = 508, Level = LogLevel.Warning, Message = "[CHROME] Could not launch Chrome: {Reason}")]
    public static partial void ChromeLaunchFailed(this ILogger logger, string reason);

    [LoggerMessage(EventId = 509, Level = LogLevel.Information, Message = "[CHROME] Closed the dedicated Chrome instance as requested.")]
    public static partial void ChromeClosedByRequest(this ILogger logger);

    [LoggerMessage(EventId = 600, Level = LogLevel.Debug, Message = "[PAGE] Examined {Count} page(s).")]
    public static partial void WarriorPageCandidateCount(this ILogger logger, int count);

    [LoggerMessage(EventId = 601, Level = LogLevel.Debug, Message = "[PAGE]   {Candidate}")]
    public static partial void WarriorPageCandidate(this ILogger logger, string candidate);

    [LoggerMessage(EventId = 602, Level = LogLevel.Information, Message = "[PAGE] Warrior SIM identified: {Host}{Path} \"{Title}\"")]
    public static partial void WarriorPageFound(this ILogger logger, string host, string path, string title);

    [LoggerMessage(EventId = 603, Level = LogLevel.Warning, Message = "[PAGE] Not found: {Reason}")]
    public static partial void WarriorPageNotFound(this ILogger logger, string reason);

    [LoggerMessage(EventId = 604, Level = LogLevel.Warning, Message = "[PAGE] {Count} pages passed validation; selected {Selected}")]
    public static partial void WarriorPageAmbiguous(this ILogger logger, int count, string selected);

    [LoggerMessage(EventId = 605, Level = LogLevel.Debug, Message = "[PAGE] Skipped a page that could not be inspected: {Reason}")]
    public static partial void WarriorPageInspectFailed(this ILogger logger, string reason);

    [LoggerMessage(EventId = 700, Level = LogLevel.Debug, Message = "[LEVEL2] Selector '{Selector}' could not be evaluated: {Reason}")]
    public static partial void Level2SelectorFailed(this ILogger logger, string selector, string reason);

    /// <remarks>
    /// Debug, not Warning: a page that legitimately has no Level 2 - a scanner or alert popout -
    /// hits this on every health check, and warning about an expected condition every few
    /// seconds trains the operator to ignore the log.
    /// </remarks>
    [LoggerMessage(EventId = 701, Level = LogLevel.Debug, Message = "[LEVEL2] '{Selector}' matched tabs but none contain \"{Expected}\" (e.g. {Found}); no Level 2 on this page.")]
    public static partial void Level2TextMismatch(this ILogger logger, string selector, string expected, string found);

    [LoggerMessage(EventId = 702, Level = LogLevel.Debug, Message = "[LEVEL2] Selected the Level 2 tab matched by '{Selector}' ({Mode} click).")]
    public static partial void Level2Selecting(this ILogger logger, string selector, string mode);

    [LoggerMessage(EventId = 703, Level = LogLevel.Debug, Message = "[LEVEL2] No FlexLayout tab bar; treating Level 2 as selected (popped-out layout).")]
    public static partial void Level2NoTabBar(this ILogger logger);

    [LoggerMessage(EventId = 704, Level = LogLevel.Information, Message = "[LEVEL2] Ready via '{Selector}' (selected={Selected}, panels={Panels}).")]
    public static partial void Level2Ready(this ILogger logger, string selector, bool selected, int panels);

    [LoggerMessage(EventId = 705, Level = LogLevel.Warning, Message = "[LEVEL2] Not ready: {Reason}")]
    public static partial void Level2NotReady(this ILogger logger, string reason);

    [LoggerMessage(EventId = 800, Level = LogLevel.Debug, Message = "[QUEUE] {Depth} commands waiting.")]
    public static partial void CommandQueueBacklog(this ILogger logger, int depth);

    [LoggerMessage(EventId = 801, Level = LogLevel.Information, Message = "<<<<<<<<<< OK      {Action} - {Timings}\n")]
    public static partial void CommandSucceeded(this ILogger logger, string action, string timings);

    [LoggerMessage(EventId = 802, Level = LogLevel.Error, Message = "<<<<<<<<<< FAILED  {Action}: {Reason} - {Timings}\n")]
    public static partial void CommandFailed(this ILogger logger, string action, string reason, string timings);

    [LoggerMessage(EventId = 803, Level = LogLevel.Warning, Message = "[COMMAND] {Action} not executed: {Reason}")]
    public static partial void CommandRejected(this ILogger logger, string action, string reason);

    [LoggerMessage(EventId = 804, Level = LogLevel.Debug, Message = "[COMMAND] Preparation attempt {Attempt} failed ({Reason}); retrying.")]
    public static partial void CommandPreparationRetry(this ILogger logger, int attempt, string reason);

    [LoggerMessage(EventId = 805, Level = LogLevel.Information, Message = "[KEYBOARD] Dispatched {Keys}")]
    public static partial void CommandChordDispatched(this ILogger logger, string keys);

    [LoggerMessage(EventId = 806, Level = LogLevel.Error, Message = "[KEYBOARD] {Action} reported {Reason}. It may already have been delivered, so it will NOT be retried automatically.")]
    public static partial void CommandDispatchUncertain(this ILogger logger, string action, string reason);

    [LoggerMessage(EventId = 807, Level = LogLevel.Warning, Message = "[WINDOW] Could not raise the Chrome window; the tab was activated but Chrome may stay behind other apps.")]
    public static partial void CommandWindowNotRaised(this ILogger logger);

    [LoggerMessage(EventId = 810, Level = LogLevel.Warning, Message = "[WINDOW] Could not identify the Chrome window for the SIM tab.")]
    public static partial void ChromeWindowNotResolved(this ILogger logger);

    [LoggerMessage(EventId = 811, Level = LogLevel.Warning, Message = "[WINDOW] SetForegroundWindow refused (Win32 error {Error}).")]
    public static partial void ChromeWindowRaiseFailed(this ILogger logger, int error);

    [LoggerMessage(EventId = 812, Level = LogLevel.Debug, Message = "[WINDOW] Window bounds unavailable ({Reason}); falling back to title matching.")]
    public static partial void ChromeWindowBoundsUnavailable(this ILogger logger, string reason);

    [LoggerMessage(EventId = 813, Level = LogLevel.Warning, Message = "[HOTKEY] {Warning}")]
    public static partial void HotkeyKeyExpressionAmbiguous(this ILogger logger, string warning);

    [LoggerMessage(EventId = 814, Level = LogLevel.Debug, Message = "[WINDOW]   hwnd=0x{Window:X} rect=({Left},{Top} {Width}x{Height}) scale={Scale:0.##} size={SizeMismatch:0.#} origin={OriginMismatch:0.#}")]
    public static partial void ChromeWindowCandidate(this ILogger logger, nint window, int left, int top, int width, int height, double scale, double sizeMismatch, double originMismatch);

    [LoggerMessage(EventId = 815, Level = LogLevel.Warning, Message = "[WINDOW] {Count} Chrome windows match the SIM tab's size equally well; refusing to guess which to raise.")]
    public static partial void ChromeWindowAmbiguous(this ILogger logger, int count);

    [LoggerMessage(EventId = 706, Level = LogLevel.Debug, Message = "[LEVEL2] Could not read the owning tabset's state ({Reason}); treating it as inactive so the tab gets clicked.")]
    public static partial void Level2TabsetCheckFailed(this ILogger logger, string reason);

    [LoggerMessage(EventId = 707, Level = LogLevel.Warning, Message = "[HEALTH] Health check could not read the page ({Reason}); will retry.")]
    public static partial void HealthCheckFailed(this ILogger logger, string reason);

    [LoggerMessage(EventId = 808, Level = LogLevel.Warning, Message = "[COMMAND] {Action} discarded: it waited {AgeMs:0}ms, longer than the {LimitMs}ms freshness limit.")]
    public static partial void CommandTooOld(this ILogger logger, string action, double ageMs, int limitMs);
}
