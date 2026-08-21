using System.Diagnostics.CodeAnalysis;

namespace WarriorHotkeyBridge.Warrior;

/// <summary>
/// The target-identity rules, kept pure so they can be exhaustively unit tested without Chrome.
/// </summary>
/// <remarks>
/// This is the one place that decides whether a page may receive trading input, so it fails
/// closed on anything it cannot positively identify.
/// </remarks>
internal static class WarriorTargetValidator
{
    /// <summary>
    /// True only when the URL parses and its host is <em>exactly</em> one of the allowed hosts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Substring tests are the trap this exists to avoid. The operator legitimately has
    /// <c>chatroom.warriortrading.com</c> open, and hostile look-alikes such as
    /// <c>sim.warriortrading.com.evil.test</c> or userinfo tricks like
    /// <c>https://sim.warriortrading.com@evil.test/</c> both contain an allowed host as a
    /// substring while resolving somewhere else entirely. Parsing the URI and comparing
    /// <see cref="Uri.Host"/> defeats all of them.
    /// </para>
    /// <para>
    /// A <em>list</em> rather than one host, because Warrior moved the SIM from
    /// <c>sim.warriortrading.com</c> to <c>sim2.warriortrading.com</c> overnight and every key
    /// stopped working - the bridge connected to Chrome, found no page it was allowed to touch,
    /// and said so. Each entry is still an exact match; widening to a wildcard or a suffix test
    /// would trade a day of downtime for a permanent hole.
    /// </para>
    /// </remarks>
    public static bool IsAllowedHost([NotNullWhen(true)] string? url, IReadOnlyList<string> allowedHosts)
    {
        if (string.IsNullOrWhiteSpace(url) || allowedHosts is null || allowedHosts.Count == 0)
        {
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        // about:, file:, devtools:, chrome-extension: are never trading targets.
        if (uri.Scheme is not ("http" or "https"))
        {
            return false;
        }

        foreach (string allowedHost in allowedHosts)
        {
            if (!string.IsNullOrWhiteSpace(allowedHost)
                && string.Equals(uri.Host, allowedHost, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc cref="IsAllowedHost(string?, IReadOnlyList{string})"/>
    public static bool IsAllowedHost([NotNullWhen(true)] string? url, string allowedHost) =>
        IsAllowedHost(url, new[] { allowedHost });

    /// <summary>True when the page title contains the expected marker.</summary>
    public static bool TitleMatches(string? title, string expectedTitle) =>
        !string.IsNullOrWhiteSpace(title)
        && !string.IsNullOrWhiteSpace(expectedTitle)
        && title.Contains(expectedTitle, StringComparison.OrdinalIgnoreCase);
}
