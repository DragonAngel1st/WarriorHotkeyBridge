namespace WarriorHotkeyBridge.Models;

internal enum ApplicationState
{
    Starting,
    Running,
    ShuttingDown,
    Faulted,
}

internal enum ChromeState
{
    Unknown,
    Disconnected,
    Connecting,
    Connected,
    Faulted,
}

internal enum WarriorPageState
{
    Unknown,
    NotFound,
    Found,
}

internal enum Level2State
{
    Unknown,
    NotFound,
    Found,
    Ready,
}

internal enum HotkeyState
{
    Uninitialized,
    Initializing,
    Registered,
    PartiallyRegistered,
    Failed,
}

internal enum CommandOutcome
{
    Succeeded,
    Failed,
    Rejected,
}

/// <summary>
/// Roll-up of the individual subsystem states. This is what drives the tray icon colour and
/// tooltip, so the operator can tell at a glance whether a keypress would do anything.
/// </summary>
internal enum BridgeStatus
{
    /// <summary>Grey - still starting up.</summary>
    Starting,

    /// <summary>Grey - alive, but Chrome is not connected.</summary>
    WaitingForChrome,

    /// <summary>Yellow - Chrome connected, but the Warrior page or Level 2 is not ready.</summary>
    Degraded,

    /// <summary>Green - every subsystem is ready; a hotkey will execute.</summary>
    Ready,

    /// <summary>Red - a subsystem has faulted.</summary>
    Error,
}

/// <summary>
/// Immutable snapshot of everything the bridge knows about itself.
/// </summary>
/// <remarks>
/// Being a record makes each published snapshot inherently thread safe: readers on the UI
/// thread never observe a half-updated object, and value equality lets the state service
/// suppress no-op change notifications for free.
/// </remarks>
internal sealed record BridgeState
{
    public ApplicationState Application { get; init; } = ApplicationState.Starting;

    public ChromeState Chrome { get; init; } = ChromeState.Unknown;

    public WarriorPageState WarriorPage { get; init; } = WarriorPageState.Unknown;

    public Level2State Level2 { get; init; } = Level2State.Unknown;

    public HotkeyState Hotkeys { get; init; } = HotkeyState.Uninitialized;

    /// <summary>Description of the most recent action, e.g. <c>Shift+1 (Buy 75% BP)</c>.</summary>
    public string? LastAction { get; init; }

    public CommandOutcome? LastCommandResult { get; init; }

    public TimeSpan? LastCommandLatency { get; init; }

    public DateTimeOffset? LastCommandAt { get; init; }

    /// <summary>Most recent error text, retained for the tray menu. Never contains page content.</summary>
    public string? LastError { get; init; }

    public BridgeStatus Status
    {
        get
        {
            if (Application is ApplicationState.Faulted
                || Chrome is ChromeState.Faulted
                || Hotkeys is HotkeyState.Failed)
            {
                return BridgeStatus.Error;
            }

            if (Application is ApplicationState.Starting)
            {
                return BridgeStatus.Starting;
            }

            if (Chrome is not ChromeState.Connected)
            {
                return BridgeStatus.WaitingForChrome;
            }

            return WarriorPage is WarriorPageState.Found
                && Level2 is Level2State.Ready
                && Hotkeys is HotkeyState.Registered
                    ? BridgeStatus.Ready
                    : BridgeStatus.Degraded;
        }
    }
}

internal static class BridgeStateDescriptions
{
    public static string Describe(this BridgeStatus status) => status switch
    {
        BridgeStatus.Starting => "STARTING",
        BridgeStatus.WaitingForChrome => "WAITING FOR CHROME",
        BridgeStatus.Degraded => "DEGRADED",
        BridgeStatus.Ready => "READY",
        BridgeStatus.Error => "ERROR",
        _ => status.ToString().ToUpperInvariant(),
    };

    public static string Describe(this ChromeState state) => state switch
    {
        ChromeState.Unknown => "Unknown",
        ChromeState.Disconnected => "Disconnected",
        ChromeState.Connecting => "Connecting",
        ChromeState.Connected => "Connected",
        ChromeState.Faulted => "Faulted",
        _ => state.ToString(),
    };

    public static string Describe(this WarriorPageState state) => state switch
    {
        WarriorPageState.Unknown => "Unknown",
        WarriorPageState.NotFound => "Not Found",
        WarriorPageState.Found => "Found",
        _ => state.ToString(),
    };

    public static string Describe(this Level2State state) => state switch
    {
        Level2State.Unknown => "Unknown",
        Level2State.NotFound => "Not Found",
        Level2State.Found => "Found",
        Level2State.Ready => "Ready",
        _ => state.ToString(),
    };

    public static string Describe(this HotkeyState state) => state switch
    {
        HotkeyState.Uninitialized => "Not Registered",
        HotkeyState.Initializing => "Initializing",
        HotkeyState.Registered => "Registered",
        HotkeyState.PartiallyRegistered => "Partially Registered",
        HotkeyState.Failed => "Failed",
        _ => state.ToString(),
    };

    /// <summary>Compact single-line form used in log messages.</summary>
    public static string ToLogSummary(this BridgeState state) =>
        $"{state.Status.Describe()} (chrome={state.Chrome}, page={state.WarriorPage}, level2={state.Level2}, hotkeys={state.Hotkeys})";
}
