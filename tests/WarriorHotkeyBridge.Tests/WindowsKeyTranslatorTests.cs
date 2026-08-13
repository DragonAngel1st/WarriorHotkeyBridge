using WarriorHotkeyBridge.Hotkeys;

namespace WarriorHotkeyBridge.Tests;

/// <summary>
/// Covers turning a keypress into the expression Playwright will send.
/// </summary>
/// <remarks>
/// The point of capture is that pressing a key produces the spelling a person would get wrong,
/// so the cases that matter most are the ones the documentation has to explain: a physical
/// Shift+1 becoming Shift+Digit1 rather than Shift+1, and letters becoming KeyQ rather than Q.
/// </remarks>
public class WindowsKeyTranslatorTests
{
    /// <summary>
    /// The reason this feature exists. Typing produces Shift+1, which delivers event.key '1' -
    /// something a real keyboard never sends. Pressing the same keys must produce Shift+Digit1,
    /// which delivers '!' exactly as the hardware does.
    /// </summary>
    [Fact]
    public void APhysicalShiftAndOneBecomesShiftDigit1()
    {
        Assert.True(WindowsKeyTranslator.TryTranslate(Keys.D1 | Keys.Shift, out string? expression, out _));
        Assert.Equal("Shift+Digit1", expression);
    }

    [Theory]
    [InlineData(Keys.D1, "Digit1")]
    [InlineData(Keys.D0, "Digit0")]
    [InlineData(Keys.A, "KeyA")]
    [InlineData(Keys.Q, "KeyQ")]
    [InlineData(Keys.Z, "KeyZ")]
    [InlineData(Keys.F1, "F1")]
    [InlineData(Keys.F12, "F12")]
    [InlineData(Keys.NumPad5, "Numpad5")]
    [InlineData(Keys.Enter, "Enter")]
    [InlineData(Keys.Tab, "Tab")]
    [InlineData(Keys.Space, "Space")]
    [InlineData(Keys.Back, "Backspace")]
    [InlineData(Keys.Up, "ArrowUp")]
    [InlineData(Keys.PageDown, "PageDown")]
    [InlineData(Keys.OemQuestion, "Slash")]
    [InlineData(Keys.Oemtilde, "Backquote")]
    [InlineData(Keys.OemMinus, "Minus")]
    [InlineData(Keys.Oemplus, "Equal")]
    [InlineData(Keys.Add, "NumpadAdd")]
    public void MapsKeysToTheirBrowserNames(Keys key, string expected)
    {
        Assert.True(WindowsKeyTranslator.TryTranslate(key, out string? expression, out _));
        Assert.Equal(expected, expression);
    }

    [Theory]
    [InlineData(Keys.Q | Keys.Control, "Control+KeyQ")]
    [InlineData(Keys.X | Keys.Control, "Control+KeyX")]
    [InlineData(Keys.D5 | Keys.Shift, "Shift+Digit5")]
    [InlineData(Keys.A | Keys.Control | Keys.Shift, "Control+Shift+KeyA")]
    [InlineData(Keys.A | Keys.Alt | Keys.Control, "Control+Alt+KeyA")]
    [InlineData(Keys.F4 | Keys.Control | Keys.Alt | Keys.Shift, "Control+Alt+Shift+F4")]
    public void ComposesModifiersInAFixedOrder(Keys key, string expected)
    {
        Assert.True(WindowsKeyTranslator.TryTranslate(key, out string? expression, out _));
        Assert.Equal(expected, expression);
    }

    /// <summary>
    /// These are the keys a Stream Deck sends, so an operator will press one here expecting it to
    /// work. It has to fail with an explanation that points them at the right column.
    /// </summary>
    [Theory]
    [InlineData(Keys.F13)]
    [InlineData(Keys.F19)]
    [InlineData(Keys.F24)]
    public void RejectsFunctionKeysAboveTwelveWithAUsefulReason(Keys key)
    {
        Assert.False(WindowsKeyTranslator.TryTranslate(key, out _, out string? error));
        Assert.Contains("Hotkey column", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Keys.ControlKey)]
    [InlineData(Keys.ShiftKey)]
    [InlineData(Keys.Menu)]
    [InlineData(Keys.LWin)]
    [InlineData(Keys.None)]
    [InlineData(Keys.Control)]
    [InlineData(Keys.Control | Keys.Shift)]
    public void TreatsModifierOnlyPressesAsIncomplete(Keys key)
    {
        Assert.True(WindowsKeyTranslator.IsModifierOnly(key));
        Assert.False(WindowsKeyTranslator.TryTranslate(key, out _, out _));
    }

    /// <summary>
    /// The invariant that makes capture safe to offer: it can never produce something the
    /// configuration loader would then reject. A convenience that emits invalid input is worse
    /// than no convenience.
    /// </summary>
    [Fact]
    public void AnythingItProducesIsAcceptedByTheConfigurationValidator()
    {
        Keys[] modifierSets = [Keys.None, Keys.Control, Keys.Alt, Keys.Shift, Keys.Control | Keys.Shift, Keys.Control | Keys.Alt | Keys.Shift];
        int produced = 0;

        foreach (Keys key in Enum.GetValues<Keys>())
        {
            foreach (Keys modifiers in modifierSets)
            {
                if (!WindowsKeyTranslator.TryTranslate(key | modifiers, out string? expression, out _))
                {
                    continue;
                }

                produced++;

                Assert.True(
                    PlaywrightKeys.TryNormalize(expression, out string? normalized, out string? error),
                    $"Capture produced '{expression}' which the validator rejects: {error}");

                // And it is already normalised, so capture and validation agree on spelling.
                Assert.Equal(expression, normalized);
            }
        }

        // Guards against the loop silently translating nothing and the assertions never running.
        Assert.True(produced > 200, $"Only {produced} combinations translated; the table has probably regressed.");
    }

    [Theory]
    [InlineData("Shift+Digit1", "!")]
    [InlineData("Shift+Digit5", "%")]
    [InlineData("Shift+Digit7", "&")]
    public void DescribesWhatAShiftedDigitActuallyDelivers(string expression, string expectedCharacter) =>
        Assert.Contains(expectedCharacter, WindowsKeyTranslator.Describe(expression), StringComparison.Ordinal);

    [Fact]
    public void DescribesAModifiedLetter() =>
        Assert.Contains("Control", WindowsKeyTranslator.Describe("Control+KeyQ"), StringComparison.Ordinal);
}
