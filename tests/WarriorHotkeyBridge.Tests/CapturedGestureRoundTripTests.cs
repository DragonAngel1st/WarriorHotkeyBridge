using WarriorHotkeyBridge.Hotkeys;

namespace WarriorHotkeyBridge.Tests;

/// <summary>
/// Covers a chord reaching the Sends column identically whether or not it is a registered hotkey.
/// </summary>
/// <remarks>
/// Windows delivers a registered hotkey to the registering window and never to the focused one,
/// so a chord already bound as a hotkey arrives at the capture dialog by a completely different
/// route from an unbound one. The two routes must produce the same text, because the case that
/// exposes any difference is the natural one: wanting the deck key and the SIM shortcut to be the
/// same chord.
/// </remarks>
public class CapturedGestureRoundTripTests
{
    /// <summary>The exact chord that could not be captured: bound as a hotkey, then typed into Sends.</summary>
    [Fact]
    public void AChordBoundAsAHotkeyStillTranslatesForTheSendsColumn()
    {
        Assert.True(HotkeyGesture.TryParse("Ctrl+Shift+D", out HotkeyGesture gesture, out _));

        Assert.True(WindowsKeyTranslator.TryTranslate(gesture.ToWindowsKeyData(), out string? sends, out string? error), error);
        Assert.Equal("Control+Shift+KeyD", sends);
    }

    /// <summary>
    /// The invariant. Whichever route the press took, the recorded value is the same - otherwise a
    /// binding would mean different things depending on whether it had been configured yet.
    /// </summary>
    [Theory]
    [InlineData("Ctrl+Shift+D", Keys.D | Keys.Control | Keys.Shift)]
    [InlineData("Ctrl+Q", Keys.Q | Keys.Control)]
    [InlineData("Shift+1", Keys.D1 | Keys.Shift)]
    [InlineData("Alt+F4", Keys.F4 | Keys.Alt)]
    [InlineData("Ctrl+Alt+Shift+A", Keys.A | Keys.Control | Keys.Alt | Keys.Shift)]
    [InlineData("Up", Keys.Up)]
    [InlineData("NumPad5", Keys.NumPad5)]
    public void BothDeliveryRoutesProduceTheSameText(string gestureText, Keys directKeyData)
    {
        Assert.True(HotkeyGesture.TryParse(gestureText, out HotkeyGesture gesture, out _));

        // Route one: intercepted as a hotkey, forwarded by the bridge.
        Assert.True(WindowsKeyTranslator.TryTranslate(gesture.ToWindowsKeyData(), out string? viaHotkey, out _));

        // Route two: never intercepted, read as ordinary keyboard input by the dialog.
        Assert.True(WindowsKeyTranslator.TryTranslate(directKeyData, out string? viaKeyboard, out _));

        Assert.Equal(viaKeyboard, viaHotkey);
    }

    /// <summary>
    /// The Windows key has no Keys modifier flag, so a gesture carrying it has to pass that fact
    /// separately or it would silently be dropped from the recorded chord.
    /// </summary>
    [Fact]
    public void TheWindowsKeyModifierSurvivesAsMeta()
    {
        Assert.True(HotkeyGesture.TryParse("Win+Shift+D", out HotkeyGesture gesture, out _));
        Assert.True(gesture.Modifiers.HasFlag(HotkeyModifiers.Windows));

        Assert.True(WindowsKeyTranslator.TryTranslate(
            gesture.ToWindowsKeyData(),
            includeMeta: true,
            out string? sends,
            out string? error), error);

        Assert.Equal("Meta+Shift+KeyD", sends);
        Assert.True(PlaywrightKeys.TryNormalize(sends, out _, out _));
    }

    /// <summary>
    /// F13-F24 make good hotkeys and cannot be sent into a page, so a deck key forwarded into the
    /// Sends dialog must be refused rather than silently recorded as something unusable.
    /// </summary>
    [Fact]
    public void ADeckKeyForwardedIntoTheSendsColumnIsStillRefused()
    {
        Assert.True(HotkeyGesture.TryParse("F13", out HotkeyGesture gesture, out _));

        Assert.False(WindowsKeyTranslator.TryTranslate(gesture.ToWindowsKeyData(), out _, out string? error));
        Assert.Contains("Hotkey column", error, StringComparison.Ordinal);
    }
}
