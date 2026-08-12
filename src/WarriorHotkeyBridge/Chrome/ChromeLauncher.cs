using System.Diagnostics;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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

    public async Task<bool> LaunchOnRequestAsync(CancellationToken cancellationToken)
    {
        if (await IsEndpointAliveAsync(cancellationToken).ConfigureAwait(false))
        {
            // Already there. The session is ready, which is what was actually being asked for.
            return true;
        }

        if (!_options.AutoLaunch)
        {
            return false;
        }

        _lastAttempt = Stopwatch.GetTimestamp();
        return await LaunchAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> LaunchAsync(CancellationToken cancellationToken)
    {
        string profile = string.IsNullOrWhiteSpace(_options.UserDataDirectory)
            ? _paths.ChromeProfile
            : _options.UserDataDirectory;

        if (!File.Exists(_options.ExecutablePath))
        {
            _logger.ChromeLaunchFailed($"Chrome was not found at {_options.ExecutablePath}");
            return false;
        }

        try
        {
            Directory.CreateDirectory(profile);

            var startInfo = new ProcessStartInfo(_options.ExecutablePath) { UseShellExecute = false };
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
