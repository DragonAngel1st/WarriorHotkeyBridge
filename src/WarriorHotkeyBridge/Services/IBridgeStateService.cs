using WarriorHotkeyBridge.Models;

namespace WarriorHotkeyBridge.Services;

internal sealed class BridgeStateChangedEventArgs(BridgeState previous, BridgeState current) : EventArgs
{
    public BridgeState Previous { get; } = previous;

    public BridgeState Current { get; } = current;

    public bool StatusChanged => Previous.Status != Current.Status;
}

/// <summary>
/// The single owner of <see cref="BridgeState"/>.
/// </summary>
/// <remarks>
/// Services publish their own subsystem state here; the tray reads from here. Nothing writes
/// tray text directly, which keeps UI updates in one place and off the hot command path.
/// </remarks>
internal interface IBridgeStateService
{
    BridgeState Current { get; }

    event EventHandler<BridgeStateChangedEventArgs>? Changed;

    /// <summary>
    /// Atomically replaces the state.
    /// </summary>
    /// <param name="mutate">
    /// Pure function producing the next snapshot. It runs while an internal lock is held, so
    /// it must not block, raise events or call back into this service.
    /// </param>
    /// <returns>The resulting state.</returns>
    BridgeState Update(Func<BridgeState, BridgeState> mutate);
}
