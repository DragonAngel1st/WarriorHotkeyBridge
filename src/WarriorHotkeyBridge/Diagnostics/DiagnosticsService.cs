using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using WarriorHotkeyBridge.Chrome;
using WarriorHotkeyBridge.Commands;
using WarriorHotkeyBridge.Configuration;
using WarriorHotkeyBridge.Hotkeys;
using WarriorHotkeyBridge.Models;
using WarriorHotkeyBridge.Services;
using WarriorHotkeyBridge.Startup;
using WarriorHotkeyBridge.Warrior;

namespace WarriorHotkeyBridge.Diagnostics;

internal interface IDiagnosticsService
{
    /// <summary>Builds the report text. Never throws.</summary>
    Task<string> BuildReportAsync(CancellationToken cancellationToken);

    /// <summary>Builds the report and writes it to the Diagnostics folder.</summary>
    /// <returns>The file path, or null if it could not be written.</returns>
    Task<string?> WriteReportAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Produces a single self-contained snapshot for troubleshooting.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately safe to paste into a support thread: it records hosts and paths but never query
/// strings, which on a real session carry the account's <c>userId</c> and session <c>hash</c>.
/// No cookies, tokens or page content appear anywhere.
/// </para>
/// <para>
/// Every section is independently guarded. A diagnostics tool that throws while collecting
/// evidence about a broken system is worse than useless, so a failed section reports its own
/// failure and collection continues.
/// </para>
/// </remarks>
internal sealed class DiagnosticsService : IDiagnosticsService
{
    private readonly IChromeConnectionManager _chrome;
    private readonly IWarriorPageLocator _locator;
    private readonly ILevel2Controller _level2;
    private readonly IGlobalHotkeyService _hotkeys;
    private readonly IBridgeStateService _state;
    private readonly CommandQueue _queue;
    private readonly IStartupManager _startup;
    private readonly AppPaths _paths;
    private readonly CommandLineOptions _cli;
    private readonly ChromeOptions _chromeOptions;
    private readonly WarriorSimOptions _warriorOptions;
    private readonly TimeProvider _time;

    public DiagnosticsService(
        IChromeConnectionManager chrome,
        IWarriorPageLocator locator,
        ILevel2Controller level2,
        IGlobalHotkeyService hotkeys,
        IBridgeStateService state,
        CommandQueue queue,
        IStartupManager startup,
        AppPaths paths,
        CommandLineOptions cli,
        IOptions<ChromeOptions> chromeOptions,
        IOptions<WarriorSimOptions> warriorOptions,
        TimeProvider time)
    {
        _chrome = chrome;
        _locator = locator;
        _level2 = level2;
        _hotkeys = hotkeys;
        _state = state;
        _queue = queue;
        _startup = startup;
        _paths = paths;
        _cli = cli;
        _chromeOptions = chromeOptions.Value;
        _warriorOptions = warriorOptions.Value;
        _time = time;
    }

