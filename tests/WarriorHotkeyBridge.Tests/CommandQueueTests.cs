using Microsoft.Extensions.Logging.Abstractions;
using WarriorHotkeyBridge.Commands;
using WarriorHotkeyBridge.Hotkeys;
using WarriorHotkeyBridge.Models;

namespace WarriorHotkeyBridge.Tests;

/// <summary>
/// The two ways a command reaches the queue.
/// </summary>
/// <remarks>
/// A command started from a window - the hotkey editor's Test button - goes onto the same
/// single-consumer queue as a keypress, so it cannot re-target the page while a trading command
/// is part-way through selecting a component on it. What it needs and a keypress does not is the
/// outcome coming back, and the failure that matters is the one where it never does: a button
/// left disabled and a window waiting on a queue that has already stopped.
/// </remarks>
public class CommandQueueTests
{
    private static readonly HotkeyAction Targeting = new() { Kind = HotkeyActionKind.Test };

    [Fact]
    public void EnqueueAsync_QueuesTheCommandAndWaitsForIt()
    {
        var queue = new CommandQueue(NullLogger<CommandQueue>.Instance);

        Task<CommandResult> pending = queue.EnqueueAsync(Targeting, "a test");

        Assert.False(pending.IsCompleted);
        Assert.Equal(1, queue.Depth);
        Assert.True(queue.Reader.TryRead(out CommandRequest? request));
        Assert.Same(Targeting, request!.Action);
        Assert.Equal("a test", request.Source);
        Assert.NotNull(request.Completion);
    }

    /// <summary>A keypress has nobody waiting on it, and must not be given a completion to leak.</summary>
    [Fact]
    public void Enqueue_FromAKeypressCarriesNoCompletion()
    {
        var queue = new CommandQueue(NullLogger<CommandQueue>.Instance);
        var gesture = new HotkeyGesture(HotkeyModifiers.None, Keys.F13);

        queue.Enqueue(new HotkeyRegistration(1, gesture, Targeting, Succeeded: true, Error: null), 0);

        Assert.True(queue.Reader.TryRead(out CommandRequest? request));
        Assert.Null(request!.Completion);
        Assert.Equal(gesture.Display, request.Source);
    }

    /// <summary>
    /// Reachable in the ordinary way: the bridge is shut down with the editor still open. The
    /// caller has to be released rather than left awaiting a command that will never run.
    /// </summary>
    [Fact]
    public async Task EnqueueAsync_CompletesAsRejectedOnceTheQueueIsClosed()
    {
        var queue = new CommandQueue(NullLogger<CommandQueue>.Instance);
        queue.Complete();

        CommandResult result = await queue
            .EnqueueAsync(Targeting, "a test")
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(CommandOutcome.Rejected, result.Outcome);
        Assert.False(result.Dispatched);
        Assert.False(string.IsNullOrWhiteSpace(result.FailureReason));
    }
}
