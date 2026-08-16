using Microsoft.Extensions.Logging.Abstractions;
using WarriorHotkeyBridge.Diagnostics;
using WarriorHotkeyBridge.Services;
using WarriorHotkeyBridge.Startup;

namespace WarriorHotkeyBridge.Tests;

/// <summary>
/// Covers when the bridge may change the operator's "start with Windows" setting.
/// </summary>
/// <remarks>
/// These rules are the kind that rot silently: almost every branch is a decision NOT to act, and
/// nothing at runtime would fail loudly if one regressed - the bridge would simply start asking a
/// question it has no business asking, or quietly reverse a choice the operator made.
/// </remarks>
public class StartupRegistrationServiceTests
{
    // ---------------------------------------------------------------- first run

    [Fact]
    public async Task FirstRun_EnablesStartupAndRecordsTheVersion()
    {
        var startup = new FakeStartupManager(StartupState.Disabled);
        var store = new FakeStartupPreferenceStore(null);

        await Run(startup, store);

        Assert.Equal(1, startup.EnableCalls);
        Assert.True(store.Written!.StartWithWindows);
        Assert.Equal(AppInfo.Version, store.Written.AppVersion);
    }

    /// <summary>
    /// A recorded "on" that never happened is worse than no record: the service would take the
    /// do-nothing path forever afterwards, and the restore path could never fire, so a new
    /// install whose first registry write failed would be condemned to never starting.
    /// </summary>
    [Fact]
    public async Task FirstRun_WhenTheRegistryWriteFails_RecordsNothing()
    {
        var startup = new FakeStartupManager(StartupState.Disabled, enableSucceeds: false);
        var store = new FakeStartupPreferenceStore(null);

        await Run(startup, store);

        Assert.Null(store.Written);
    }

    /// <summary>
    /// A preference file that exists but will not parse records a decision we cannot read.
    /// Treating it as "never decided" runs the first-run path and switches startup on, which for
    /// an operator who had switched it off is exactly the reversal this service must never do.
    /// An interrupted write is all it takes to produce one.
    /// </summary>
    [Fact]
    public async Task PreferenceFileExistsButIsUnreadable_ChangesNothing()
    {
        var startup = new FakeStartupManager(StartupState.Disabled);
        var store = new FakeStartupPreferenceStore(null, exists: true);
        var ui = new RecordingUiDispatcher();

        await Run(startup, store, ui);

        Assert.Equal(0, startup.EnableCalls);
        Assert.Null(store.Written);
        Assert.Equal(0, ui.DeferredCount);
    }

    // ---------------------------------------------------------------- startup left on

    [Fact]
    public async Task StartupOnAndAlreadyEnabled_ChangesNothing()
    {
        var startup = new FakeStartupManager(StartupState.Enabled);
        var store = new FakeStartupPreferenceStore(Preference(on: true, version: AppInfo.Version));

        await Run(startup, store);

        Assert.Equal(0, startup.EnableCalls);
        Assert.Null(store.Written);
    }

    [Fact]
    public async Task StartupOnButPointingElsewhere_IsRepairedWithoutAsking()
    {
        var startup = new FakeStartupManager(StartupState.PointsElsewhere);
        var store = new FakeStartupPreferenceStore(Preference(on: true, version: "0.0.1-previous"));
        var ui = new RecordingUiDispatcher();

        await Run(startup, store, ui);

        Assert.Equal(1, startup.EnableCalls);
        Assert.Equal(0, ui.DeferredCount);
    }

    /// <summary>
    /// The uninstall-then-reinstall path. The uninstaller removes the Run value while the
    /// preference lives under %LOCALAPPDATA% and survives, so on the way back in the two
    /// disagree - and before this was handled, start with Windows was silently lost every time.
    /// </summary>
    [Fact]
    public async Task StartupOnButRegistrationMissing_IsRestoredWithoutAsking()
    {
        var startup = new FakeStartupManager(StartupState.Disabled);
        var store = new FakeStartupPreferenceStore(Preference(on: true, version: AppInfo.Version));
        var ui = new RecordingUiDispatcher();

        await Run(startup, store, ui);

        Assert.Equal(1, startup.EnableCalls);
        Assert.Equal(0, ui.DeferredCount);
    }

