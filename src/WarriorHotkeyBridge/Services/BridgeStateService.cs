using Microsoft.Extensions.Logging;
using WarriorHotkeyBridge.Diagnostics;
using WarriorHotkeyBridge.Models;

namespace WarriorHotkeyBridge.Services;

/// <inheritdoc cref="IBridgeStateService"/>
internal sealed class BridgeStateService(ILogger<BridgeStateService> logger) : IBridgeStateService
{
    private readonly Lock _gate = new();
    private readonly ILogger<BridgeStateService> _logger = logger;

    private BridgeState _current = new();

    public BridgeState Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public event EventHandler<BridgeStateChangedEventArgs>? Changed;

    public BridgeState Update(Func<BridgeState, BridgeState> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        BridgeState previous;
        BridgeState next;

        lock (_gate)
        {
            previous = _current;
            next = mutate(previous);

            // Record value equality makes "nothing actually changed" free to detect, which
            // keeps a 3-second health check from repainting the tray forever.
            if (next == previous)
            {
                return previous;
            }

            _current = next;
        }

        // Raised outside the lock so a handler can never deadlock the state service.
        // Subscribers render from Current rather than from these arguments, so a reordered
        // pair of notifications can only cause a redundant repaint, never a stale one.
        // Guarded because building the summaries costs two string formats that would
        // otherwise be paid even when Debug logging is switched off.
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            string before = previous.ToLogSummary();
            string after = next.ToLogSummary();

            // The record can differ without the subsystem summary differing - a completed
            // command updates LastAction and LastCommandAt, for instance. Logging those as
            // "READY (...) -> READY (...)" with identical text is pure noise.
            if (!string.Equals(before, after, StringComparison.Ordinal))
            {
                _logger.StateChanged(before, after);
            }
        }

        Changed?.Invoke(this, new BridgeStateChangedEventArgs(previous, next));

        return next;
    }
}
