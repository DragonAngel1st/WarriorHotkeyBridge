using System.ComponentModel.DataAnnotations;
using WarriorHotkeyBridge.Configuration;

namespace WarriorHotkeyBridge.Tests;

/// <summary>
/// Pins the one property that matters about <c>Chrome:CdpEndpoint</c>: anything validation
/// accepts must also parse.
/// </summary>
/// <remarks>
/// The launcher derives Chrome's <c>--remote-debugging-port</c> from the same string the bridge
/// connects to. If validation ever accepts something the parser rejects, the two diverge and the
/// bridge launches Chrome on one port while connecting to another - which presents to the
/// operator as "Chrome started but never connects", with nothing in the log pointing at the
/// configuration value that caused it.
/// </remarks>
public class ChromeOptionsTests
{
    [Theory]
    [InlineData("http://127.0.0.1:9222", 9222)]
    [InlineData("http://localhost:9222", 9222)]
    [InlineData("http://[::1]:9222", 9222)]
    [InlineData("https://127.0.0.1:9333", 9333)]
    [InlineData("http://127.0.0.1:9222/", 9222)]
    // Scheme case is not significant in a URL, and Uri normalises it. A validator that rejected
    // this would be refusing a legal spelling.
    [InlineData("HTTP://127.0.0.1:9222", 9222)]
    // No explicit port is unusual for CDP but unambiguous, and the default is what Chrome and
    // the bridge would both use.
    [InlineData("http://127.0.0.1", 80)]
    public void AcceptsUsableEndpointsAndAgreesOnThePort(string endpoint, int expectedPort)
    {
        var options = new ChromeOptions { CdpEndpoint = endpoint };

        Assert.Empty(Validate(options));
        Assert.Equal(expectedPort, options.CdpPort);
    }

    /// <summary>
    /// Every case here was accepted by an earlier pattern-based validator whose host term
    /// silently consumed the <c>:port</c> substring, leaving its port-range check matching
    /// nothing.
    /// </summary>
    [Theory]
    [InlineData("http://127.0.0.1:999999")]  // port beyond 65535
    [InlineData("http://127.0.0.1:-5")]
    [InlineData("http://:9222")]             // no host
    [InlineData("http://a:b:c")]
    [InlineData("127.0.0.1:9222")]           // not absolute
    [InlineData("ftp://127.0.0.1:9222")]     // cannot carry DevTools
    [InlineData("file:///C:/chrome")]
    [InlineData("not a url")]
    public void RejectsEndpointsThatWouldNotParse(string endpoint)
    {
        var options = new ChromeOptions { CdpEndpoint = endpoint };

        Assert.NotEmpty(Validate(options));
    }

    /// <summary>
    /// The invariant, stated directly rather than case by case: validation must never accept an
    /// endpoint the launcher cannot derive a port from.
    /// </summary>
    [Theory]
    [InlineData("http://127.0.0.1:9222")]
    [InlineData("http://127.0.0.1:999999")]
    [InlineData("http://a:b:c")]
    [InlineData("http://:9222")]
    [InlineData("ftp://127.0.0.1:9222")]
    [InlineData("")]
    [InlineData("   ")]
    public void AcceptedImpliesParseable(string endpoint)
    {
        var options = new ChromeOptions { CdpEndpoint = endpoint };

        if (Validate(options).Count == 0)
        {
            Assert.True(options.CdpPort is > 0 and <= 65535,
                $"'{endpoint}' passed validation but yielded port {options.CdpPort}.");
        }
    }

    [Fact]
    public void TheShippedDefaultIsValid()
    {
        var options = new ChromeOptions();

        Assert.Empty(Validate(options));
        Assert.Equal(9222, options.CdpPort);
    }

    /// <summary>
    /// Mirrors what ValidateDataAnnotations does at startup, including IValidatableObject.
    /// </summary>
    private static List<ValidationResult> Validate(ChromeOptions options)
    {
        List<ValidationResult> results = [];
        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);
        return results;
    }
}
