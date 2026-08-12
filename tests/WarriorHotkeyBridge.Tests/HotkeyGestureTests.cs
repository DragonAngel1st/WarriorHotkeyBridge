using WarriorHotkeyBridge.Hotkeys;

namespace WarriorHotkeyBridge.Tests;

public class HotkeyGestureTests
{
    /// <summary>
    /// Asserted through the normalised <see cref="HotkeyGesture.Display"/> form, which pins
    /// down parsing and canonical rendering in one pass — the modifier enum is internal and so
    /// cannot appear in a public theory signature.
    /// </summary>
    [Theory]
    [InlineData("F13", "F13")]
    [InlineData("f13", "F13")]
    [InlineData("F24", "F24")]
    [InlineData("Ctrl+F13", "Ctrl+F13")]
    [InlineData("Control+F13", "Ctrl+F13")]
    [InlineData("Alt+F13", "Alt+F13")]
    [InlineData("Shift+F13", "Shift+F13")]
    [InlineData("Win+F13", "Win+F13")]
    [InlineData("windows+F13", "Win+F13")]
    [InlineData("Ctrl+Alt+F13", "Ctrl+Alt+F13")]
    [InlineData("alt+ctrl+F13", "Ctrl+Alt+F13")]
    [InlineData("  ctrl + shift + f14  ", "Ctrl+Shift+F14")]
    [InlineData("1", "D1")]
    [InlineData("Ctrl+0", "Ctrl+D0")]
    public void TryParse_AcceptsValidGestures(string text, string expectedDisplay)
    {
        bool parsed = HotkeyGesture.TryParse(text, out HotkeyGesture gesture, out string? error);

        Assert.True(parsed, error);
        Assert.Null(error);
        Assert.Equal(expectedDisplay, gesture.Display);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("Ctrl")]              // modifier alone is not a gesture
    [InlineData("Ctrl+Alt")]          // no key at the end
    [InlineData("Shift")]
    [InlineData("NotAKey")]
    [InlineData("Hyper+F13")]         // unknown modifier
    [InlineData("124")]               // numeric value that would otherwise resolve to F13
    [InlineData("ControlKey")]        // the modifier key itself cannot be a hotkey
    [InlineData("+")]
    public void TryParse_RejectsInvalidGestures(string? text)
    {
        bool parsed = HotkeyGesture.TryParse(text, out _, out string? error);

        Assert.False(parsed);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    /// <summary>
    /// F13's virtual-key code is 0x7C. If this ever changes, every registration silently binds
    /// the wrong physical key, so it is asserted explicitly rather than trusted.
    /// </summary>
    [Fact]
    public void VirtualKeyCode_MatchesWin32ForFunctionKeys()
    {
        Assert.True(HotkeyGesture.TryParse("F13", out HotkeyGesture f13, out _));
        Assert.Equal(0x7Cu, f13.VirtualKeyCode);

        Assert.True(HotkeyGesture.TryParse("F24", out HotkeyGesture f24, out _));
        Assert.Equal(0x87u, f24.VirtualKeyCode);
    }

    [Fact]
    public void VirtualKeyCode_ExcludesModifierBits()
    {
        var gesture = new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Alt, Keys.F13);

        // Keys carries modifier flags in its high bits; RegisterHotKey wants the bare VK code.
        Assert.Equal(0x7Cu, gesture.VirtualKeyCode);
    }

    [Fact]
    public void ToWin32Modifiers_AlwaysSuppressesAutoRepeat()
    {
        var gesture = new HotkeyGesture(HotkeyModifiers.None, Keys.F13);

        // Without MOD_NOREPEAT, holding a Stream Deck button would enqueue a burst of orders.
        Assert.Equal(NativeMethods.ModNoRepeat, gesture.ToWin32Modifiers() & NativeMethods.ModNoRepeat);
    }

    [Fact]
    public void ToWin32Modifiers_MapsEachModifierToItsWin32Flag()
    {
        var gesture = new HotkeyGesture(
            HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift | HotkeyModifiers.Windows,
            Keys.F13);

        uint expected = NativeMethods.ModNoRepeat
            | NativeMethods.ModControl
            | NativeMethods.ModAlt
            | NativeMethods.ModShift
            | NativeMethods.ModWin;

        Assert.Equal(expected, gesture.ToWin32Modifiers());
    }

    [Fact]
    public void Display_OrdersModifiersConsistently()
    {
        Assert.Equal("F13", new HotkeyGesture(HotkeyModifiers.None, Keys.F13).Display);
        Assert.Equal("Ctrl+F13", new HotkeyGesture(HotkeyModifiers.Control, Keys.F13).Display);
        Assert.Equal("Win+F14", new HotkeyGesture(HotkeyModifiers.Windows, Keys.F14).Display);

        // Ctrl, Alt, Shift, Win regardless of the order the flags were combined in.
        Assert.Equal(
            "Ctrl+Alt+Shift+Win+F13",
            new HotkeyGesture(
                HotkeyModifiers.Windows | HotkeyModifiers.Shift | HotkeyModifiers.Alt | HotkeyModifiers.Control,
                Keys.F13).Display);
    }

    [Fact]
    public void Equality_IsValueBased()
    {
        // Duplicate detection during binding resolution depends on this.
        Assert.Equal(
            new HotkeyGesture(HotkeyModifiers.Control, Keys.F13),
            new HotkeyGesture(HotkeyModifiers.Control, Keys.F13));

        Assert.NotEqual(
            new HotkeyGesture(HotkeyModifiers.Control, Keys.F13),
            new HotkeyGesture(HotkeyModifiers.None, Keys.F13));
    }
}