    public async Task<string> BuildReportAsync(CancellationToken cancellationToken)
    {
        var report = new StringBuilder();

        Section(report, "WARRIOR HOTKEY BRIDGE - DIAGNOSTICS");
        Line(report, "Generated", _time.GetLocalNow().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
        Line(report, "Version", AppInfo.Version);
        Line(report, "Runtime", AppInfo.FrameworkDescription);
        Line(report, "Mode", _cli.Debug ? "debug (console attached)" : "normal (tray only)");
        Line(report, "Console", ConsoleHost.IsAttached ? "attached" : "none");
        Line(report, "Install dir", _paths.InstallDirectory);
        Line(report, "Logs", _paths.Logs);
        Line(report, "User config", $"{_paths.UserConfigFile} (exists: {File.Exists(_paths.UserConfigFile)})");

        BridgeState state = _state.Current;
        Section(report, "STATE");
        Line(report, "Status", state.Status.Describe());
        Line(report, "Chrome", state.Chrome.Describe());
        Line(report, "Warrior SIM", state.WarriorPage.Describe());
        Line(report, "Level 2", state.Level2.Describe());
        Line(report, "Hotkeys", state.Hotkeys.Describe());
        Line(report, "Last action", state.LastAction ?? "(none this session)");
        Line(report, "Last result", state.LastCommandResult?.ToString() ?? "-");
        Line(report, "Last latency", state.LastCommandLatency is { } l ? $"{l.TotalMilliseconds:0.#} ms" : "-");
        Line(report, "Last error", state.LastError ?? "(none)");

        Section(report, "CHROME");
        Line(report, "CDP endpoint", _chromeOptions.CdpEndpoint);
        Line(report, "Connection", _chrome.State.Describe());

        await AppendChromeAsync(report, cancellationToken).ConfigureAwait(false);
        await AppendWarriorAsync(report, cancellationToken).ConfigureAwait(false);

        Section(report, "HOTKEYS");
        Line(report, "Registered", $"{_hotkeys.Registrations.Count(r => r.Succeeded)} of {_hotkeys.Registrations.Count}");

        foreach (HotkeyRegistration registration in _hotkeys.Registrations)
        {
            report.Append("  ")
                .Append(registration.Succeeded ? "[ok]   " : "[FAIL] ")
                .Append(registration.GestureDisplay.PadRight(16))
                .Append(" -> ")
                .Append(registration.ActionDescription)
                .AppendLine(registration.Succeeded ? string.Empty : $"   ({registration.Error})");
        }

        Section(report, "START WITH WINDOWS");
        StartupStatus startup = _startup.GetStatus();
        Line(report, "State", startup.Describe());
        Line(report, "Would launch", startup.RegisteredCommand ?? "(not registered)");
        Line(report, "This executable", startup.ExpectedCommand);

        Section(report, "COMMAND QUEUE");
        Line(report, "Waiting", _queue.Depth.ToString(CultureInfo.InvariantCulture));

        report.AppendLine();
        report.AppendLine("Query strings are omitted throughout; they carry account and session identifiers.");

        return report.ToString();
    }

    private async Task AppendChromeAsync(StringBuilder report, CancellationToken cancellationToken)
    {
        try
        {
            IBrowser? browser = _chrome.Browser;

            if (browser is null)
            {
                Line(report, "Browser", "not connected");
                return;
            }

            Line(report, "Browser", browser.Version);
            Line(report, "Contexts", browser.Contexts.Count.ToString(CultureInfo.InvariantCulture));
            Line(report, "Pages", browser.Contexts.Sum(c => c.Pages.Count).ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is PlaywrightException or ObjectDisposedException)
        {
            Line(report, "Browser", $"could not be inspected: {ex.Message}");
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task AppendWarriorAsync(StringBuilder report, CancellationToken cancellationToken)
    {
        Section(report, "WARRIOR SIM");
        Line(report, "Allowed host", _warriorOptions.AllowedHost);
        Line(report, "Expected title", _warriorOptions.ExpectedTitle);

        WarriorPageResult located;

        try
        {
            located = await _locator.LocateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
            Line(report, "Location", $"failed: {ex.Message}");
            return;
        }

        Line(report, "Result", located.Status.ToString());
        Line(report, "Ambiguous", located.WasAmbiguous ? "YES - dispatch is refused" : "no");

        if (located.Reason is not null)
        {
            Line(report, "Reason", located.Reason);
        }

        report.AppendLine("  Candidates:");

        foreach (WarriorPageCandidate candidate in located.Candidates)
        {
            report.Append("    ").AppendLine(candidate.Describe());
        }

        if (located.Page is null)
        {
            return;
        }

        Section(report, "LEVEL 2");

        try
        {
            IReadOnlyList<SelectorMatch> selectors = await _level2
                .DescribeSelectorsAsync(located.Page, cancellationToken)
                .ConfigureAwait(false);

            for (int i = 0; i < selectors.Count; i++)
            {
                Line(report, i == 0 ? "Primary" : $"Fallback {i}", selectors[i].Describe());
            }

            Level2Result level2 = await _level2
                .LocateAsync(located.Page, index: 0, cancellationToken)
                .ConfigureAwait(false);

            Line(report, "Status", level2.Status.ToString());
            Line(report, "Matched by", level2.MatchedSelector ?? "(none)");
            Line(report, "Panels", level2.MatchCount.ToString(CultureInfo.InvariantCulture));
            Line(report, "Has tab bar", level2.HasTabBar ? "yes" : "no (popped out?)");
            Line(report, "Tabset selected", level2.IsSelected ? "yes" : "no - the command path will click it");

            // Separate from selection on purpose: these two disagree in exactly the case that
            // matters, and a report showing only the first says everything is fine while a chord
            // would land in a chart.
            Line(report, "Keyboard focus", level2.FocusTrapped
                ? "HELD (a chart frame or a text field) - the command path will release it"
                : "on the page, nothing intercepting");

            Line(report, "Probe failed", level2.ProbeFailed ? "YES" : "no");
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
            Line(report, "Level 2", $"could not be inspected: {ex.Message}");
        }
    }

    public async Task<string?> WriteReportAsync(CancellationToken cancellationToken)
    {
        string report = await BuildReportAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            string path = Path.Combine(
                _paths.Diagnostics,
                $"diagnostics-{_time.GetLocalNow():yyyyMMdd-HHmmss}.txt");

            await File.WriteAllTextAsync(path, report, cancellationToken).ConfigureAwait(false);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void Section(StringBuilder report, string title)
    {
        report.AppendLine();
        report.AppendLine(title);
        report.AppendLine(new string('-', title.Length));
    }

    private static void Line(StringBuilder report, string label, string value) =>
        report.Append("  ").Append(label.PadRight(16)).Append(": ").AppendLine(value);
}
