using WarriorHotkeyBridge.Configuration;
using WarriorHotkeyBridge.Hotkeys;
using WarriorHotkeyBridge.Models;

namespace WarriorHotkeyBridge.Tests;

public class HotkeyBindingResolverTests
{
    private static HotkeyBindingResolution Resolve(params (string Gesture, HotkeyBindingConfig Config)[] entries)
    {
        Dictionary<string, HotkeyBindingConfig> map = [];

        foreach ((string gesture, HotkeyBindingConfig config) in entries)
        {
            map[gesture] = config;
        }

        return HotkeyBindingResolver.Resolve(map);
    }

    [Fact]
    public void Resolve_MapsASendBindingToAPassThroughChord()
    {
        HotkeyBindingResolution result = Resolve(("F13", new HotkeyBindingConfig
        {
            Send = "Shift+1",
            Label = "Buy 75% BP",
        }));

        Assert.Empty(result.Problems);
        HotkeyBinding binding = Assert.Single(result.Bindings);
        Assert.Equal(HotkeyActionKind.SendKeys, binding.Action.Kind);
        Assert.Equal("Shift+1", binding.Action.Keys);
        Assert.Equal("Buy 75% BP", binding.Action.Label);
        Assert.True(binding.Action.DispatchesInput);
    }

    [Theory]
    [InlineData("ctrl+shift+1", "Control+Shift+1")]
    [InlineData("Ctrl+1", "Control+1")]
    [InlineData("  shift + Digit1  ", "Shift+Digit1")]
    [InlineData("win+2", "Meta+2")]
    [InlineData("cmd+a", "Meta+a")]
    [InlineData("Shift+1", "Shift+1")]
    [InlineData("Enter", "Enter")]
    public void Resolve_NormalisesModifierNamesForPlaywright(string send, string expected)
    {
        // Playwright's modifier vocabulary is Control/Alt/Shift/Meta; operators type Ctrl and Win.
        HotkeyBindingResolution result = Resolve(("F13", new HotkeyBindingConfig { Send = send }));

        Assert.Empty(result.Problems);
        Assert.Equal(expected, Assert.Single(result.Bindings).Action.Keys);
    }

    /// <summary>
    /// Every one of these would have registered cleanly and then failed at dispatch, which is
    /// the worst possible time to discover an unusable chord.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Shift+")]
    [InlineData("Shift")]
    [InlineData("Ctrl+Alt")]
    [InlineData("Hyper+1")]
    [InlineData("F13")]              // valid as a bridge hotkey, but no browser key mapping
    [InlineData("Shift+F19")]
    [InlineData("digit1")]           // Playwright's key names are case sensitive
    [InlineData("Shift+digit1")]
    [InlineData("keya")]
    [InlineData("Ctrl+NotAKey")]
    public void Resolve_RejectsUnusableSendExpressions(string send)
    {
        HotkeyBindingResolution result = Resolve(("F13", new HotkeyBindingConfig { Send = send }));

        Assert.Empty(result.Bindings);
        Assert.Single(result.Problems);
    }

    [Fact]
    public void Resolve_MapsBuiltInActionsByExactNameRegardlessOfCase()
    {
        // The action enum is internal, so it cannot appear in a public theory signature.
        (string Configured, HotkeyActionKind Expected)[] cases =
        [
            ("Test", HotkeyActionKind.Test),
            ("diagnostics", HotkeyActionKind.Diagnostics),
            ("  TEST  ", HotkeyActionKind.Test),
        ];

        foreach ((string configured, HotkeyActionKind expected) in cases)
        {
            HotkeyBindingResolution result = Resolve(("F13", new HotkeyBindingConfig { Action = configured }));

            Assert.Empty(result.Problems);
            HotkeyBinding binding = Assert.Single(result.Bindings);
            Assert.Equal(expected, binding.Action.Kind);
            Assert.False(binding.Action.DispatchesInput);
            Assert.Null(binding.Action.Keys);
        }
    }

