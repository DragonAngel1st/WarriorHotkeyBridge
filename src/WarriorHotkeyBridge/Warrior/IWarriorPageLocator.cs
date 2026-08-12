using Microsoft.Playwright;

namespace WarriorHotkeyBridge.Warrior;

internal enum WarriorPageStatus
{
    /// <summary>Chrome is not connected, so no page could be examined.</summary>
    NotConnected,

    /// <summary>Chrome is connected but no open page passed target validation.</summary>
    PageNotFound,

    /// <summary>A validated Warrior SIM page was selected.</summary>
    PageFound,
}

/// <summary>
/// One page considered during location, kept for diagnostics.
/// </summary>
/// <remarks>
/// Records host and path but deliberately never the query string or fragment, which are
/// exactly where session tokens travel. Diagnostics must be safe to paste into a support
/// thread.
/// </remarks>
internal sealed record WarriorPageCandidate(
    string Host,
    string Path,
    string Title,
    bool HostMatches,
    bool TitleMatches,
    bool HasLevel2,
    bool IsVisible)
{
    /// <summary>
    /// True only when every identity check passed, including carrying a Level 2 component.
    /// </summary>
    /// <remarks>
    /// Requiring Level 2 is what separates the trading dashboard from the SIM's other pages -
    /// scanner and alert popouts share its host, path and title, and differ only in what they
    /// contain. Selecting on capability also means the target follows the panel if it is ever
    /// popped out into its own window, with no configuration change.
    /// </remarks>
    public bool IsEligible => HostMatches && TitleMatches && HasLevel2;

    public string Describe() =>
        $"{Host}{Path} \"{Title}\" [host={(HostMatches ? "ok" : "no")}, title={(TitleMatches ? "ok" : "no")}, "
        + $"level2={(HasLevel2 ? "yes" : "no")}, visible={IsVisible}]";
}

internal sealed record WarriorPageResult
{
    public required WarriorPageStatus Status { get; init; }

    /// <summary>The selected page. Non-null exactly when <see cref="Status"/> is PageFound.</summary>
    public IPage? Page { get; init; }

    /// <summary>Every page examined, in enumeration order.</summary>
    public IReadOnlyList<WarriorPageCandidate> Candidates { get; init; } = [];

    /// <summary>Why location failed, or how the winner was chosen among several.</summary>
    public string? Reason { get; init; }

    /// <summary>True when more than one page passed validation and one had to be chosen.</summary>
    public bool WasAmbiguous { get; init; }

    /// <summary>
    /// True when a DOM probe could not be executed, as distinct from running and finding
    /// nothing. Only this says anything about whether the connection is alive.
    /// </summary>
    public bool ProbeFailed { get; init; }
}

/// <summary>
/// Finds the Warrior SIM page among everything open in Chrome.
/// </summary>
/// <remarks>
/// This is the application's safety boundary. A page is only ever a candidate if its URI host
/// is exactly the configured host - never a substring match, because the operator legitimately
/// has <c>chatroom.warriortrading.com</c> open, which any "contains warrior" test would accept.
/// </remarks>
internal interface IWarriorPageLocator
{
    Task<WarriorPageResult> LocateAsync(CancellationToken cancellationToken);
}
