using WarriorHotkeyBridge.Hotkeys;

namespace WarriorHotkeyBridge.Tests;

/// <summary>
/// Covers the Hotkey column accepting the browser key names the Sends column uses.
/// </summary>
/// <remarks>
/// The two columns sit side by side and use different vocabularies - Windows key names on the
/// left, KeyboardEvent.code on the right - and F13 is spelled identically in both, which suggests
/// they are the same. Typing KeyD in the left column is therefore a mistake the interface invites,
/// and every one of these names identifies exactly one key, so accepting both resolves a spelling
/// rather than guessing an intent.
/// </remarks>
public class HotkeyGestureAliasTests
{
    /// <summary>The exact value that was rejected in use.</summary>
    [Fact]
    public void AcceptsTheBrowserSpellingThatWasPreviouslyRejected()
    {
        Assert.True(HotkeyGesture.TryParse("Control+Alt+KeyD", out HotkeyGesture gesture, out string? error), error);

        Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Alt, gesture.Modifiers);
        Assert.Equal(Keys.D, gesture.Key);
    }

    /// <summary>
    /// Both spellings must land on the same gesture, so a row typed one way and a row typed the
    /// other are recognised as the duplicate they are rather than both being registered.
    /// </summary>
    [Theory]
    [InlineData("KeyD", "D")]
    [InlineData("Digit1", "1")]
    [InlineData("Digit0", "D0")]
    [InlineData("ArrowUp", "Up")]
    [InlineData("ArrowLeft", "Left")]
    [InlineData("Numpad5", "NumPad5")]
    [InlineData("Backspace", "Back")]
    [InlineData("NumpadAdd", "Add")]
    [InlineData("Semicolon", "OemSemicolon")]
    [InlineData("Slash", "OemQuestion")]
    [InlineData("Ctrl+Alt+KeyQ", "Ctrl+Alt+Q")]
    [InlineData("Shift+Digit7", "Shift+7")]
    public void BrowserAndWindowsSpellingsResolveToTheSameGesture(string browser, string windows)
    {
        Assert.True(HotkeyGesture.TryParse(browser, out HotkeyGesture fromBrowser, out string? browserError), browserError);
        Assert.True(HotkeyGesture.TryParse(windows, out HotkeyGesture fromWindows, out string? windowsError), windowsError);

        Assert.Equal(fromWindows, fromBrowser);
    }

    /// <summary>
    /// Spelled the same in both vocabularies, which is exactly why the difference is invisible
    /// until something is rejected.
    /// </summary>
    [Theory]
    [InlineData("F13")]
    [InlineData("F24")]
    [InlineData("Escape")]
    [InlineData("Enter")]
    [InlineData("Tab")]
    [InlineData("Space")]
    [InlineData("Home")]
    [InlineData("PageUp")]
    public void NamesCommonToBothVocabulariesStillParse(string name) =>
        Assert.True(HotkeyGesture.TryParse(name, out _, out string? error), error);

    /// <summary>
    /// The alias is tried second, so nothing that already parsed can change meaning. Keys.Add is
    /// the numpad plus in the Windows vocabulary and must stay that way.
    /// </summary>
    [Fact]
    public void WindowsNamesKeepTheirExistingMeaning()
    {
        Assert.True(HotkeyGesture.TryParse("Add", out HotkeyGesture gesture, out _));
        Assert.Equal(Keys.Add, gesture.Key);

        Assert.True(HotkeyGesture.TryParse("D", out HotkeyGesture d, out _));
        Assert.Equal(Keys.D, d.Key);

        Assert.True(HotkeyGesture.TryParse("D1", out HotkeyGesture d1, out _));
        Assert.Equal(Keys.D1, d1.Key);
    }

    [Theory]
    [InlineData("KeyDD")]
    [InlineData("Digit99")]
    [InlineData("Numpad")]
    [InlineData("NotAKeyAtAll")]
    [InlineData("Ctrl+Alt+Nonsense")]
    public void StillRejectsThingsThatNameNoKey(string text) =>
        Assert.False(HotkeyGesture.TryParse(text, out _, out _));

    /// <summary>
    /// The message has to name both vocabularies, because being told a name is unrecognised
    /// without being told which vocabulary is expected leaves nowhere to go.
    /// </summary>
    [Fact]
    public void TheErrorNamesBothVocabularies()
    {
        Assert.False(HotkeyGesture.TryParse("Ctrl+Nonsense", out _, out string? error));

        Assert.Contains("Windows key name", error, StringComparison.Ordinal);
        Assert.Contains("KeyD", error, StringComparison.Ordinal);
    }
}
