using Microsoft.Extensions.Configuration;
using WarriorHotkeyBridge.Configuration;
using WarriorHotkeyBridge.Startup;

namespace WarriorHotkeyBridge.Tests;

/// <summary>
/// Pins the one property the first-run template must have: it changes nothing.
/// </summary>
/// <remarks>
/// The template exists so a new operator has something to read and copy from. It is written
/// automatically, without being asked for, into an application that can place trades - so it has
/// to be provably inert. A future edit that made one of its examples live would ship a working
/// buy binding to everyone who installs the product.
/// </remarks>
public class StarterConfigurationTests
{
    [Fact]
    public void TheTemplateIsValidJson() =>
        Assert.NotNull(Load());

    /// <summary>
    /// Every name inside Bindings is read as a hotkey, so an example left in there would not be
    /// ignored - it would be reported at startup as an unparseable key, on a fresh install, to
    /// someone who has not configured anything yet.
    /// </summary>
    [Fact]
    public void TheTemplateDefinesNoBindings()
    {
        IConfigurationRoot configuration = Load();

        IConfigurationSection bindings = configuration.GetSection("Hotkeys:Bindings");

        Assert.Empty(bindings.GetChildren());
    }

    /// <summary>
    /// The documentation keys are prefixed so the binder cannot mistake them for settings. If one
    /// were ever named as a real section, the template would start changing behaviour.
    /// </summary>
    [Fact]
    public void EveryTopLevelKeyIsEitherDocumentationOrAKnownSection()
    {
        IConfigurationRoot configuration = Load();

        string[] realSections =
        [
            ChromeOptions.SectionName,
            WarriorSimOptions.SectionName,
            HotkeyOptions.SectionName,
            CommandOptions.SectionName,
            LogOptions.SectionName,
        ];

        foreach (IConfigurationSection child in configuration.GetChildren())
        {
            bool isDocumentation = child.Key.StartsWith("//", StringComparison.Ordinal);
            bool isKnownSection = realSections.Contains(child.Key, StringComparer.Ordinal);

            Assert.True(
                isDocumentation || isKnownSection,
                $"'{child.Key}' is neither a documentation key nor a known section, so the template "
                + "may be changing behaviour it does not intend to.");
        }
    }

    /// <summary>
    /// The one-button-start block is offered but must arrive switched off, so installing the
    /// product never causes it to launch a browser on its own.
    /// </summary>
    [Fact]
    public void ChromeAutoLaunchIsNotEnabledByTheTemplate()
    {
        IConfigurationRoot configuration = Load();

        Assert.False(configuration.GetSection("Chrome:AutoLaunch").Exists());
    }

    [Fact]
    public void TryWrite_CreatesTheFileOnceAndNeverOverwritesIt()
    {
        string root = Path.Combine(Path.GetTempPath(), "whb-starter-" + Guid.NewGuid().ToString("N"));

        try
        {
            AppPaths paths = AppPathsFor(root);

            Assert.True(StarterConfiguration.TryWrite(paths));
            Assert.True(File.Exists(paths.UserConfigFile));

            // Stand in for an operator who has since authored real bindings. A second run must
            // leave that alone: the file belongs to them from the moment it exists.
            File.WriteAllText(paths.UserConfigFile, "{ \"mine\": true }");

            Assert.False(StarterConfiguration.TryWrite(paths));
            Assert.Equal("{ \"mine\": true }", File.ReadAllText(paths.UserConfigFile));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static IConfigurationRoot Load()
    {
        string root = Path.Combine(Path.GetTempPath(), "whb-starter-" + Guid.NewGuid().ToString("N"));

        try
        {
            AppPaths paths = AppPathsFor(root);
            StarterConfiguration.TryWrite(paths);

            return new ConfigurationBuilder()
                .AddJsonFile(paths.UserConfigFile, optional: false)
                .Build();
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// Builds an <see cref="AppPaths"/> rooted in a temporary directory rather than the real
    /// %LOCALAPPDATA%, so a test run cannot touch the machine's own configuration.
    /// </summary>
    /// <remarks>
    /// Uses the explicit-root overload rather than redirecting the LOCALAPPDATA environment
    /// variable. Environment.GetFolderPath asks the Windows shell and ignores that variable, so
    /// the redirection approach looks isolated while actually operating on the live profile - a
    /// test suite that wrote a starter configuration over an operator's real trading bindings.
    /// </remarks>
    private static AppPaths AppPathsFor(string root) => AppPaths.CreateAndEnsure(root);
}