    /// <summary>
    /// Guards the whole class of values <c>Enum.TryParse</c> would have accepted beyond a bare
    /// member name — the underlying number, a signed number, and comma-separated lists that get
    /// OR-ed together even for a non-flags enum.
    /// </summary>
    [Theory]
    [InlineData("SendKeys")]              // selected via Send, never nameable as an Action
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("+1")]
    [InlineData("-1")]
    [InlineData("Test,Diagnostics")]
    [InlineData("Test, Diagnostics")]
    [InlineData("Nonsense")]
    public void Resolve_RejectsAnythingThatIsNotAnExactActionName(string action)
    {
        HotkeyBindingResolution result = Resolve(("F13", new HotkeyBindingConfig { Action = action }));

        Assert.Empty(result.Bindings);
        Assert.Single(result.Problems);
    }

    /// <summary>
    /// Ambiguity here could either fire an unintended order or swallow an intended one, so it
    /// is rejected rather than resolved by precedence.
    /// </summary>
    [Fact]
    public void Resolve_RejectsABindingThatSetsBothSendAndAction()
    {
        HotkeyBindingResolution result = Resolve(("F13", new HotkeyBindingConfig
        {
            Send = "Shift+1",
            Action = "Test",
        }));

        Assert.Empty(result.Bindings);
        Assert.Contains(result.Problems, p => p.Contains("both", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Resolve_RejectsAnEmptyBinding()
    {
        HotkeyBindingResolution result = Resolve(("F13", new HotkeyBindingConfig()));

        Assert.Empty(result.Bindings);
        Assert.Single(result.Problems);
    }

    [Fact]
    public void Resolve_RejectsANegativeLevel2Index()
    {
        HotkeyBindingResolution result = Resolve(("F13", new HotkeyBindingConfig
        {
            Send = "Shift+1",
            Level2Index = -1,
        }));

        Assert.Empty(result.Bindings);
        Assert.Single(result.Problems);
    }

    [Fact]
    public void Resolve_CarriesLevel2IndexForMultiPanelLayouts()
    {
        HotkeyBindingResolution result = Resolve(("F13", new HotkeyBindingConfig
        {
            Send = "Shift+1",
            Level2Index = 1,
        }));

        Assert.Empty(result.Problems);
        Assert.Equal(1, Assert.Single(result.Bindings).Action.Level2Index);
    }

    [Fact]
    public void Resolve_ReportsInvalidGesture()
    {
        HotkeyBindingResolution result = Resolve(("NotAKey", new HotkeyBindingConfig { Action = "Test" }));

        Assert.Empty(result.Bindings);
        Assert.Contains(result.Problems, p => p.Contains("NotAKey", StringComparison.Ordinal));
    }

    [Fact]
    public void Resolve_KeepsFirstOfDuplicateGesturesAndReportsTheRest()
    {
        // Textually distinct configuration keys that mean the same physical gesture; Windows
        // would only grant the first registration.
        HotkeyBindingResolution result = Resolve(
            ("F13", new HotkeyBindingConfig { Action = "Test" }),
            (" F13 ", new HotkeyBindingConfig { Send = "Shift+1" }));

        HotkeyBinding binding = Assert.Single(result.Bindings);
        Assert.Equal(HotkeyActionKind.Test, binding.Action.Kind);
        Assert.Single(result.Problems);
    }

    [Fact]
    public void Resolve_ContinuesAfterABadEntry()
    {
        // One mistyped line must not cost the operator every other hotkey.
        HotkeyBindingResolution result = Resolve(
            ("Nonsense", new HotkeyBindingConfig { Action = "Test" }),
            ("F14", new HotkeyBindingConfig { Send = "Shift+2" }));

        Assert.Equal("Shift+2", Assert.Single(result.Bindings).Action.Keys);
        Assert.Single(result.Problems);
    }

    [Fact]
    public void Resolve_HandlesEmptyConfiguration()
    {
        HotkeyBindingResolution result = HotkeyBindingResolver.Resolve(new Dictionary<string, HotkeyBindingConfig>());

        Assert.Empty(result.Bindings);
        Assert.Empty(result.Problems);
    }

    /// <summary>
    /// Both traps deliver a different key than the configuration appears to say, so both must
    /// be flagged rather than silently accepted.
    /// </summary>
    [Theory]
    [InlineData("Shift+1", "Shift+Digit1")]        // delivers event.key '1', not '!'
    [InlineData("Shift+2", "Shift+Digit2")]
    [InlineData("Numpad1", "End")]                 // unshifted numpad carries navigation meaning
    [InlineData("Numpad5", "Clear")]
    [InlineData("NumpadDecimal", "Delete")]
    public void DescribeAmbiguity_WarnsAboutKeysThatDeliverSomethingElse(string expression, string mentions)
    {
        Assert.True(PlaywrightKeys.TryNormalize(expression, out string? normalized, out _));

        string? warning = PlaywrightKeys.DescribeAmbiguity(normalized);

        Assert.NotNull(warning);
        Assert.Contains(mentions, warning, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Shift+Digit1")]    // the faithful spelling
    [InlineData("Control+KeyA")]
    [InlineData("Enter")]
    [InlineData("Shift+KeyA")]      // Shift with a letter is unambiguous
    [InlineData("Shift+Numpad1")]   // shifted numpad does deliver the digit
    public void DescribeAmbiguity_StaysQuietForUnambiguousExpressions(string expression)
    {
        Assert.True(PlaywrightKeys.TryNormalize(expression, out string? normalized, out _));

        Assert.Null(PlaywrightKeys.DescribeAmbiguity(normalized));
    }

    [Fact]
    public void Describe_ShowsChordAndOperatorLabel()
    {
        var withLabel = new HotkeyAction { Kind = HotkeyActionKind.SendKeys, Keys = "Shift+1", Label = "Buy 75% BP" };
        var withoutLabel = new HotkeyAction { Kind = HotkeyActionKind.SendKeys, Keys = "Shift+1" };
        var builtIn = new HotkeyAction { Kind = HotkeyActionKind.Test };

        Assert.Equal("Shift+1 (Buy 75% BP)", withLabel.Describe());
        Assert.Equal("Shift+1", withoutLabel.Describe());
        Assert.Equal("Test", builtIn.Describe());
    }
}
