using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using WarriorHotkeyBridge.Diagnostics;
using WarriorHotkeyBridge.Hotkeys;
using WarriorHotkeyBridge.Models;

namespace WarriorHotkeyBridge.Commands;

/// <summary>
/// Single-consumer command queue between the hotkey thread and the Playwright worker.
/// </summary>
/// <remarks>
/// <para>
/// Two fast presses must execute in the order they were pressed, and never concurrently: two
/// overlapping Playwright interactions against the same page would race over which component
/// is selected, and could deliver the second chord to a panel the first one just changed.
/// </para>
/// <para>
/// The channel is unbounded and writes never block. The producer is the UI thread inside a
/// window procedure - blocking it would stall the entire message loop, including the delivery
/// of the next hotkey. A human pressing deck buttons cannot outpace the consumer enough for
/// unboundedness to matter, and dropping a trading command silently would be far worse than
/// briefly holding a few.
/// </para>
/// </remarks>
internal sealed class CommandQueue
{
    private readonly Channel<CommandRequest> _channel;
    private readonly ILogger<CommandQueue> _logger;
    private int _depth;

    public CommandQueue(ILogger<CommandQueue> logger)
    {
        _logger = logger;
        _channel = Channel.CreateUnbounded<CommandRequest>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,

            // Continuations must not run inline on the producer, which is the UI thread.
            AllowSynchronousContinuations = false,
        });
    }

    /// <summary>Approximate number of commands waiting. Diagnostics only.</summary>
    public int Depth => Volatile.Read(ref _depth);

    public ChannelReader<CommandRequest> Reader => _channel.Reader;

    /// <summary>
    /// Enqueues a press. Safe to call from the window procedure: never blocks, never throws.
    /// </summary>
    public void Enqueue(HotkeyRegistration registration, long receivedTimestamp) =>
        Write(new CommandRequest(
            registration.Action,
            registration.GestureDisplay,
            receivedTimestamp,
            Stopwatch.GetTimestamp()));

    /// <summary>
    /// Enqueues a command started from the user interface and completes when it has run.
    /// </summary>
    /// <remarks>
    /// The same queue, deliberately. A window that ran its own command against the executor
    /// would be a second consumer, free to re-target the page while a trading command was
    /// part-way through selecting a component on it.
    /// </remarks>
    public Task<CommandResult> EnqueueAsync(HotkeyAction action, string source)
    {
        ArgumentNullException.ThrowIfNull(action);

        // Asynchronous continuations for the same reason the channel disallows synchronous ones:
        // the awaiting caller is the UI thread, and resuming it inline on the command consumer
        // would run window code off the message loop.
        var completion = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        long now = Stopwatch.GetTimestamp();

        Write(new CommandRequest(action, source, now, now) { Completion = completion });

        return completion.Task;
    }

    private void Write(CommandRequest request)
    {
        if (_channel.Writer.TryWrite(request))
        {
            int depth = Interlocked.Increment(ref _depth);

            if (depth > 1)
            {
                _logger.CommandQueueBacklog(depth);
            }

            return;
        }

        // Unreachable for an unbounded channel unless it has been completed during shutdown.
        _logger.CommandRejected(request.Source, "the command queue is closed");

        // A caller waiting on the outcome has to be told, rather than left awaiting a queue that
        // will never run its command.
        request.Completion?.TrySetResult(new CommandResult
        {
            Outcome = CommandOutcome.Rejected,
            ActionDescription = request.Action.Describe(),
            Dispatched = false,
            FailureReason = "The bridge is shutting down.",
        });
    }

    /// <summary>Called by the consumer after taking an item, to keep <see cref="Depth"/> honest.</summary>
    public void OnDequeued() => Interlocked.Decrement(ref _depth);

    /// <summary>Stops accepting new commands. In-flight items still drain.</summary>
    public void Complete() => _channel.Writer.TryComplete();
}
