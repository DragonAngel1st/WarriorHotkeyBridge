using WarriorHotkeyBridge.Configuration;
using WarriorHotkeyBridge.Warrior;

namespace WarriorHotkeyBridge.Tests;

/// <summary>
/// Which hosts may receive a trading chord, and why there is more than one of them.
/// </summary>
/// <remarks>
/// <para>
/// On 2026-08-21 Warrior moved the SIM from <c>sim.warriortrading.com</c> to
/// <c>sim2.warriortrading.com</c> with no notice. The bridge connected to Chrome exactly as it
/// should, found no page it was permitted to touch, and every key stopped working - the dashboard
/// was open the whole time, on a host one character different.
/// </para>
/// <para>
/// The response is a list of exact hosts, not a looser test. A suffix or wildcard match on
/// <c>warriortrading.com</c> would have survived the move, and would also have let a chord reach
/// the chatroom, a marketing page, or anything else served from that domain. Trading one morning
/// of downtime for a permanent hole is not a trade worth making.
/// </para>
/// </remarks>
public class AllowedHostTests
{
    private static readonly WarriorSimOptions Shipped = new();

    [Theory]
    [InlineData("https://sim.warriortrading.com/web/dashboard?hash=b78c8a9")]
    [InlineData("https://sim2.warriortrading.com/web/dashboard?hash=f138377")]
    public void BothSimHostsAreAccepted(string url)
        => Assert.True(WarriorTargetValidator.IsAllowedHost(url, Shipped.EffectiveAllowedHosts));

    /// <summary>
    /// The old host stays accepted. Not every account is moved at the same time, and an operator
    /// who has not been moved - or gets moved back - must not be broken by the fix for the ones
    /// who have.
    /// </summary>
    [Fact]
    public void TheOriginalHostIsNotDropped()
        => Assert.Contains("sim.warriortrading.com", WarriorSimOptions.DefaultAllowedHosts);

    /// <summary>
    /// The chatroom is open on the operator's screen every day and must never be a target.
    /// </summary>
    [Theory]
    [InlineData("https://chatroom.warriortrading.com/dashboard?hash=abd911b0d")]
    [InlineData("https://www.warriortrading.com/login-member/")]
    [InlineData("https://sim3.warriortrading.com/web/dashboard")]
    public void OtherWarriorHostsAreStillRefused(string url)
        => Assert.False(WarriorTargetValidator.IsAllowedHost(url, Shipped.EffectiveAllowedHosts));

    /// <summary>
    /// A list widens which hosts are allowed; it must not weaken how each one is compared.
    /// </summary>
    [Theory]
    [InlineData("https://sim2.warriortrading.com.evil.test/web/dashboard")]
    [InlineData("https://sim2.warriortrading.com@evil.test/")]
    [InlineData("https://evil.test/?x=https://sim2.warriortrading.com/")]
    public void LookAlikesAreStillRefusedAgainstAList(string url)
        => Assert.False(WarriorTargetValidator.IsAllowedHost(url, Shipped.EffectiveAllowedHosts));

    [Fact]
    public void AnEmptyListMatchesNothing()
        => Assert.False(WarriorTargetValidator.IsAllowedHost("https://sim2.warriortrading.com/", []));

    /// <summary>
    /// The emergency lever: one line in the user's own file, addable over the phone, that gets
    /// them trading the next time Warrior moves the SIM.
    /// </summary>
    [Fact]
    public void AnOperatorCanAddAHostWithoutARelease()
    {
        var options = new WarriorSimOptions { AllowedHost = "sim3.warriortrading.com" };

        Assert.True(WarriorTargetValidator.IsAllowedHost(
            "https://sim3.warriortrading.com/web/dashboard", options.EffectiveAllowedHosts));
    }

    /// <summary>
    /// That lever adds; it never replaces. This is the property that stops it becoming the next
    /// bug - a line left in the user's file after the fix ships must not quietly narrow the bridge
    /// to a host Warrior has since abandoned, which is exactly how the F23/F24 bindings went wrong.
    /// </summary>
    [Fact]
    public void AddingAHostDoesNotRemoveTheDefaults()
    {
        var options = new WarriorSimOptions { AllowedHost = "sim3.warriortrading.com" };

        Assert.True(WarriorTargetValidator.IsAllowedHost(
            "https://sim2.warriortrading.com/web/dashboard", options.EffectiveAllowedHosts));
        Assert.True(WarriorTargetValidator.IsAllowedHost(
            "https://sim.warriortrading.com/web/dashboard", options.EffectiveAllowedHosts));
    }

    [Fact]
    public void ARepeatedHostIsNotListedTwice()
    {
        var options = new WarriorSimOptions { AllowedHost = "SIM2.warriortrading.com" };

        Assert.Equal(
            WarriorSimOptions.DefaultAllowedHosts.Length,
            options.EffectiveAllowedHosts.Count);
    }

    /// <summary>
    /// A mangled configuration must degrade to the built-in hosts, not to nothing.
    /// </summary>
    /// <remarks>
    /// Refusing everything would be safe but unreadable: it presents as "the bridge stopped
    /// working" with no clue why, which is the failure this whole file exists to prevent a
    /// repeat of.
    /// </remarks>
    [Fact]
    public void AnEmptyConfiguredListFallsBackToTheDefaults()
    {
        var options = new WarriorSimOptions { AllowedHosts = ["", "   "] };

        Assert.Equal(WarriorSimOptions.DefaultAllowedHosts, options.EffectiveAllowedHosts);
    }

    /// <summary>
    /// Configured hosts replace the defaults, so an operator who deliberately narrows the list
    /// gets what they asked for.
    /// </summary>
    [Fact]
    public void AConfiguredListReplacesTheDefaults()
    {
        var options = new WarriorSimOptions { AllowedHosts = ["sim2.warriortrading.com"] };

        Assert.False(WarriorTargetValidator.IsAllowedHost(
            "https://sim.warriortrading.com/web/dashboard", options.EffectiveAllowedHosts));
    }
}
