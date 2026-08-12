using System.ComponentModel.DataAnnotations;

namespace WarriorHotkeyBridge.Configuration;

/// <summary>
/// Identity rules for the Warrior Trading SIM page and its Level 2 component.
/// These values are the application's safety boundary: a command is only ever
/// dispatched to a page that satisfies all of them.
/// </summary>
internal sealed class WarriorSimOptions
{
    public const string SectionName = "WarriorSim";

    /// <summary>
    /// Exact host that a page must have to be considered a Warrior SIM page.
    /// Compared with <see cref="StringComparison.OrdinalIgnoreCase"/> against
    /// <see cref="Uri.Host"/> - never a substring match.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string AllowedHost { get; init; } = "sim.warriortrading.com";

    /// <summary>Substring the page title is expected to contain.</summary>
    [Required(AllowEmptyStrings = false)]
    public string ExpectedTitle { get; init; } = "Sim Trading Platform";

    /// <summary>
    /// Ordered Level 2 selectors, most stable first. <c>data-layout-path</c> is deliberately
    /// not used: FlexLayout rewrites those paths whenever the user rearranges the layout.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Plain CSS only. Every match is filtered by <see cref="Level2TabText"/> anyway, so the
    /// fallback can be the generic tab-button class rather than a Playwright-specific
    /// <c>:has-text()</c> selector, which the DOM probe could not evaluate.
    /// </para>
    /// <para>
    /// Defaults deliberately live in <see cref="DefaultLevel2Selectors"/> rather than as an
    /// initialiser here. The configuration binder APPENDS to an array property that already has
    /// a value, so a non-empty default is concatenated with whatever appsettings.json supplies -
    /// which both duplicates the shipped list and makes an operator override add to the defaults
    /// instead of replacing them. Read <see cref="EffectiveLevel2Selectors"/>, never this.
    /// </para>
    /// </remarks>
    public string[] Level2Selectors { get; init; } = [];

    /// <summary>Used when configuration supplies no selectors at all.</summary>
    public static readonly string[] DefaultLevel2Selectors =
    [
        ".widget-t-level2",
        "div.flexlayout__tab_button",
    ];

    /// <summary>The selectors to actually use, in order.</summary>
    public string[] EffectiveLevel2Selectors =>
        Level2Selectors.Length > 0 ? Level2Selectors : DefaultLevel2Selectors;

    /// <summary>Text the matched tab must actually contain, to guard against a selector collision.</summary>
    [Required(AllowEmptyStrings = false)]
    public string Level2TabText { get; init; } = "Level 2 & Order Entry";

    /// <summary>Class FlexLayout puts on the selected tab button.</summary>
    [Required(AllowEmptyStrings = false)]
    public string SelectedTabButtonClass { get; init; } = "flexlayout__tab_button--selected";

    /// <summary>
    /// Class FlexLayout puts on the tabbar of the active tabset.
    /// </summary>
    /// <remarks>
    /// Verified on a live dashboard: exactly one element document-wide carries this at a time,
    /// even though the page hosts six separate FlexLayout instances, and clicking a tab header
    /// moves it. That makes it the authoritative "which component has the keyboard" signal —
    /// unlike the per-tabset <see cref="SelectedTabButtonClass"/>, which several tabs carry at once.
    /// </remarks>
    [Required(AllowEmptyStrings = false)]
    [RegularExpression(@"^[A-Za-z0-9_\-]+$", ErrorMessage = "Must be a single CSS class name.")]
    public string SelectedTabsetClass { get; init; } = "flexlayout__tabset-selected";

    /// <summary>
    /// Class of the tab bar element that carries <see cref="SelectedTabsetClass"/>.
    /// </summary>
    /// <remarks>
    /// Needed because it is not the tab button's immediate parent: FlexLayout nests
    /// <c>..._tabbar_inner_tab_container</c> and <c>..._tabbar_inner</c> in between, and both of
    /// those match a loose "contains flexlayout__tabset" test while never carrying the selected
    /// class. Constrained to a bare class name so it cannot inject XPath.
    /// </remarks>
    [Required(AllowEmptyStrings = false)]
    [RegularExpression(@"^[A-Za-z0-9_\-]+$", ErrorMessage = "Must be a single CSS class name.")]
    public string TabsetTabBarClass { get; init; } = "flexlayout__tabset_tabbar_outer";

    /// <summary>
    /// Class of the label element inside a tab button, used as the click target.
    /// </summary>
    /// <remarks>
    /// Aiming at the label rather than the tab keeps the click on text, well away from a close
    /// button or drag affordance at the tab's edge.
    /// </remarks>
    [Required(AllowEmptyStrings = false)]
    [RegularExpression(@"^[A-Za-z0-9_\-]+$", ErrorMessage = "Must be a single CSS class name.")]
    public string TabButtonContentClass { get; init; } = "flexlayout__tab_button_content";

    /// <summary>
    /// Timeout for a single DOM interaction during Level 2 targeting, in milliseconds.
    /// Deliberately short: this sits on the hotkey path, and a slow answer is a failure worth
    /// reporting rather than something to wait out.
    /// </summary>
    [Range(100, 10_000)]
    public float SelectionTimeoutMs { get; init; } = 1_000;

    /// <summary>
    /// Ceiling on a single DOM probe, in milliseconds.
    /// </summary>
    /// <remarks>
    /// Playwright's <c>EvaluateAsync</c> has no timeout of its own, so this is the only thing
    /// standing between a wedged renderer and a permanently blocked command queue.
    /// </remarks>
    [Range(200, 30_000)]
    public int ProbeTimeoutMs { get; init; } = 2_000;

    public TimeSpan ProbeTimeout => TimeSpan.FromMilliseconds(ProbeTimeoutMs);
}
