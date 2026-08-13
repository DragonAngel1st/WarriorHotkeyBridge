using Microsoft.Extensions.Logging.Abstractions;
using WarriorHotkeyBridge.Configuration;
using WarriorHotkeyBridge.Hotkeys;
using WarriorHotkeyBridge.Models;
using WarriorHotkeyBridge.Services;

namespace WarriorHotkeyBridge.Tests;

/// <summary>
/// Covers suppression of hotkey dispatch while a key is being captured.
/// </summary>
/// <remarks>
/// This is the most safety-critical behaviour in the editor. F13-F24 stay registered globally
/// while the capture dialog is open, so a press intended to be recorded would otherwise reach the
/// command queue and place a real order. Every test here is about that not happening, and about
/// suppression ending afterwards - a suppression that leaked would silently kill every trading key
/// until the bridge was restarted.
/// </remarks>
public class DispatchSuppressionTests
{
    [Fact]
    public void PressesAreDispatchedNormallyWhenNotSuppressed() => OnUiThread(service =>
    {
        int dispatched = 0;
        service.HotkeyPressed += (_, _) => dispatched++;

        service.RegisterAll();
        Press(service);

        Assert.Equal(1, dispatched);
    });

    /// <summary>The one that matters: nothing reaches the command path while capturing.</summary>
    [Fact]
    public void PressesAreNotDispatchedWhileSuppressed() => OnUiThread(service =>
    {
        int dispatched = 0;
        service.HotkeyPressed += (_, _) => dispatched++;

        service.RegisterAll();

        using (service.SuppressDispatch())
        {
            Press(service);
            Press(service);
        }

        Assert.Equal(0, dispatched);
    });

    [Fact]
    public void DispatchResumesWhenTheScopeEnds() => OnUiThread(service =>
    {
        int dispatched = 0;
        service.HotkeyPressed += (_, _) => dispatched++;

        service.RegisterAll();

        using (service.SuppressDispatch())
        {
            Press(service);
        }

        Press(service);

        Assert.Equal(1, dispatched);
    });

    /// <summary>
    /// A dialog that throws must not leave the operator's keys permanently dead, which is why the
    /// scope is a disposable used with <c>using</c> rather than a property that has to be reset.
    /// </summary>
    [Fact]
    public void SuppressionEndsEvenWhenTheScopeBodyThrows() => OnUiThread(service =>
    {
        int dispatched = 0;
        service.HotkeyPressed += (_, _) => dispatched++;

        service.RegisterAll();

        // Explicitly typed: a lambda whose body only throws matches both the Action and the
        // Func<Task> overload, and xUnit's async overload is obsolete for synchronous code.
        Action failingScope = () =>
        {
            using (service.SuppressDispatch())
            {
                throw new InvalidOperationException("the dialog fell over");
            }
        };

        Assert.Throws<InvalidOperationException>(failingScope);

        Press(service);

        Assert.Equal(1, dispatched);
    });

    /// <summary>
    /// Nested scopes must not have the inner one re-arm the keys while the outer still needs them
    /// held - which is what a boolean flag would do.
    /// </summary>
    [Fact]
    public void NestedScopesOnlyResumeWhenTheLastOneEnds() => OnUiThread(service =>
    {
        int dispatched = 0;
        service.HotkeyPressed += (_, _) => dispatched++;

        service.RegisterAll();

        using (service.SuppressDispatch())
        {
            using (service.SuppressDispatch())
            {
                Press(service);
            }

            // Inner scope closed, outer still open: still suppressed.
            Press(service);
            Assert.Equal(0, dispatched);
        }

        Press(service);
        Assert.Equal(1, dispatched);
    });

    [Fact]
    public void DisposingAScopeTwiceDoesNotResumeEarly() => OnUiThread(service =>
    {
        int dispatched = 0;
        service.HotkeyPressed += (_, _) => dispatched++;

        service.RegisterAll();

        IDisposable outer = service.SuppressDispatch();
        IDisposable inner = service.SuppressDispatch();

        inner.Dispose();
        inner.Dispose();

        Press(service);
        Assert.Equal(0, dispatched);

        outer.Dispose();
        Press(service);
        Assert.Equal(1, dispatched);
    });

    /// <summary>
    /// Delivers a press of the registered test hotkey through the real message handler.
    /// </summary>
    /// <remarks>
    /// Uses the id Windows actually granted rather than assuming 1, and fails loudly if the key
    /// could not be registered - otherwise a machine where something else holds the combination
    /// would report these tests as passing while exercising nothing.
    /// </remarks>
    private static void Press(GlobalHotkeyService service)
    {
        HotkeyRegistration registration = Assert.Single(service.Registrations);

        Assert.True(
            registration.Succeeded,
            $"The test hotkey could not be registered ({registration.Error}); suppression was never exercised.");

        service.HandleHotkeyMessage(registration.Id);
    }

    /// <summary>
    /// Runs on an STA thread with a real message loop context, because registration asserts it is
    /// on the UI thread - a check that exists precisely because getting it wrong produces hotkeys
    /// that register and then never fire.
    /// </summary>
    private static void OnUiThread(Action<GlobalHotkeyService> body)
    {
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                var dispatcher = new WinFormsUiDispatcher(NullLogger<WinFormsUiDispatcher>.Instance);

                var store = new StubBindingStore(new Dictionary<string, HotkeyBindingConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    // A key nothing else is likely to hold, so registration succeeds on a build
                    // agent as well as a desktop.
                    ["Control+Alt+Shift+F19"] = new() { Action = "Test", Label = "suppression test" },
                });

                using var service = new GlobalHotkeyService(
                    store,
                    new StubBridgeState(),
                    dispatcher,
                    NullLogger<GlobalHotkeyService>.Instance);

                body(service);

                service.UnregisterAll();
                dispatcher.Dispose();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw new InvalidOperationException("The suppression scenario failed.", failure);
        }
    }

    private sealed class StubBindingStore(IReadOnlyDictionary<string, HotkeyBindingConfig> bindings) : IHotkeyBindingStore
    {
        public IReadOnlyDictionary<string, HotkeyBindingConfig> Current { get; private set; } = bindings;

        public void Replace(IReadOnlyDictionary<string, HotkeyBindingConfig> value) => Current = value;
    }

    private sealed class StubBridgeState : IBridgeStateService
    {
        public BridgeState Current { get; private set; } = new();

        public event EventHandler<BridgeStateChangedEventArgs>? Changed;

        public BridgeState Update(Func<BridgeState, BridgeState> mutate)
        {
            BridgeState previous = Current;
            Current = mutate(previous);
            Changed?.Invoke(this, new BridgeStateChangedEventArgs(previous, Current));
            return Current;
        }
    }
}
