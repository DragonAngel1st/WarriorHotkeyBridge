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
    /// True only when the URL parses and its host is <em>exactly</em> the allowed host.
    /// </summary>
    /// <remarks>
    /// Substring tests are the trap this exists to avoid. The operator legitimately has
    /// <c>chatroom.warriortrading.com</c> open, and hostile look-alikes such as
    /// <c>sim.warriortrading.com.evil.test</c> or userinfo tricks like
    /// <c>https://sim.warriortrading.com@evil.test/</c> both contain the allowed host as a
    /// substring while resolving somewhere else entirely. Parsing the URI and comparing
    /// <see cref="Uri.Host"/> defeats all of them.
    /// </remarks>
    public static bool IsAllowedHost([NotNullWhen(true)] string? url, string allowedHost)
    {
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(allowedHost))
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

        return string.Equals(uri.Host, allowedHost, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when the page title contains the expected marker.</summary>
    public static bool TitleMatches(string? title, string expectedTitle) =>
        !string.IsNullOrWhiteSpace(title)
        && !string.IsNullOrWhiteSpace(expectedTitle)
        && title.Contains(expectedTitle, StringComparison.OrdinalIgnoreCase);
}
