using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;

namespace WarriorHotkeyBridge.Configuration;

/// <summary>
/// How the bridge reaches Chrome over the DevTools Protocol.
/// </summary>
internal sealed class ChromeOptions : IValidatableObject
{
    public const string SectionName = "Chrome";

    /// <summary>
    /// CDP endpoint of the dedicated Warrior Chrome instance, e.g. <c>http://127.0.0.1:9222</c>.
    /// Loopback is used deliberately: the DevTools endpoint is unauthenticated, so it must
    /// never be exposed on a routable interface.
    /// </summary>
    /// <remarks>
    /// Validated by <see cref="Validate"/> as a URL the framework can actually parse, not merely
    /// as non-empty and not by pattern match. The launcher derives Chrome's
    /// <c>--remote-debugging-port</c> from this same value, so an endpoint that passed validation
    /// but failed to parse would launch Chrome on one port while the bridge connected to another
    /// - a failure that presents as "Chrome started but never connects".
    /// </remarks>
    [Required(AllowEmptyStrings = false)]
    public string CdpEndpoint { get; init; } = "http://127.0.0.1:9222";

    /// <summary>
    /// Port taken from <see cref="CdpEndpoint"/>. Validation guarantees this is reachable only
    /// for an endpoint that parses, so the zero is unreachable in a configured application.
    /// </summary>
    public int CdpPort => TryParseEndpoint(CdpEndpoint, out Uri? uri) ? uri.Port : 0;

    /// <summary>
    /// The single definition of a usable CDP endpoint, shared by validation and by the port the
    /// launcher hands to Chrome so the two can never disagree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Uri"/> is the authority rather than a regular expression, because the only
    /// property that matters is "will the code that consumes this be able to parse it" - and the
    /// only way to answer that honestly is to run the same parser. A pattern is a second,
    /// independent opinion about URL syntax, and where the two disagree the pattern is wrong by
    /// definition.
    /// </para>
    /// <para>
    /// An earlier attempt did use a pattern, and demonstrated the point. Its host term consumed
    /// the <c>:port</c> substring, so the port-range group it was paired with never matched
    /// anything: <c>http://127.0.0.1:999999</c> passed validation, then failed to parse, and the
    /// port silently fell back to a default the bridge was not connecting to - precisely the
    /// failure the validation existed to prevent. It also rejected <c>HTTP://</c>, which is a
    /// perfectly legal spelling.
    /// </para>
    /// </remarks>
    internal static bool TryParseEndpoint(string? value, [NotNullWhen(true)] out Uri? endpoint)
    {
        endpoint = null;

        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? parsed))
        {
            return false;
        }

        // Anything else - file, ftp, a Windows drive letter parsed as a scheme - cannot carry
        // the DevTools protocol, and Uri would hand back a meaningless port for it.
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        if (parsed.Port is <= 0 or > 65535)
        {
            return false;
        }

        endpoint = parsed;
        return true;
    }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!TryParseEndpoint(CdpEndpoint, out _))
        {
            yield return new ValidationResult(
                $"Chrome:CdpEndpoint '{CdpEndpoint}' is not a usable endpoint. It must be an "
                + "absolute http or https URL with a valid port, for example http://127.0.0.1:9222.",
                [nameof(CdpEndpoint)]);
        }
    }

    /// <summary>Full path to chrome.exe, used only if <see cref="AutoLaunch"/> is enabled.</summary>
    public string ExecutablePath { get; init; } = @"C:\Program Files\Google\Chrome\Application\chrome.exe";

    /// <summary>
    /// Dedicated Chrome profile directory. Modern Chrome refuses <c>--remote-debugging-port</c>
    /// against the default profile, so a separate user-data-dir is mandatory. Empty means
    /// "use %LOCALAPPDATA%\WarriorHotkeyBridge\ChromeProfile".
    /// </summary>
    public string UserDataDirectory { get; init; } = string.Empty;

    /// <summary>
    /// Whether the watchdog puts Chrome back if it disappears during an armed session.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Off by default, and that is the point: closing the browser yourself should keep it closed.
    /// With it on, the watchdog relaunches within its cooldown, which is what made an earlier
    /// build feel like it was fighting the operator.
    /// </para>
    /// <para>
    /// This does <b>not</b> gate Start. Pressing Start - from the tray or a deck key - always
    /// launches Chrome, because that is the operator asking rather than the bridge deciding.
    /// The two were once the same thing, and leaving them so meant a fresh install's Start button
    /// silently did nothing at all.
    /// </para>
    /// </remarks>
    public bool AutoLaunch { get; init; }

    /// <summary>
    /// Page the auto-launched Chrome opens.
    /// </summary>
    /// <remarks>
    /// The member login page, not the SIM. Reaching the SIM requires a per-session <c>hash</c>
    /// query parameter that the member dashboard mints during sign-in, so its URL cannot be
    /// navigated to directly from a cold start. Landing on the login page puts the operator one
    /// click from the chain that produces it - and if the session is somehow still alive, the
    /// login page simply redirects onward.
    /// </remarks>
    [Required(AllowEmptyStrings = false)]
    public string StartUrl { get; init; } = "https://www.warriortrading.com/login-member/";

    /// <summary>
    /// Minimum gap between launch attempts, in seconds.
    /// </summary>
    /// <remarks>
    /// Without this, a Chrome that cannot start - wrong executable path, unwritable profile -
    /// would spawn a new process on every watchdog pass.
    /// </remarks>
    [Range(5, 600)]
    public int RelaunchCooldownSeconds { get; init; } = 30;

    public TimeSpan RelaunchCooldown => TimeSpan.FromSeconds(RelaunchCooldownSeconds);

    [Range(250, 60_000)]
    public int ConnectTimeoutMs { get; init; } = 5_000;

    [Range(100, 60_000)]
    public int ReconnectInitialDelayMs { get; init; } = 1_000;

    [Range(1_000, 300_000)]
    public int ReconnectMaxDelayMs { get; init; } = 30_000;

    /// <summary>
    /// How often the watchdog verifies the connection while connected. Deliberately seconds,
    /// not milliseconds: polling the DOM aggressively would cost latency for no benefit.
    /// </summary>
    [Range(500, 60_000)]
    public int HealthCheckIntervalMs { get; init; } = 3_000;

    public TimeSpan ConnectTimeout => TimeSpan.FromMilliseconds(ConnectTimeoutMs);

    public TimeSpan ReconnectInitialDelay => TimeSpan.FromMilliseconds(ReconnectInitialDelayMs);

    public TimeSpan ReconnectMaxDelay => TimeSpan.FromMilliseconds(ReconnectMaxDelayMs);

    /// <summary>
    /// Consecutive failed health checks before the connection is torn down and rebuilt.
    /// </summary>
    /// <remarks>
    /// Guards against a zombie connection: after a laptop resumes, or when a proxy drops an
    /// idle socket, <c>IsConnected</c> can still report true while nothing actually gets
    /// through. Without this the bridge would keep believing it was connected and never
    /// reconnect. A small number rather than one, so a single blocked main thread on a busy
    /// page does not throw away a perfectly good connection.
    /// </remarks>
    [Range(1, 20)]
    public int HealthFailuresBeforeReconnect { get; init; } = 3;

    public TimeSpan HealthCheckInterval => TimeSpan.FromMilliseconds(HealthCheckIntervalMs);
}
