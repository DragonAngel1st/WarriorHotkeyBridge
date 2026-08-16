using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WarriorHotkeyBridge.Diagnostics;
using WarriorHotkeyBridge.Startup;

namespace WarriorHotkeyBridge.Services;

/// <summary>
/// Registers the bridge to start with Windows the first time it ever runs, repairs a
/// registration left pointing at an old install location, and offers to restore startup once
/// after an update.
/// </summary>
/// <remarks>
/// Runs once at startup, not on a timer. The rules exist to keep it from fighting the operator:
/// it enables startup only when no preference has ever been recorded, and it never silently
/// re-enables afterwards. Switching startup off in the tray is therefore permanent unless the
/// operator says otherwise in answer to a direct question.
/// </remarks>
internal sealed class StartupRegistrationService : IHostedService
{
    private readonly IStartupManager _startup;
    private readonly IStartupPreferenceStore _preferences;
    private readonly IUiDispatcher _ui;
    private readonly IStartupPrompt _prompt;
    private readonly TimeProvider _time;
    private readonly ILogger<StartupRegistrationService> _logger;

    public StartupRegistrationService(
        IStartupManager startup,
        IStartupPreferenceStore preferences,
        IUiDispatcher ui,
        IStartupPrompt prompt,
        TimeProvider time,
        ILogger<StartupRegistrationService> logger)
    {
        _startup = startup;
        _preferences = preferences;
        _ui = ui;
        _prompt = prompt;
        _time = time;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        StartupPreference? preference = _preferences.Read();
        StartupStatus status = _startup.GetStatus();

        if (preference is null)
        {
            // A preference that exists but will not parse is not the same as no preference at
            // all. It records a decision; we simply cannot read which one. Treating it as "never
            // decided" would run the first-run path and switch startup on - potentially
            // reversing an explicit off, which is the one thing this service must never do. A
            // truncated file is not exotic: it is what an interrupted write leaves behind.
            if (_preferences.Exists)
            {
                _logger.StartupPreferenceUnreadable();
                return Task.CompletedTask;
            }

            EnableOnFirstRun();
            return Task.CompletedTask;
        }

        if (preference.StartWithWindows)
        {
            // The operator wants startup on but Windows will not deliver it. Two ways that
            // happens, and both are a repair rather than a new decision:
            //
            //   PointsElsewhere - the entry survives but names an executable that has moved,
            //                     typically after an upgrade changed the install location.
            //   Disabled        - the entry is gone entirely. The ordinary cause is an
            //                     uninstall followed by a reinstall: the uninstaller removes the
            //                     Run value, while this preference lives under %LOCALAPPDATA%
            //                     and deliberately survives, so the two disagree on the way back
            //                     in. Without this the setting would be silently lost every
            //                     reinstall, and the operator would find out only when their
            //                     hotkeys were dead the next morning.
            //
            // BlockedByWindows is pointedly not in that list. There the value is present and
            // correct, and Windows has been told not to run it from Task Manager's Startup apps.
            // Rewriting the value cannot clear that, and doing so would be overriding a decision
            // made deliberately somewhere else.
            if (status.State is StartupState.PointsElsewhere or StartupState.Disabled)
            {
                if (_startup.TryEnable(out string? error))
                {
                    if (status.State is StartupState.PointsElsewhere)
                    {
                        _logger.StartupRepaired(status.RegisteredCommand ?? "(unknown)");
                    }
                    else
                    {
                        _logger.StartupRestored();
                    }
                }
                else
                {
                    _logger.StartupRegistryFailed(error ?? "unknown");
                }
            }
            else if (status.State is StartupState.Enabled
                && !string.Equals(status.RegisteredCommand, status.ExpectedCommand, StringComparison.Ordinal))
            {
                // Right executable, stale arguments. An entry written by an older build points at
                // the same file, so every other check reads it as healthy and leaves it alone -
                // and it would keep launching without the parked switch, arming a session and
                // opening Chrome at every sign-in. That is the exact behaviour the switch exists
                // to prevent, so an upgrade has to rewrite the value rather than inherit it.
                if (_startup.TryEnable(out string? error))
                {
                    _logger.StartupArgumentsUpdated(status.RegisteredCommand ?? "(unknown)", status.ExpectedCommand);
                }
                else
                {
                    _logger.StartupRegistryFailed(error ?? "unknown");
                }
            }

            // Keep the recorded version current so that if the operator later switches startup
            // off, the post-update offer is measured from that point rather than from whenever
            // the preference file happened to be written.
            RecordVersionIfChanged(preference);
            return Task.CompletedTask;
        }

        OfferAfterUpdate(preference, status);
        return Task.CompletedTask;
    }

