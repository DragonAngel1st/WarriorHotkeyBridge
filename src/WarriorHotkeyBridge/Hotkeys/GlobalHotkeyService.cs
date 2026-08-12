using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WarriorHotkeyBridge.Configuration;
using WarriorHotkeyBridge.Diagnostics;
using WarriorHotkeyBridge.Models;
using WarriorHotkeyBridge.Services;

namespace WarriorHotkeyBridge.Hotkeys;

/// <inheritdoc cref="IGlobalHotkeyService"/>
internal sealed class GlobalHotkeyService : IGlobalHotkeyService, IDisposable
{
    private readonly HotkeyOptions _options;
    private readonly IBridgeStateService _state;
    private readonly IUiDispatcher _ui;
    private readonly ILogger<GlobalHotkeyService> _logger;

    /// <summary>Registration by hotkey id, for O(1) lookup on the message path.</summary>
    private readonly Dictionary<int, HotkeyRegistration> _byId = [];

    private readonly List<HotkeyRegistration> _registrations = [];

    /// <summary>Configuration entries dropped by the resolver, kept so state can be republished.</summary>
    private int _configurationProblemCount;

    private string? _firstConfigurationProblem;

    private HotkeyWindow? _window;
    private bool _disposed;

    public GlobalHotkeyService(
        IOptions<HotkeyOptions> options,
        IBridgeStateService state,
        IUiDispatcher ui,
        ILogger<GlobalHotkeyService> logger)
    {
        _options = options.Value;
        _state = state;
        _ui = ui;
        _logger = logger;
    }

    public IReadOnlyList<HotkeyRegistration> Registrations => _registrations;

    public event EventHandler<HotkeyPressedEventArgs>? HotkeyPressed;

    public void RegisterAll()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureUiThread();

        if (_window is not null)
        {
            throw new InvalidOperationException("Hotkeys are already registered.");
        }

        _state.Update(current => current with { Hotkeys = HotkeyState.Initializing });

        HotkeyBindingResolution resolution = HotkeyBindingResolver.Resolve(_options.Bindings);

        foreach (string problem in resolution.Problems)
        {
            _logger.HotkeyConfigurationProblem(problem);
        }

        if (resolution.Bindings.Count == 0)
        {
            const string NoBindings = "No valid hotkey bindings are configured; no keys will be received.";
            _logger.HotkeyRegistrationFailedEntirely(NoBindings);
            _state.Update(current => current with { Hotkeys = HotkeyState.Failed, LastError = NoBindings });
            return;
        }

        _window = new HotkeyWindow();
        _window.HotkeyPressed += OnHotkeyMessage;

        int nextId = 1;

        foreach (HotkeyBinding binding in resolution.Bindings)
        {
            int id = nextId++;
            HotkeyRegistration registration = TryRegister(id, binding);

            _registrations.Add(registration);

            if (registration.Succeeded)
            {
                _byId[id] = registration;
                _logger.HotkeyRegistered(registration.GestureDisplay, registration.ActionDescription);

                // Surfaced at registration rather than at first press: the operator is looking
                // at the log now, and a shortcut that silently does nothing later is expensive
                // to diagnose.
                if (binding.Action.Keys is { } keys
                    && PlaywrightKeys.DescribeAmbiguity(keys) is { } warning)
                {
                    _logger.HotkeyKeyExpressionAmbiguous(warning);
                }
            }
            else
            {
                _logger.HotkeyRegistrationFailed(registration.GestureDisplay, registration.Error ?? "unknown error");
            }
        }

        _configurationProblemCount = resolution.Problems.Count;
        _firstConfigurationProblem = resolution.Problems.Count > 0 ? resolution.Problems[0] : null;