    /// <summary>
    /// A transient registry failure during the restore must not be written down as a decision.
    /// Recording "off" here would convert one bad moment into a permanent setting: the restore
    /// could never run again, and the operator would find out when their hotkeys were dead.
    /// </summary>
    [Fact]
    public async Task StartupOnButRestoreFails_DoesNotDowngradeTheRecordedPreference()
    {
        var startup = new FakeStartupManager(StartupState.Disabled, enableSucceeds: false);
        var store = new FakeStartupPreferenceStore(Preference(on: true, version: AppInfo.Version));

        await Run(startup, store);

        Assert.True(store.Written is null || store.Written.StartWithWindows);
    }

    /// <summary>
    /// Switched off in Task Manager's Startup apps: the Run value is present and correct, and
    /// rewriting it cannot clear Windows' own block.
    /// </summary>
    [Fact]
    public async Task StartupOnButBlockedByWindows_IsLeftAlone()
    {
        var startup = new FakeStartupManager(StartupState.BlockedByWindows);
        var store = new FakeStartupPreferenceStore(Preference(on: true, version: AppInfo.Version));
        var ui = new RecordingUiDispatcher();

        await Run(startup, store, ui);

        Assert.Equal(0, startup.EnableCalls);
        Assert.Equal(0, ui.DeferredCount);
    }

    // ---------------------------------------------------------------- startup left off

    /// <summary>The core promise: an explicit off survives, and is not quietly undone.</summary>
    [Fact]
    public async Task StartupOffOnTheSameVersion_DoesNothingAtAll()
    {
        var startup = new FakeStartupManager(StartupState.Disabled);
        var store = new FakeStartupPreferenceStore(Preference(on: false, version: AppInfo.Version));
        var ui = new RecordingUiDispatcher();

        await Run(startup, store, ui);

        Assert.Equal(0, startup.EnableCalls);
        Assert.Null(store.Written);
        Assert.Equal(0, ui.DeferredCount);
    }

    /// <summary>
    /// A preference written before the version field existed says nothing about which build was
    /// refused, so prompting on it would fire once for every existing user purely because a field
    /// was added.
    /// </summary>
    [Fact]
    public async Task StartupOffWithNoRecordedVersion_AdoptsTheVersionWithoutAsking()
    {
        var startup = new FakeStartupManager(StartupState.Disabled);
        var store = new FakeStartupPreferenceStore(Preference(on: false, version: null));
        var ui = new RecordingUiDispatcher();

        await Run(startup, store, ui);

        Assert.Equal(0, ui.DeferredCount);
        Assert.Equal(0, startup.EnableCalls);
        Assert.Equal(AppInfo.Version, store.Written!.AppVersion);
        Assert.False(store.Written.StartWithWindows);
    }

    /// <summary>
    /// The registry could not be read, so whether startup is off is unknown. Stamping the version
    /// would spend this update's one offer on a question that was never asked - and the transient
    /// causes have usually cleared by the next launch.
    /// </summary>
    [Fact]
    public async Task StartupOffButStateUnknownAfterAnUpdate_DoesNotConsumeTheOffer()
    {
        var startup = new FakeStartupManager(StartupState.Unknown);
        var store = new FakeStartupPreferenceStore(Preference(on: false, version: "0.0.1-previous"));
        var ui = new RecordingUiDispatcher();

        await Run(startup, store, ui);

        Assert.Equal(0, ui.DeferredCount);
        Assert.Null(store.Written);
    }

