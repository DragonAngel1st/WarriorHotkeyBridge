using System.Diagnostics;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Win32;
using WarriorHotkeyBridge.Configuration;
using WarriorHotkeyBridge.Diagnostics;
using WarriorHotkeyBridge.Startup;

namespace WarriorHotkeyBridge.Chrome;

internal interface IChromeLauncher
{
    /// <summary>
    /// Starts the dedicated Chrome instance if the CDP endpoint is not answering.
    /// </summary>
    /// <returns>True when the endpoint is reachable on return.</returns>
    Task<bool> EnsureRunningAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Starts Chrome now because the operator asked, ignoring the relaunch cooldown.
    /// </summary>
    /// <remarks>
    /// The cooldown exists to stop a watchdog spawning a process every few seconds when Chrome
    /// cannot start. It is the wrong rule for a button press: someone pressing "go trading" is
    /// entitled to have something happen immediately, and a person pressing a button repeatedly
    /// is self-limiting in a way a timer loop is not.
    /// </remarks>
    Task<bool> LaunchOnRequestAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Optionally launches the dedicated Warrior Chrome instance.
/// </summary>
/// <remarks>
/// <para>
/// Off by default. When enabled it lets a single Stream Deck button start a trading session:
/// the button runs the bridge, the bridge brings Chrome up on the dedicated profile, and the
/// deck switches to its trading page.
/// </para>
/// <para>
/// It only ever starts Chrome with its own <c>--user-data-dir</c>, and never touches, closes or
/// reconfigures the operator's ordinary Chrome. Modern Chrome refuses
/// <c>--remote-debugging-port</c> on the default profile anyway, so a separate profile is a
/// requirement rather than a preference.
/// </para>
/// </remarks>
internal sealed class ChromeLauncher : IChromeLauncher, IDisposable
{
    private readonly ChromeOptions _options;
    private readonly AppPaths _paths;
    private readonly HttpClient _http;
    private readonly ILogger<ChromeLauncher> _logger;

    private long _lastAttempt;

    public ChromeLauncher(IOptions<ChromeOptions> options, AppPaths paths, ILogger<ChromeLauncher> logger)
    {
        _options = options.Value;
        _paths = paths;
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
    }

