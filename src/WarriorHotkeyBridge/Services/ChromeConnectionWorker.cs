using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using WarriorHotkeyBridge.Chrome;
using WarriorHotkeyBridge.Configuration;
using WarriorHotkeyBridge.Diagnostics;
using WarriorHotkeyBridge.Models;
using WarriorHotkeyBridge.Warrior;

namespace WarriorHotkeyBridge.Services;

/// <summary>
/// Keeps the Chrome connection warm and the Warrior page located.
/// </summary>
/// <remarks>
/// <para>
/// Runs on the thread pool, never on the UI thread, so a slow or unreachable Chrome can never
/// stall the message loop and therefore never delay a keypress.
/// </para>
/// <para>
/// The loop deliberately re-locates on a timer rather than watching the DOM: the operator can
/// reload the SIM, rearrange the layout or open a second tab at any moment, and a few seconds
/// of staleness costs nothing because the command path re-validates before dispatching anyway.
/// </para>
/// </remarks>
internal sealed class ChromeConnectionWorker : BackgroundService
{
    private readonly IChromeConnectionManager _chrome;
    private readonly IWarriorPageLocator _locator;
    private readonly ILevel2Controller _level2;
    private readonly IBridgeStateService _state;
    private readonly ISystemResumeMonitor _resume;
    private readonly IChromeLauncher _launcher;
    private readonly ChromeOptions _options;
    private readonly ILogger<ChromeConnectionWorker> _logger;

    /// <summary>
    /// Last observation logged per category, so an unchanged world stays quiet.
    /// </summary>
    /// <remarks>
    /// Keyed by category rather than held as one field: the health check reports on the page
    /// and on Level 2 in the same pass, and a single slot would have each overwriting the
    /// other's signature so neither ever matched and both logged every time.
    /// </remarks>
    private readonly Dictionary<string, string> _lastReported = [];

    /// <summary>Health checks that have failed back to back; reset by any success.</summary>
    private int _consecutiveHealthFailures;

    public ChromeConnectionWorker(
        IChromeConnectionManager chrome,
        IWarriorPageLocator locator,
        ILevel2Controller level2,
        IBridgeStateService state,
        ISystemResumeMonitor resume,
        IChromeLauncher launcher,
        IOptions<ChromeOptions> options,
        ILogger<ChromeConnectionWorker> logger)
    {
        _chrome = chrome;
        _locator = locator;
        _level2 = level2;
        _state = state;
        _resume = resume;
        _launcher = launcher;
        _options = options.Value;
        _logger = logger;

        // Surfaces a dropped connection immediately rather than at the next health check, so
        // the tray goes grey the moment Chrome closes.
        _chrome.StateChanged += OnChromeStateChanged;

        _resume.Resumed += OnSystemResumed;
    }

    /// <summary>
    /// Forces a reconnect after the machine wakes.
    /// </summary>
    /// <remarks>
    /// Fire and forget on purpose: this runs on the SystemEvents thread, which must not be
    /// blocked. Dropping the connection is enough - the worker loop reconnects on its next
    /// pass, with the existing backoff if Chrome is not back yet.
    /// </remarks>
    private void OnSystemResumed(object? sender, EventArgs e) =>
        _ = Task.Run(async () =>
        {
            try
            {
                await _chrome.DisconnectAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is PlaywrightException or ObjectDisposedException)
            {
                _logger.HealthCheckFailed(ex.Message);
            }
        });

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var backoff = new ExponentialBackoff(_options.ReconnectInitialDelay, _options.ReconnectMaxDelay);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // Only from the watchdog, never from the command path: a keypress must not wait
                // for a browser to boot.
                await _launcher.EnsureRunningAsync(stoppingToken).ConfigureAwait(false);

