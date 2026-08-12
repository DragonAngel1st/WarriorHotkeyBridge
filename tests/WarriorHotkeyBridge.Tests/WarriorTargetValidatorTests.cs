using WarriorHotkeyBridge.Warrior;

namespace WarriorHotkeyBridge.Tests;

/// <summary>
/// These rules decide whether a page may receive trading input, so the negative cases matter
/// more than the positive ones.
/// </summary>
public class WarriorTargetValidatorTests
{
    private const string AllowedHost = "sim.warriortrading.com";

    [Theory]
    [InlineData("https://sim.warriortrading.com/")]
    [InlineData("https://sim.warriortrading.com/platform/trade")]
    [InlineData("https://SIM.WarriorTrading.COM/")]
    [InlineData("http://sim.warriortrading.com/")]
    [InlineData("https://sim.warriortrading.com:443/x?session=abc#frag")]
    public void IsAllowedHost_AcceptsTheExactHost(string url) =>
        Assert.True(WarriorTargetValidator.IsAllowedHost(url, AllowedHost));

    /// <summary>
    /// Every entry here contains the allowed host as a substring, so a
    /// <c>Contains("sim.warriortrading.com")</c> check would accept all of them.
    /// </summary>
    [Theory]
    [InlineData("https://sim.warriortrading.com.evil.test/")]          // suffix attack
    [InlineData("https://evil.test/sim.warriortrading.com")]           // host in the path
    [InlineData("https://sim.warriortrading.com@evil.test/")]          // userinfo trick
    [InlineData("https://notsim.warriortrading.com/")]                 // prefix attack
    [InlineData("https://evil.test/?next=sim.warriortrading.com")]     // host in the query
    public void IsAllowedHost_RejectsLookAlikesThatContainTheHostAsASubstring(string url) =>
        Assert.False(WarriorTargetValidator.IsAllowedHost(url, AllowedHost));

    /// <summary>The operator really does have this open; it must never be a target.</summary>
    [Fact]
    public void IsAllowedHost_RejectsTheWarriorChatroomSubdomain() =>
        Assert.False(WarriorTargetValidator.IsAllowedHost("https://chatroom.warriortrading.com/", AllowedHost));

    [Theory]
    [InlineData("about:blank")]
    [InlineData("chrome://newtab/")]
    [InlineData("devtools://devtools/bundled/inspector.html")]
    [InlineData("file:///C:/sim.warriortrading.com.html")]
    [InlineData("chrome-extension://abcdef/popup.html")]
    public void IsAllowedHost_RejectsNonWebSchemes(string url) =>
        Assert.False(WarriorTargetValidator.IsAllowedHost(url, AllowedHost));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("/relative/path")]
    public void IsAllowedHost_FailsClosedOnUnusableInput(string? url) =>
        Assert.False(WarriorTargetValidator.IsAllowedHost(url, AllowedHost));

    [Fact]
    public void IsAllowedHost_FailsClosedWhenNoHostIsConfigured() =>
        Assert.False(WarriorTargetValidator.IsAllowedHost("https://sim.warriortrading.com/", ""));

    [Theory]
    [InlineData("Sim Trading Platform")]
    [InlineData("Warrior Trading - Sim Trading Platform")]
    [InlineData("sim trading platform")]
    public void TitleMatches_AcceptsTitlesContainingTheMarker(string title) =>
        Assert.True(WarriorTargetValidator.TitleMatches(title, "Sim Trading Platform"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("WarriorTrading Chatroom")]
    [InlineData("Sim Trading")]
    public void TitleMatches_RejectsAnythingElse(string? title) =>
        Assert.False(WarriorTargetValidator.TitleMatches(title, "Sim Trading Platform"));
}
