using Microsoft.Extensions.Logging;
using WarriorHotkeyBridge.Chrome;
using WarriorHotkeyBridge.Diagnostics;
using WarriorHotkeyBridge.Hotkeys;
using WarriorHotkeyBridge.Models;

namespace WarriorHotkeyBridge.Services;

internal interface ISessionController
{
    /// <summary>Whether the bridge is currently switched on.</summary>
    SessionState State { get; }

    /// <summary>Registers the hotkeys and brings Chrome up. Idempotent.</summary>
    Task ArmAsync(CancellationToken cancellationToken);

    /// <summary>Releases the hotkeys and closes Chrome. Idempotent.</summary>
    Task ParkAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The on/off switch: the single place that arms or parks a trading session.
/// </summary>
/// <remarks>
/// <para>
/// One component rather than a tray handler and a signal handler doing the same work, because
/// arming has an ordering requirement that is easy to get subtly wrong in a second copy: hotkeys
/// are registered before Chrome is launched, so the deck is live immediately rather than after a
/// browser has finished booting, and parking reverses that.
/// </para>
/// <para>
/// Hotkey registration is marshalled to the UI thread even though callers usually are one already.
/// Win32 binds a hotkey to the registering thread's window and delivers <c>WM_HOTKEY</c> only to
/// that thread's queue, so registering from the thread pool succeeds and then never delivers a
/// single keypress - a failure that looks exactly like the operator's deck being broken.
/// </para>
/// </remarks>
internal sealed class SessionController : ISessionController, IDisposable
{
    private readonly GlobalHotkeyService _hotkeys;
    private readonly IChromeLauncher _launcher;
    private readonly IChromeConnectionManager _chrome;
    private readonly IBridgeStateService _state;
    private readonly IUiDispatcher _ui;
    private readonly ILogger<SessionController> _logger;

    /// <summary>Serialises arm against park; both touch the hotkey table and the browser.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SessionController(
        GlobalHotkeyService hotkeys,
        IChromeLauncher launcher,
        IChromeConnectionManager chrome,
        IBridgeStateService state,
        IUiDispatcher ui,
        ILogger<SessionController> logger)
    {
        _hotkeys = hotkeys;
        _launcher = launcher;
        _chrome = chrome;
        _state = state;
        _ui = ui;
        _logger = logger;
    }

    public SessionState State => _state.Current.Session;

    public async Task ArmAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Not an error and not worth a second launch attempt: pressing a "go trading" button
            // twice is an ordinary thing to do, and the correct response is to carry on.
            if (State is SessionState.Armed)
            {
                _logger.SessionAlreadyArmed();
                return;
            }

            _logger.SessionArming();

            // Hotkeys first. They are the part the operator is waiting on, they cost microseconds,
            // and they must not be delayed behind a browser launch that can take seconds or fail.
            await _ui.InvokeAsync(_hotkeys.RegisterAll).ConfigureAwait(false);

            _state.Update(current => current with { Session = SessionState.Armed });

            // Launched after the state flips so the watchdog, which only maintains Chrome while
            // armed, takes over from here whatever this one call does.
            bool ready = await _launcher.LaunchOnRequestAsync(cancellationToken).ConfigureAwait(false);

            if (!ready)
            {
                // Not a failure to arm: the hotkeys are live either way. But it is the operator's
                // Start button appearing to do nothing, so it is recorded in the state as well as
                // the log - the tray is where they are looking, and the log is not.
                _logger.SessionChromeNotReady();

                _state.Update(current => current with
                {
                    LastError = "Chrome could not be started. Check that it is installed at the "
                        + "configured path; the log names the path that was tried.",
                });
            }

            _logger.SessionArmed();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ParkAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (State is SessionState.Parked)
            {
                _logger.SessionAlreadyParked();
                return;
            }

            _logger.SessionParking();

            // State first this time, and deliberately: it stops the watchdog relaunching Chrome
            // in the window between closing the browser and finishing this method. Reversing
            // these two is how a stop button would appear to do nothing.
            _state.Update(current => current with
            {
                Session = SessionState.Parked,
                Hotkeys = HotkeyState.Uninitialized,
                WarriorPage = WarriorPageState.Unknown,
                Level2 = Level2State.Unknown,

                // Cleared because it describes a session that no longer exists; leaving it would
                // put a stale fault next to an OFF icon.
                LastError = null,
            });

            await _ui.InvokeAsync(_hotkeys.UnregisterAll).ConfigureAwait(false);

            try
            {
                // A real CDP Browser.close. Playwright's CloseAsync only detaches on a CDP
                // connection, which would leave the operator's Chrome open and the stop button
                // looking broken.
                await _chrome.CloseBrowserAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Never let a browser that will not close keep the hotkeys held. Releasing them
                // is the half of parking that affects the rest of the machine.
                _logger.SessionChromeCloseFailed(ex.Message);
            }

            _logger.SessionParked();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