    /// <summary>
    /// Asking must be deferred, never run inline. Hosted services start on the UI thread before
    /// Application.Run, so an inline modal blocks host start and appears before the tray icon
    /// exists and before a single hotkey is registered.
    /// </summary>
    [Fact]
    public async Task ThePostUpdateOfferIsDeferred_NotRunInline()
    {
        var startup = new FakeStartupManager(StartupState.Disabled);
        var store = new FakeStartupPreferenceStore(Preference(on: false, version: "0.0.1-previous"));
        var ui = new RecordingUiDispatcher();

        await Run(startup, store, ui);

        Assert.Equal(1, ui.DeferredCount);
        Assert.Equal(0, ui.PostedCount);
    }

    /// <summary>
    /// Nothing may change before the operator answers. A refactor that enabled startup first and
    /// asked afterwards would override an explicit off at launch, and if the dialog were never
    /// seen or never answered, startup would stay on with no record of the question.
    /// </summary>
    [Fact]
    public async Task ThePostUpdateOfferChangesNothingBeforeItIsAnswered()
    {
        var startup = new FakeStartupManager(StartupState.Disabled);
        var store = new FakeStartupPreferenceStore(Preference(on: false, version: "0.0.1-previous"));
        var ui = new RecordingUiDispatcher();

        await Run(startup, store, ui);

        Assert.Equal(0, startup.EnableCalls);
        Assert.Null(store.Written);
    }

    [Fact]
    public async Task AnsweringYes_EnablesStartupAndRecordsIt()
    {
        var startup = new FakeStartupManager(StartupState.Disabled);
        var store = new FakeStartupPreferenceStore(Preference(on: false, version: "0.0.1-previous"));
        var ui = new RecordingUiDispatcher();
        var prompt = new FakeStartupPrompt(answer: true);

        await Run(startup, store, ui, prompt);
        ui.RunDeferred();

        Assert.Equal(1, prompt.AskCount);
        Assert.Equal(1, startup.EnableCalls);
        Assert.True(store.Written!.StartWithWindows);
        Assert.Equal(AppInfo.Version, store.Written.AppVersion);
    }

    /// <summary>
    /// No must not touch the registry, and must still stamp the version - otherwise the same
    /// question returns on every launch until the next update.
    /// </summary>
    [Fact]
    public async Task AnsweringNo_LeavesStartupOffAndStillStampsTheVersion()
    {
        var startup = new FakeStartupManager(StartupState.Disabled);
        var store = new FakeStartupPreferenceStore(Preference(on: false, version: "0.0.1-previous"));
        var ui = new RecordingUiDispatcher();
        var prompt = new FakeStartupPrompt(answer: false);

        await Run(startup, store, ui, prompt);
        ui.RunDeferred();

        Assert.Equal(0, startup.EnableCalls);
        Assert.False(store.Written!.StartWithWindows);
        Assert.Equal(AppInfo.Version, store.Written.AppVersion);
    }

    /// <summary>
    /// Said yes, got nothing. Without the report the dialog just closes, the tray shows startup
    /// off, and the operator is left believing they turned it back on.
    /// </summary>
    [Fact]
    public async Task AnsweringYesWhenTheRegistryWriteFails_TellsTheOperatorAndRecordsOff()
    {
        var startup = new FakeStartupManager(StartupState.Disabled, enableSucceeds: false);
        var store = new FakeStartupPreferenceStore(Preference(on: false, version: "0.0.1-previous"));
        var ui = new RecordingUiDispatcher();
        var prompt = new FakeStartupPrompt(answer: true);

        await Run(startup, store, ui, prompt);
        ui.RunDeferred();

        Assert.Equal(1, prompt.EnableFailedReports);
        Assert.False(store.Written!.StartWithWindows);
    }