        PublishRegistrationState();
    }

    /// <summary>
    /// Re-attempts any registration that previously lost the key to another application.
    /// </summary>
    /// <remarks>
    /// A global hotkey is exclusive and first-come. If something else owned the key when the
    /// bridge started - the operator's old AutoHotkey script is the obvious case - the key
    /// stays dead until that application exits. Without this, reclaiming it means restarting
    /// the bridge, which is a poor answer to "I just closed the other app".
    /// </remarks>
    /// <returns>True if at least one key was reclaimed.</returns>
    public bool RetryFailedRegistrations()
    {
        if (_disposed || _window is null)
        {
            return false;
        }

        EnsureUiThread();

        bool reclaimed = false;

        for (int i = 0; i < _registrations.Count; i++)
        {
            HotkeyRegistration registration = _registrations[i];

            if (registration.Succeeded)
            {
                continue;
            }

            bool succeeded = NativeMethods.RegisterHotKey(
                _window.Handle,
                registration.Id,
                registration.Gesture.ToWin32Modifiers(),
                registration.Gesture.VirtualKeyCode);

            if (!succeeded)
            {
                continue;
            }

            HotkeyRegistration updated = registration with { Succeeded = true, Error = null };
            _registrations[i] = updated;
            _byId[updated.Id] = updated;
            reclaimed = true;

            _logger.HotkeyReclaimed(updated.GestureDisplay, updated.ActionDescription);
        }

        if (reclaimed)
        {
            PublishRegistrationState();
        }

        return reclaimed;
    }

    private HotkeyRegistration TryRegister(int id, HotkeyBinding binding)
    {
        Debug.Assert(_window is not null, "The hotkey window is created before registration.");

        bool succeeded = NativeMethods.RegisterHotKey(
            _window.Handle,
            id,
            binding.Gesture.ToWin32Modifiers(),
            binding.Gesture.VirtualKeyCode);

        if (succeeded)
        {
            return new HotkeyRegistration(id, binding.Gesture, binding.Action, Succeeded: true, Error: null);
        }

        int lastError = Marshal.GetLastWin32Error();

        string message = lastError == NativeMethods.ErrorHotkeyAlreadyRegistered
            ? "another application has already registered this key combination"
            : new System.ComponentModel.Win32Exception(lastError).Message;

        return new HotkeyRegistration(id, binding.Gesture, binding.Action, Succeeded: false, Error: message);
    }

    private void PublishRegistrationState()
    {
        int succeeded = _registrations.Count(r => r.Succeeded);
        int attempted = _registrations.Count;

        HotkeyState hotkeyState;
        string? error;

        if (succeeded == 0)
        {
            hotkeyState = HotkeyState.Failed;
            error = "No global hotkeys could be registered; another application is likely holding them.";
        }
        else if (succeeded < attempted)
        {
            hotkeyState = HotkeyState.PartiallyRegistered;
            error = $"{attempted - succeeded} of {attempted} hotkeys failed to register; see the log.";
        }
        else if (_configurationProblemCount > 0)
        {
            // Every key we asked for registered, but configuration entries were dropped by the
            // resolver before registration was ever attempted. Those never reach the attempted
            // count, so reporting a failure ratio here would literally read "0 of 2 failed".
            // The operator needs the reason instead, since the log is not what they are looking at.
            hotkeyState = HotkeyState.PartiallyRegistered;
            error = _configurationProblemCount == 1
                ? _firstConfigurationProblem!
                : $"{_configurationProblemCount} hotkey configuration problems; see the log.";
        }
        else
        {
            hotkeyState = HotkeyState.Registered;
            error = null;
        }

        _logger.HotkeyRegistrationSummary(succeeded, attempted);

        _state.Update(current => error is null
            ? current with { Hotkeys = hotkeyState }
            : current with { Hotkeys = hotkeyState, LastError = error });
    }

    /// <summary>
    /// Runs on the UI thread inside the window procedure, so it must stay short: any work done
    /// here delays the message loop and therefore every subsequent keypress.
    /// </summary>
    private void OnHotkeyMessage(object? sender, int hotkeyId)
    {
        // Captured first so the measured latency includes everything after message receipt.
        long received = Stopwatch.GetTimestamp();

        if (!_byId.TryGetValue(hotkeyId, out HotkeyRegistration? registration))
        {
            // Not ours: ids are window-scoped, so this should be unreachable. Logged rather
            // than ignored because it would indicate a real bookkeeping bug.
            _logger.HotkeyUnknownId(hotkeyId);
            return;
        }

        _logger.HotkeyReceived(registration.GestureDisplay, registration.ActionDescription);

        HotkeyPressed?.Invoke(this, new HotkeyPressedEventArgs(registration, received));
    }

    public void UnregisterAll()
    {
        if (_window is null)
        {
            return;
        }

        EnsureUiThread();

        foreach (HotkeyRegistration registration in _registrations.Where(r => r.Succeeded))
        {
            if (!NativeMethods.UnregisterHotKey(_window.Handle, registration.Id))
            {
                string reason = new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message;
                _logger.HotkeyUnregisterFailed(registration.GestureDisplay, reason);
            }
        }

        _window.HotkeyPressed -= OnHotkeyMessage;
        _window.Dispose();
        _window = null;

        _byId.Clear();
        _registrations.Clear();

        _logger.HotkeysUnregistered();

        _state.Update(current => current with { Hotkeys = HotkeyState.Uninitialized });
    }

    /// <summary>
    /// Win32 ties hotkey registration to the thread owning the window, and the message can only
    /// arrive on that thread's queue. Getting this wrong produces hotkeys that register
    /// successfully and then never fire, so it is asserted rather than assumed.
    /// </summary>
    private void EnsureUiThread()
    {
        if (!_ui.IsOnUiThread)
        {
            throw new InvalidOperationException(
                "Global hotkeys must be registered and released on the UI thread that runs the message loop.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Only safe from the UI thread; shutdown always unregisters there first, leaving this
        // as a no-op. Disposing from anywhere else would be a bug we want to see.
        if (_window is not null && _ui.IsOnUiThread)
        {
            UnregisterAll();
        }
    }
}
