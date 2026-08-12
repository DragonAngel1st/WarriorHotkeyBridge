using WarriorHotkeyBridge.Models;

namespace WarriorHotkeyBridge.Tests;

/// <summary>
/// The derived <see cref="BridgeState.Status"/> is what the operator reads off the tray icon
/// to decide whether a keypress will do anything, so every branch is pinned down here.
/// </summary>
public class BridgeStateTests
{
    private static BridgeState Ready() => new()
    {
        Application = ApplicationState.Running,
        Chrome = ChromeState.Connected,
        WarriorPage = WarriorPageState.Found,
        Level2 = Level2State.Ready,
        Hotkeys = HotkeyState.Registered,
    };

    [Fact]
    public void DefaultState_IsStarting() => Assert.Equal(BridgeStatus.Starting, new BridgeState().Status);

    [Fact]
    public void AllSubsystemsReady_IsReady() => Assert.Equal(BridgeStatus.Ready, Ready().Status);

    [Fact]
    public void ChromeNotConnected_IsWaitingForChrome()
    {
        foreach (ChromeState chrome in (ChromeState[])[ChromeState.Unknown, ChromeState.Disconnected, ChromeState.Connecting])
        {
            Assert.Equal(BridgeStatus.WaitingForChrome, (Ready() with { Chrome = chrome }).Status);
        }
    }

    [Fact]
    public void ChromeConnectedButPageMissing_IsDegraded() =>
        Assert.Equal(BridgeStatus.Degraded, (Ready() with { WarriorPage = WarriorPageState.NotFound }).Status);

    [Fact]
    public void ChromeConnectedButLevel2NotReady_IsDegraded() =>
        Assert.Equal(BridgeStatus.Degraded, (Ready() with { Level2 = Level2State.Found }).Status);

    [Fact]
    public void HotkeysOnlyPartiallyRegistered_IsDegraded() =>
        Assert.Equal(BridgeStatus.Degraded, (Ready() with { Hotkeys = HotkeyState.PartiallyRegistered }).Status);

    [Fact]
    public void ApplicationFaulted_IsError() =>
        Assert.Equal(BridgeStatus.Error, (Ready() with { Application = ApplicationState.Faulted }).Status);

    [Fact]
    public void ChromeFaulted_IsError() =>
        Assert.Equal(BridgeStatus.Error, (Ready() with { Chrome = ChromeState.Faulted }).Status);

    [Fact]
    public void HotkeyRegistrationFailed_IsError() =>
        Assert.Equal(BridgeStatus.Error, (Ready() with { Hotkeys = HotkeyState.Failed }).Status);

    /// <summary>
    /// A fault must win over "still starting", otherwise a startup failure would sit on a grey
    /// icon looking like normal initialisation.
    /// </summary>
    [Fact]
    public void FaultDuringStartup_ReportsErrorNotStarting()
    {
        var state = new BridgeState { Application = ApplicationState.Starting, Chrome = ChromeState.Faulted };

        Assert.Equal(BridgeStatus.Error, state.Status);
    }

    [Fact]
    public void StatesAreValueEqual_SoRedundantUpdatesCanBeSuppressed()
    {
        // BridgeStateService relies on this to avoid repainting the tray on every health check.
        Assert.Equal(Ready(), Ready());
        Assert.NotEqual(Ready(), Ready() with { Level2 = Level2State.NotFound });
    }

    [Fact]
    public void WithExpression_LeavesUnrelatedSubsystemsUntouched()
    {
        BridgeState updated = Ready() with { Chrome = ChromeState.Disconnected };

        Assert.Equal(ChromeState.Disconnected, updated.Chrome);
        Assert.Equal(HotkeyState.Registered, updated.Hotkeys);
        Assert.Equal(WarriorPageState.Found, updated.WarriorPage);
    }
}
