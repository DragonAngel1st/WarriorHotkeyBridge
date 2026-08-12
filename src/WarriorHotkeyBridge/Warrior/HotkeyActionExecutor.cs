using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using WarriorHotkeyBridge.Chrome;
using WarriorHotkeyBridge.Configuration;
using WarriorHotkeyBridge.Diagnostics;
using WarriorHotkeyBridge.Models;

namespace WarriorHotkeyBridge.Warrior;

internal interface IHotkeyActionExecutor
{
    Task<CommandResult> ExecuteAsync(CommandRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Runs one hotkey action end to end: verify, target, activate, dispatch.
/// </summary>
/// <remarks>
/// <para>
/// The single most important rule here is the split between preparation and dispatch.
/// Everything up to the keystroke is idempotent and may be retried after a transient
/// Playwright failure. The keystroke itself is attempted exactly once: a Playwright call that
/// reports a timeout may still have delivered the key, and retrying could place a second
/// order. A missed command is recoverable by pressing the key again; a duplicated one is not.
/// </para>
/// </remarks>
internal sealed class HotkeyActionExecutor : IHotkeyActionExecutor
{
    /// <summary>Attempts allowed for the preparation phase only.</summary>
    private const int PreparationAttempts = 2;

    private readonly IChromeConnectionManager _chrome;
    private readonly IWarriorPageLocator _locator;
    private readonly ILevel2Controller _level2;
    private readonly IPageActivator _activator;
    private readonly IDiagnosticsService _diagnostics;
    private readonly CommandOptions _options;
    private readonly ILogger<HotkeyActionExecutor> _logger;

    public HotkeyActionExecutor(
        IChromeConnectionManager chrome,
        IWarriorPageLocator locator,
        ILevel2Controller level2,
        IPageActivator activator,
        IDiagnosticsService diagnostics,
        IOptions<CommandOptions> options,
        ILogger<HotkeyActionExecutor> logger)
    {
        _chrome = chrome;
        _locator = locator;
        _level2 = level2;
        _activator = activator;
        _diagnostics = diagnostics;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<CommandResult> ExecuteAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        long started = Stopwatch.GetTimestamp();
        HotkeyAction action = request.Action;

        var timings = new CommandTimings
        {
            ToQueue = Stopwatch.GetElapsedTime(request.ReceivedTimestamp, request.QueuedTimestamp),
            QueueWait = Stopwatch.GetElapsedTime(request.QueuedTimestamp, started),
        };

        // Freshness gate, checked before anything else touches the page. A command that has been
        // waiting is one the operator has already given up on and probably pressed again.
        TimeSpan age = Stopwatch.GetElapsedTime(request.ReceivedTimestamp, started);

        if (action.DispatchesInput && age.TotalMilliseconds > _options.MaxCommandAgeMs)
        {
            _logger.CommandTooOld(action.Describe(), age.TotalMilliseconds, _options.MaxCommandAgeMs);

            return new CommandResult
            {
                Outcome = CommandOutcome.Rejected,
                ActionDescription = action.Describe(),
                Dispatched = false,
                FailureReason = $"Discarded after waiting {age.TotalMilliseconds:0}ms; press again if still wanted.",
                Timings = timings with { Total = age },
            };
        }

        // ---- Preparation: safe to retry ----
        IPage? page = null;
        string? failure = null;

        for (int attempt = 1; attempt <= PreparationAttempts; attempt++)
        {
            (page, failure) = await PrepareAsync(action, cancellationToken).ConfigureAwait(false);

            if (page is not null)
            {
                break;
            }

            if (attempt < PreparationAttempts)
            {
                // A recreated React subtree or a page that navigated mid-check are both normal
                // and resolve on a second look. The activator's cached window may also be stale.
                _activator.Invalidate();
                _logger.CommandPreparationRetry(attempt, failure ?? "unknown");
            }
        }

        timings = timings with { Targeting = Stopwatch.GetElapsedTime(started) - timings.QueueWait };

        if (page is null)
        {
            return Fail(action, failure ?? "targeting failed", timings, started);
        }

        // ---- Activation ----
        // Before the non-dispatching early return, not after it, so Test rehearses the same path
        // a trading key takes. Test's purpose is to answer "would this work?" without risking an
        // order, and an answer that skipped the window activation would be answering a different
        // question from the one asked.
        if (action.ActivatesWindow)
        {
            long activationStart = Stopwatch.GetTimestamp();

            try
            {
                bool raised = await _activator.ActivateAsync(page, cancellationToken).ConfigureAwait(false);

                if (!raised)
                {
                    // The tab is active even when the window could not be raised, so a chord will
                    // still land correctly. Worth a log line, not worth refusing to trade.
                    _logger.CommandWindowNotRaised();
                }
            }
            catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
            {
                return Fail(action, $"could not activate the SIM window: {ex.Message}", timings, started);
            }

            timings = timings with { Activation = Stopwatch.GetElapsedTime(activationStart) };
        }

        // ---- Non-dispatching actions stop here ----
        if (!action.DispatchesInput)
        {
            if (action.Kind is HotkeyActionKind.Diagnostics)
            {
                // Runs on the command consumer, so it is serialised with everything else and
                // cannot overlap a trading command against the same page.
                string? path = await _diagnostics.WriteReportAsync(cancellationToken).ConfigureAwait(false);

                if (path is not null)
                {
                    _logger.DiagnosticsWritten(path);
                }
            }

            timings = timings with { Total = Stopwatch.GetElapsedTime(request.ReceivedTimestamp) };

            return new CommandResult
            {
                Outcome = CommandOutcome.Succeeded,
                ActionDescription = action.Describe(),
                Dispatched = false,
                Timings = timings,
            };
        }

        // ---- Dispatch: exactly once, never retried ----
        long dispatchStart = Stopwatch.GetTimestamp();

        try
        {
            await page.Keyboard.PressAsync(action.Keys!).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
            timings = timings with
            {
                Dispatch = Stopwatch.GetElapsedTime(dispatchStart),
                Total = Stopwatch.GetElapsedTime(request.ReceivedTimestamp),
            };

            // Two very different failures share this catch, and conflating them misinforms the
            // operator about whether an order exists. Playwright rejects an unusable key name
            // before it sends anything, so that case is definitively "not dispatched". A
            // timeout, by contrast, may have delivered the key and simply failed to confirm.
            bool possiblyDelivered = IsPossiblyDelivered(ex);

            if (possiblyDelivered)
            {
                _logger.CommandDispatchUncertain(action.Describe(), ex.Message);
            }
            else
            {
                _logger.CommandRejected(action.Describe(), ex.Message);
            }

            return new CommandResult
            {
                Outcome = possiblyDelivered ? CommandOutcome.Failed : CommandOutcome.Rejected,
                ActionDescription = action.Describe(),
                Dispatched = possiblyDelivered,
                FailureReason = possiblyDelivered
                    ? $"Dispatch reported an error but may have been delivered; not retried: {ex.Message}"
                    : $"Nothing was sent: {ex.Message}",
                Timings = timings,
            };
        }

        timings = timings with
        {
            Dispatch = Stopwatch.GetElapsedTime(dispatchStart),
            Total = Stopwatch.GetElapsedTime(request.ReceivedTimestamp),
        };

        _logger.CommandChordDispatched(action.Keys!);

        return new CommandResult
        {
            Outcome = CommandOutcome.Succeeded,
            ActionDescription = action.Describe(),
            Dispatched = true,
            Timings = timings,
        };
    }

    /// <summary>
    /// Whether a failed dispatch might still have reached the page.
    /// </summary>
    /// <remarks>
    /// Errs towards "possibly delivered" for anything unrecognised: wrongly believing a key was
    /// not sent could lead to pressing it again and doubling an order, which is the failure that
    /// actually costs money.
    /// </remarks>
    private static bool IsPossiblyDelivered(Exception exception)
    {
        // Argument validation happens entirely client-side, before any protocol traffic.
        if (exception is PlaywrightException
            && exception.Message.Contains("Unknown key", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Verifies the connection, the target page and Level 2, selecting Level 2 if needed.
    /// </summary>
    private async Task<(IPage? Page, string? Failure)> PrepareAsync(
        HotkeyAction action,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!await _chrome.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false))
            {
                return (null, "Chrome is not connected.");
            }

            WarriorPageResult located = await _locator.LocateAsync(cancellationToken).ConfigureAwait(false);

            if (located.Status is not WarriorPageStatus.PageFound || located.Page is null)
            {
                return (null, located.Reason ?? "the Warrior SIM page was not found");
            }

            // Fail closed on a genuinely ambiguous target. The locator's tie-break prefers a
            // visible tab, but the active tab of *every* Chrome window reports itself visible,
            // so with two SIM windows the tie-break decides nothing and the winner is really
            // just CDP enumeration order — the oldest tab, not the one being watched. Guessing
            // is acceptable for a status readout; it is not acceptable for an order.
            if (located.WasAmbiguous)
            {
                return (null,
                    $"{located.Candidates.Count(c => c.IsEligible)} SIM pages contain a Level 2 panel and "
                    + "cannot be told apart. Close the extra one so the target is unambiguous.");
            }

            Level2Result level2 = await _level2
                .EnsureSelectedAsync(located.Page, action.Level2Index, cancellationToken)
                .ConfigureAwait(false);

            return level2.IsReady
                ? (located.Page, null)
                : (null, level2.Reason ?? "Level 2 could not be selected");
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
            return (null, ex.Message);
        }
    }

    private CommandResult Fail(HotkeyAction action, string reason, CommandTimings timings, long started)
    {
        timings = timings with { Total = Stopwatch.GetElapsedTime(started) + timings.ToQueue + timings.QueueWait };

        _logger.CommandRejected(action.Describe(), reason);

        return new CommandResult
        {
            Outcome = CommandOutcome.Rejected,
            ActionDescription = action.Describe(),
            Dispatched = false,
            FailureReason = reason,
            Timings = timings,
        };
    }
}
