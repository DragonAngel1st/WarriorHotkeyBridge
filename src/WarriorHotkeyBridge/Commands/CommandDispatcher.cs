using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WarriorHotkeyBridge.Diagnostics;
using WarriorHotkeyBridge.Hotkeys;
using WarriorHotkeyBridge.Models;
using WarriorHotkeyBridge.Services;
using WarriorHotkeyBridge.Warrior;

namespace WarriorHotkeyBridge.Commands;

/// <summary>
/// The single consumer of the command queue.
/// </summary>
/// <remarks>
/// Exactly one of these runs, and it awaits each command before taking the next. That single
/// thread of execution is what makes two rapid presses deterministic: the second cannot
/// re-target the page while the first is still selecting a component on it.
/// </remarks>
internal sealed class CommandDispatcher : BackgroundService
{
    private readonly CommandQueue _queue;
    private readonly IGlobalHotkeyService _hotkeys;
    private readonly IHotkeyActionExecutor _executor;
    private readonly IBridgeStateService _state;
    private readonly TimeProvider _time;
    private readonly ILogger<CommandDispatcher> _logger;

    public CommandDispatcher(
        CommandQueue queue,
        IGlobalHotkeyService hotkeys,
        IHotkeyActionExecutor executor,
        IBridgeStateService state,
        TimeProvider time,
        ILogger<CommandDispatcher> logger)
    {
        _queue = queue;
        _hotkeys = hotkeys;
        _executor = executor;
        _state = state;
        _time = time;
        _logger = logger;

        _hotkeys.HotkeyPressed += OnHotkeyPressed;
    }

    /// <summary>
    /// Runs on the UI thread inside the window procedure, so it does nothing but enqueue.
    /// Any real work here would delay the message loop and therefore the next keypress.
    /// </summary>
    private void OnHotkeyPressed(object? sender, HotkeyPressedEventArgs e) =>
        _queue.Enqueue(e.Registration, e.ReceivedTimestamp);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (CommandRequest request in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                _queue.OnDequeued();

                CommandResult result = await _executor
                    .ExecuteAsync(request, stoppingToken)
                    .ConfigureAwait(false);

                Report(result);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private void Report(CommandResult result)
    {
        string timings = result.Timings.Describe();

        if (result.Outcome is CommandOutcome.Succeeded)
        {
            _logger.CommandSucceeded(result.ActionDescription, timings);
        }
        else
        {
            _logger.CommandFailed(result.ActionDescription, result.FailureReason ?? "unknown", timings);
        }

        _state.Update(current => current with
        {
            LastAction = result.ActionDescription,
            LastCommandResult = result.Outcome,
            LastCommandLatency = result.Timings.Total,
            LastCommandAt = _time.GetUtcNow(),

            // A successful command clears a stale error; a failure records why, so the tray
            // explains itself without the operator opening the log.
            LastError = result.Outcome is CommandOutcome.Succeeded ? null : result.FailureReason,
        });
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Stop accepting new commands first, so shutdown drains rather than races.
        _queue.Complete();
        _hotkeys.HotkeyPressed -= OnHotkeyPressed;

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
