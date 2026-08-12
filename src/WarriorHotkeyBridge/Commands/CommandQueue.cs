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
    public void Enqueue(HotkeyRegistration registration, long receivedTimestamp)
    {
        var request = new CommandRequest(registration, receivedTimestamp, Stopwatch.GetTimestamp());

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
        _logger.CommandRejected(registration.GestureDisplay, "the command queue is closed");
    }

    /// <summary>Called by the consumer after taking an item, to keep <see cref="Depth"/> honest.</summary>
    public void OnDequeued() => Interlocked.Decrement(ref _depth);

    /// <summary>Stops accepting new commands. In-flight items still drain.</summary>
    public void Complete() => _channel.Writer.TryComplete();
}
