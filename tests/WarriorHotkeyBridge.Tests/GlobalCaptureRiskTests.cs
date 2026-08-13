using WarriorHotkeyBridge.Hotkeys;

namespace WarriorHotkeyBridge.Tests;

/// <summary>
/// Covers the warning for a hotkey that would take a key needed for typing.
/// </summary>
/// <remarks>
/// This came out of a real session: a bare <c>D</c> was configured as a hotkey and fired nine
/// times before anyone noticed, because a global hotkey is captured in every application - so the
/// letter D became untypable machine-wide and every attempt to type it ran a trading binding.
/// It is legal, Windows allows it, and it is never what someone configuring a Stream Deck meant.
/// </remarks>
public class GlobalCaptureRiskTests
{
    [Theory]
    [InlineData("D")]
    [InlineData("A")]
    [InlineData("1")]
    [InlineData("D1")]
    [InlineData("Space")]
    [InlineData("NumPad5")]
    [InlineData("OemQuestion")]
    public void WarnsAboutBarePrintableKeys(string text)
    {
        Assert.True(HotkeyGesture.TryParse(text, out HotkeyGesture gesture, out _));
        Assert.NotNull(gesture.DescribeGlobalCaptureRisk());
    }

    /// <summary>
    /// The keys a Stream Deck actually sends. No physical keyboard has them, so taking them
    /// globally costs the operator nothing - which is the entire reason the product uses them.
    /// </summary>
    [Theory]
    [InlineData("F13")]
    [InlineData("F19")]
    [InlineData("F24")]
    [InlineData("F1")]
    [InlineData("Escape")]
    [InlineData("Up")]
    [InlineData("Home")]
    public void DoesNotWarnAboutKeysThatAreNotUsedForTyping(string text)
    {
        Assert.True(HotkeyGesture.TryParse(text, out HotkeyGesture gesture, out _));
        Assert.Null(gesture.DescribeGlobalCaptureRisk());
    }

    /// <summary>
    /// A modifier is what makes a printable key safe to take: Ctrl+Alt+D does not stop D being
    /// typed, so there is nothing to warn about.
    /// </summary>
    [Theory]
    [InlineData("Ctrl+D")]
    [InlineData("Alt+D")]
    [InlineData("Shift+D")]
    [InlineData("Ctrl+Alt+D")]
    [InlineData("Ctrl+Shift+1")]
    public void DoesNotWarnWhenAModifierIsPresent(string text)
    {
        Assert.True(HotkeyGesture.TryParse(text, out HotkeyGesture gesture, out _));
        Assert.Null(gesture.DescribeGlobalCaptureRisk());
    }

    /// <summary>The message has to say what will happen and what to do instead.</summary>
    [Fact]
    public void TheWarningExplainsTheConsequenceAndTheFix()
    {
        Assert.True(HotkeyGesture.TryParse("D", out HotkeyGesture gesture, out _));

        string warning = gesture.DescribeGlobalCaptureRisk()!;

        Assert.Contains("every application", warning, StringComparison.Ordinal);
        Assert.Contains("F13", warning, StringComparison.Ordinal);
    }
}