    public async Task<bool> EnsureRunningAsync(CancellationToken cancellationToken)
    {
        if (await IsEndpointAliveAsync(cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        if (!_options.AutoLaunch)
        {
            return false;
        }

        // Rate limited so a Chrome that fails to start - a bad executable path, a profile
        // directory that cannot be created - produces one launch attempt per cooldown rather
        // than a new process every time the watchdog loops.
        if (_lastAttempt != 0 && Stopwatch.GetElapsedTime(_lastAttempt) < _options.RelaunchCooldown)
        {
            return false;
        }

        _lastAttempt = Stopwatch.GetTimestamp();
        return await LaunchAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts Chrome because the operator asked for a session.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately not gated on <see cref="ChromeOptions.AutoLaunch"/>. That setting answers
    /// "may the bridge start Chrome on its own initiative", and pressing Start is the opposite of
    /// that - it is the operator's initiative, and refusing it means the button does nothing at
    /// all with no way to tell why.
    /// </para>
    /// <para>
    /// The two used to be the same thing, because the only way to launch was the watchdog. Now
    /// that the watchdog runs only while armed, the setting governs just one question: whether
    /// Chrome is put back if it disappears mid-session. Keeping the gate here made a fresh
    /// install's Start button silently do nothing.
    /// </para>
    /// </remarks>
    public async Task<bool> LaunchOnRequestAsync(CancellationToken cancellationToken)
    {
        if (await IsEndpointAliveAsync(cancellationToken).ConfigureAwait(false))
        {
            // Already there. The session is ready, which is what was actually being asked for.
            return true;
        }

        _lastAttempt = Stopwatch.GetTimestamp();
        return await LaunchAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Where Windows actually puts Chrome, in the order worth trying.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The configured path is only a default, and it names one of at least four real locations.
    /// A 32-bit Chrome installs under Program Files (x86); a per-user install - the kind that
    /// needs no administrator, and therefore the kind on a machine somebody else set up - installs
    /// under the user's own AppData. On either, a single hard-coded path means Start can never
    /// work and the operator is left hand-editing JSON to fix it.
    /// </para>
    /// <para>
    /// The registry entry comes first because it is the authoritative answer and the only one that
    /// finds a genuinely custom install location. Windows maintains it for exactly this purpose.
    /// </para>
    /// </remarks>
    internal static IEnumerable<string> CandidateExecutables()
    {
        const string AppPathsKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe";

        foreach (RegistryKey root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            string? registered = null;

            try
            {
                using RegistryKey? key = root.OpenSubKey(AppPathsKey, writable: false);
                registered = key?.GetValue(null) as string;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // A locked-down machine may refuse the read. The literal paths below still apply.
            }

            if (!string.IsNullOrWhiteSpace(registered))
            {
                yield return registered.Trim('"');
            }
        }

        foreach (Environment.SpecialFolder folder in new[]
        {
            Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.LocalApplicationData,
        })
        {
            string root = Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);

            if (!string.IsNullOrWhiteSpace(root))
            {
                yield return Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe");
            }
        }
    }

    /// <summary>The configured Chrome if it exists, otherwise wherever it actually is.</summary>
    private string? ResolveExecutable()
    {
        // Configured first, always. An operator who has named a path meant it - possibly to run a
        // specific channel - and searching past it would quietly ignore their choice.
        if (File.Exists(_options.ExecutablePath))
        {
            return _options.ExecutablePath;
        }

        foreach (string candidate in CandidateExecutables())
        {
            if (File.Exists(candidate))
            {
                _logger.ChromeFoundElsewhere(_options.ExecutablePath, candidate);
                return candidate;
            }
        }

        return null;
    }

    private async Task<bool> LaunchAsync(CancellationToken cancellationToken)
    {
        string profile = string.IsNullOrWhiteSpace(_options.UserDataDirectory)
            ? _paths.ChromeProfile
            : _options.UserDataDirectory;

        if (ResolveExecutable() is not { } executable)
        {
            // Names everywhere that was tried, so the log answers "where should I have put it"
            // rather than only "it was not where I looked".
            _logger.ChromeLaunchFailed(
                $"Chrome was not found. Tried {_options.ExecutablePath}, "
                + string.Join(", ", CandidateExecutables().Distinct(StringComparer.OrdinalIgnoreCase))
                + ". Set Chrome:ExecutablePath in appsettings.json if it is somewhere else.");

            return false;
        }

        try
        {
            Directory.CreateDirectory(profile);

            var startInfo = new ProcessStartInfo(executable) { UseShellExecute = false };
            startInfo.ArgumentList.Add($"--remote-debugging-port={_options.CdpPort}");
            startInfo.ArgumentList.Add($"--user-data-dir={profile}");
            startInfo.ArgumentList.Add("--no-first-run");
            startInfo.ArgumentList.Add("--no-default-browser-check");
            startInfo.ArgumentList.Add(_options.StartUrl);

            using Process? process = Process.Start(startInfo);

            if (process is null)
            {
                _logger.ChromeLaunchFailed("Chrome did not start.");
                return false;
            }

            _logger.ChromeLaunching(profile);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            _logger.ChromeLaunchFailed(ex.Message);
            return false;
        }

        // Chrome opens its DevTools port a moment after the process starts. Polling briefly here
        // means the very first connection attempt usually succeeds, rather than failing and
        // waiting out a backoff delay the operator would feel as a slow start.
        return await WaitForEndpointAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> WaitForEndpointAsync(CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            if (await IsEndpointAliveAsync(cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private async Task<bool> IsEndpointAliveAsync(CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await _http
                .GetAsync(new Uri(new Uri(_options.CdpEndpoint), "/json/version"), cancellationToken)
                .ConfigureAwait(false);

            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            return false;
        }
    }

    public void Dispose() => _http.Dispose();
}