    private void EnableOnFirstRun()
    {
        // First run after installation: opt in, because a tray bridge the operator has to
        // remember to launch is a bridge that is not running when they press a key.
        if (_startup.TryEnable(out string? error))
        {
            Write(startWithWindows: true);
            _logger.StartupAutoEnabled();
        }
        else
        {
            _logger.StartupRegistryFailed(error ?? "unknown");
        }
    }

    /// <summary>
    /// Asks - once per update - whether startup should come back on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An update is the one moment where re-asking is legitimate: the operator may well have
    /// switched startup off because of something in the previous build, and installing a new one
    /// is a deliberate act rather than background noise. Recording the version with the answer
    /// is what bounds it to once, whichever way they answer.
    /// </para>
    /// <para>
    /// The question is posted to the UI thread rather than shown from here. This method runs
    /// during host start, before <c>Application.Run</c>; showing a modal dialog inline would put
    /// one on screen before the tray icon exists, with no way to tell what it belongs to.
    /// Posting defers it until the message loop is pumping.
    /// </para>
    /// </remarks>
    private void OfferAfterUpdate(StartupPreference preference, StartupStatus status)
    {
        // Null means a preference written before the version was recorded, which says nothing
        // about which build it referred to. Adopt the current version silently; the next update
        // is the first one that can honestly be called an update.
        if (preference.AppVersion is null)
        {
            RecordVersionIfChanged(preference);
            return;
        }

        if (string.Equals(preference.AppVersion, AppInfo.Version, StringComparison.Ordinal))
        {
            return;
        }

        // The registry could not be read, so whether startup is off is unknown. Stamping the
        // version here would spend this update's single offer on a question never asked - and
        // the transient causes (a policy applied at logon, endpoint protection holding the key)
        // are exactly the ones that have usually cleared by the next launch. Leave the recorded
        // version alone and try again then.
        if (status.State is StartupState.Unknown)
        {
            return;
        }

        // Only when startup really is off. If something else already put an entry back, or
        // Windows has it blocked, a dialog offering to enable it would be answering the wrong
        // question - and re-enabling would not fix BlockedByWindows anyway.
        if (status.State is not StartupState.Disabled)
        {
            RecordVersionIfChanged(preference);
            return;
        }

        _logger.StartupOfferingAfterUpdate(preference.AppVersion, AppInfo.Version);

        // Deferred, not posted: this runs during host start, on the UI thread, before
        // Application.Run. Posting would run it inline and block startup with a modal that
        // appears before the tray icon exists and before a single hotkey is registered - which
        // is the "silently dead hotkeys" failure the rest of this application exists to avoid.
        _ui.Defer(() =>
        {
            bool reEnable = _prompt.AskToReEnableAfterUpdate(AppInfo.Version);

            if (reEnable && !_startup.TryEnable(out string? error))
            {
                _logger.StartupRegistryFailed(error ?? "unknown");
                reEnable = false;

                // Said yes, got nothing. Without this the dialog simply closes, the tray shows
                // startup off, and the operator is left believing they turned it back on.
                _prompt.ReportEnableFailed(error ?? "The registry could not be written.");
            }

            // Written after the attempt, not before, so a failed registry write does not leave a
            // record claiming startup is on. Either way the version is stamped, so the question
            // is not asked again until the next update.
            Write(reEnable);
            _logger.StartupOfferAnswered(reEnable);
        });
    }

    private void RecordVersionIfChanged(StartupPreference preference)
    {
        if (string.Equals(preference.AppVersion, AppInfo.Version, StringComparison.Ordinal))
        {
            return;
        }

        _preferences.Write(preference with { AppVersion = AppInfo.Version });
    }

    private void Write(bool startWithWindows) =>
        _preferences.Write(new StartupPreference
        {
            StartWithWindows = startWithWindows,
            DecidedAt = _time.GetUtcNow(),
            AppVersion = AppInfo.Version,
        });

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
