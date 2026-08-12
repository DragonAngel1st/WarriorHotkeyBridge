using Microsoft.Playwright;
using WarriorHotkeyBridge.Models;

namespace WarriorHotkeyBridge.Chrome;

internal sealed class ChromeStateChangedEventArgs(ChromeState previous, ChromeState current, string? reason) : EventArgs
{
    public ChromeState Previous { get; } = previous;

    public ChromeState Current { get; } = current;

    /// <summary>Human-readable cause, for logs and the tray. Never contains page content.</summary>
    public string? Reason { get; } = reason;
}

/// <summary>
/// Owns the single, long-lived Playwright connection to the operator's Chrome.
/// </summary>
/// <remarks>
/// The connection is established once and kept warm for the life of the process. A hotkey must
/// never start Playwright, launch a process or open a socket - that is the difference between
/// a command completing in tens of milliseconds and in seconds.
/// </remarks>
internal interface IChromeConnectionManager : IAsyncDisposable
{
    ChromeState State { get; }

    /// <summary>
    /// The live browser, or null when not connected. Callers must re-read this rather than
    /// caching it: it is replaced whenever Chrome restarts.
    /// </summary>
    IBrowser? Browser { get; }

    event EventHandler<ChromeStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Connects if not already connected. Concurrent callers share one attempt rather than
    /// opening competing connections.
    /// </summary>
    /// <returns>True when the browser is usable on return.</returns>
    Task<bool> EnsureConnectedAsync(CancellationToken cancellationToken);

    /// <summary>Drops the connection without disturbing Chrome itself.</summary>
    Task DisconnectAsync();

    /// <summary>
    /// Asks the dedicated Chrome instance to close, then drops the connection.
    /// </summary>
    /// <remarks>
    /// The one place the bridge deliberately shuts Chrome down, and only on explicit request -
    /// a Stream Deck "stop trading" button. Everything else treats the browser as the operator's
    /// to own. Uses the live connection, so it closes exactly the instance the bridge is
    /// attached to and can never reach any other Chrome the operator has open.
    /// </remarks>
    Task CloseBrowserAsync();
}
