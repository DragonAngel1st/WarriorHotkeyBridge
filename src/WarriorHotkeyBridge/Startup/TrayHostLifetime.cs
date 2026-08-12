using Microsoft.Extensions.Hosting;

namespace WarriorHotkeyBridge.Startup;

/// <summary>
/// A no-op <see cref="IHostLifetime"/> for a tray application.
/// </summary>
/// <remarks>
/// The default <c>ConsoleLifetime</c> installs Ctrl+C handlers and blocks shutdown on console
/// signals, which is wrong here: this process's lifetime is owned by the WinForms message
/// loop, and in normal mode there is no console at all. Shutdown is driven by the tray's Exit
/// item calling <see cref="IHostApplicationLifetime.StopApplication"/>.
/// </remarks>
internal sealed class TrayHostLifetime : IHostLifetime
{
    public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