                if (!await _chrome.EnsureConnectedAsync(stoppingToken).ConfigureAwait(false))
                {
                    _state.Update(current => current with
                    {
                        Chrome = _chrome.State,
                        WarriorPage = WarriorPageState.Unknown,
                        Level2 = Level2State.Unknown,
                    });

                    TimeSpan delay = backoff.NextDelay();
                    _logger.ChromeReconnectScheduled(delay.TotalSeconds, backoff.Attempt);
                    await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                backoff.Reset();

                await LocateAsync(stoppingToken).ConfigureAwait(false);

                await Task.Delay(_options.HealthCheckInterval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    /// <summary>
    /// One health-check pass.
    /// </summary>
    /// <remarks>
    /// Every Playwright call here is guarded. This runs inside a <see cref="BackgroundService"/>,
    /// and an escaping exception would fault the service, stop the host and take the tray icon
    /// with it — so a routine page reload during a 3-second poll would silently kill the whole
    /// bridge mid-session. Degrading is always the right answer here; dying never is.
    /// </remarks>
    private async Task LocateAsync(CancellationToken cancellationToken)
    {
        try
        {
            await LocateCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
            _logger.HealthCheckFailed(ex.Message);

            _state.Update(current => current with
            {
                Chrome = _chrome.State,
                WarriorPage = WarriorPageState.Unknown,
                Level2 = Level2State.Unknown,
            });

            await UpdateConnectionHealthAsync(probeFailed: true).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Tracks whether the page is still answering, and rebuilds the connection when it is not.
    /// </summary>
    /// <remarks>
    /// Chrome can report the browser as connected while nothing actually gets through - after a
    /// resume, or when something drops an idle socket. <c>EnsureConnectedAsync</c> trusts
    /// <c>IsConnected</c>, so without an explicit liveness rule the bridge would consider itself
    /// connected forever and never reconnect.
    /// </remarks>
    private async Task UpdateConnectionHealthAsync(bool probeFailed)
    {
        if (!probeFailed)
        {
            _consecutiveHealthFailures = 0;
            return;
        }

        if (++_consecutiveHealthFailures < _options.HealthFailuresBeforeReconnect)
        {
            return;
        }

        _logger.ChromeConnectionPresumedDead(_consecutiveHealthFailures);
        _consecutiveHealthFailures = 0;
        await _chrome.DisconnectAsync().ConfigureAwait(false);
    }

    private async Task LocateCoreAsync(CancellationToken cancellationToken)
    {
        WarriorPageResult result = await _locator.LocateAsync(cancellationToken).ConfigureAwait(false);

        // Health is judged on whether a probe actually ran, NOT on whether an exception escaped.
        // Every Playwright failure below here is deliberately converted into an ordinary
        // negative result so a routine page reload cannot fault this service - which means an
        // exception can never reach the catch, and a counter driven from there could never fire.
        await UpdateConnectionHealthAsync(result.ProbeFailed).ConfigureAwait(false);

        switch (result.Status)
        {
            case WarriorPageStatus.PageFound:
                WarriorPageCandidate selected = result.Candidates.First(c => c.IsEligible);
                ReportOnce("page", $"{selected.Host}{selected.Path}",
                    () => _logger.WarriorPageFound(selected.Host, selected.Path, selected.Title));

                // Reported without selecting anything: the watchdog observes, it never clicks.
                // Selecting Level 2 is a side effect on the operator's live trading screen and
                // belongs only on the command path, where they asked for it by pressing a key.
                Level2Result level2 = await _level2
                    .LocateAsync(result.Page!, index: 0, cancellationToken)
                    .ConfigureAwait(false);

                if (level2.Status is Level2Status.Ready or Level2Status.Found)
                {
                    ReportOnce("level2", $"{level2.MatchedSelector}:{level2.IsSelected}:{level2.MatchCount}",
                        () => _logger.Level2Ready(level2.MatchedSelector ?? "?", level2.IsSelected, level2.MatchCount));
                }
                else
                {
                    ReportOnce("level2", $"fail:{level2.Reason}", () => _logger.Level2NotReady(level2.Reason ?? "unknown"));
                }

                _state.Update(current => current with
                {
                    Chrome = _chrome.State,
                    WarriorPage = WarriorPageState.Found,
                    Level2 = level2.Status switch
                    {
                        // "Found but not selected" is still Ready from the operator's point of
                        // view: the command path selects it before dispatching, so a key press
                        // will work. Reporting it as not-ready would be misleading.
                        Level2Status.Ready or Level2Status.Found => Level2State.Ready,
                        Level2Status.Ambiguous => Level2State.Found,
                        _ => Level2State.NotFound,
                    },
                    LastError = result.WasAmbiguous ? result.Reason : level2.Reason,
                });
                break;

            case WarriorPageStatus.PageNotFound:
                ReportOnce("page", $"none:{result.Reason}",
                    () => _logger.WarriorPageNotFound(result.Reason ?? "no reason given"));
                _state.Update(current => current with
                {
                    Chrome = _chrome.State,
                    WarriorPage = WarriorPageState.NotFound,
                    Level2 = Level2State.Unknown,
                    LastError = result.Reason,
                });
                break;

            default:
                _state.Update(current => current with
                {
                    Chrome = _chrome.State,
                    WarriorPage = WarriorPageState.Unknown,
                    Level2 = Level2State.Unknown,
                });
                break;
        }
    }

    /// <summary>
    /// Emits a health-check observation only when it differs from the last one.
    /// </summary>
    /// <remarks>
    /// The watchdog re-checks every few seconds and normally finds the world unchanged. Saying
    /// so repeatedly is not information: it is what buries the hotkey line the operator is
    /// actually watching for. State transitions still log immediately.
    /// </remarks>
    private void ReportOnce(string category, string signature, Action log)
    {
        if (_lastReported.TryGetValue(category, out string? previous) && previous == signature)
        {
            return;
        }

        _lastReported[category] = signature;
        log();
    }

    private void OnChromeStateChanged(object? sender, ChromeStateChangedEventArgs e) =>
        _state.Update(current => e.Current is ChromeState.Connected
            ? current with { Chrome = e.Current }
            : current with
            {
                Chrome = e.Current,
                WarriorPage = WarriorPageState.Unknown,
                Level2 = Level2State.Unknown,
                LastError = e.Reason ?? current.LastError,
            });

    public override void Dispose()
    {
        _chrome.StateChanged -= OnChromeStateChanged;
        _resume.Resumed -= OnSystemResumed;
        base.Dispose();
    }
}