    /// <summary>Whichever way it is answered, the question is not asked again for this version.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheOfferIsMadeOnlyOncePerUpdate(bool answer)
    {
        var startup = new FakeStartupManager(StartupState.Disabled);
        var store = new FakeStartupPreferenceStore(Preference(on: false, version: "0.0.1-previous"));
        var ui = new RecordingUiDispatcher();
        var prompt = new FakeStartupPrompt(answer);

        await Run(startup, store, ui, prompt);
        ui.RunDeferred();

        StartupPreference afterFirstLaunch = store.Written!;
        Assert.Equal(AppInfo.Version, afterFirstLaunch.AppVersion);

        // Relaunch on the same version with whatever the first launch recorded.
        var second = new FakeStartupPreferenceStore(afterFirstLaunch);
        var secondUi = new RecordingUiDispatcher();
        var secondPrompt = new FakeStartupPrompt(answer);

        await Run(new FakeStartupManager(answer ? StartupState.Enabled : StartupState.Disabled),
                  second, secondUi, secondPrompt);
        secondUi.RunDeferred();

        Assert.Equal(0, secondPrompt.AskCount);
    }

    // ---------------------------------------------------------------- helpers

    private static StartupPreference Preference(bool on, string? version) =>
        new() { StartWithWindows = on, AppVersion = version };

    private static Task Run(
        FakeStartupManager startup,
        FakeStartupPreferenceStore store,
        IUiDispatcher? ui = null,
        IStartupPrompt? prompt = null) =>
        new StartupRegistrationService(
            startup,
            store,
            ui ?? new RecordingUiDispatcher(),
            prompt ?? new FakeStartupPrompt(answer: false),
            TimeProvider.System,
            NullLogger<StartupRegistrationService>.Instance)
        .StartAsync(CancellationToken.None);

    private sealed class FakeStartupManager(StartupState state, bool enableSucceeds = true) : IStartupManager
    {
        public int EnableCalls { get; private set; }

        public StartupStatus GetStatus() => new(
            state,
            state is StartupState.PointsElsewhere ? @"C:\Old\WarriorHotkeyBridge.exe" : null,
            @"C:\Current\WarriorHotkeyBridge.exe");

        public bool TryEnable(out string? error)
        {
            EnableCalls++;
            error = enableSucceeds ? null : "access denied";
            return enableSucceeds;
        }

        public bool TryDisable(out string? error)
        {
            error = null;
            return true;
        }
    }

    private sealed class FakeStartupPreferenceStore : IStartupPreferenceStore
    {
        private readonly StartupPreference? _existing;
        private readonly bool _exists;

        public FakeStartupPreferenceStore(StartupPreference? existing, bool? exists = null)
        {
            _existing = existing;

            // Defaults to "the file is there iff it parsed", so only the damaged-file test has
            // to say otherwise.
            _exists = exists ?? existing is not null;
        }

        public StartupPreference? Written { get; private set; }

        public bool Exists => _exists;

        public StartupPreference? Read() => _existing;

        public void Write(StartupPreference preference) => Written = preference;
    }

    /// <summary>
    /// Records deferred work instead of running it, so a test can assert what was scheduled and
    /// then run it explicitly. Keeping Post and Defer separate is what lets a test prove the
    /// offer is deferred rather than executed inline during host start.
    /// </summary>
    private sealed class RecordingUiDispatcher : IUiDispatcher
    {
        private readonly List<Action> _deferred = [];

        public int PostedCount { get; private set; }

        public int DeferredCount => _deferred.Count;

        public bool IsOnUiThread => true;

        public void Post(Action action) => PostedCount++;

        public void Defer(Action action) => _deferred.Add(action);

        /// <summary>Inline: this stub reports itself as the UI thread, so there is nothing to marshal.</summary>
        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }

        public void RunDeferred()
        {
            foreach (Action action in _deferred.ToArray())
            {
                action();
            }
        }
    }

    private sealed class FakeStartupPrompt(bool answer) : IStartupPrompt
    {
        public int AskCount { get; private set; }

        public int EnableFailedReports { get; private set; }

        public bool AskToReEnableAfterUpdate(string version)
        {
            AskCount++;
            return answer;
        }

        public void ReportEnableFailed(string reason) => EnableFailedReports++;
    }
}
