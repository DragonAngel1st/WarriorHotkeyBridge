using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using WarriorHotkeyBridge.Configuration;
using WarriorHotkeyBridge.Hotkeys;

namespace WarriorHotkeyBridge.Services;

/// <summary>
/// Periodically re-attempts hotkeys that another application was holding at startup.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately slow. A contested hotkey is freed by a human closing an application, so
/// checking every few seconds would burn cycles for years to catch an event that happens once
/// a day at most.
/// </para>
/// <para>
/// The retry itself has to run on the UI thread, because Win32 ties a hotkey to the thread
/// that owns the registering window - so this posts the work rather than doing it here.
/// </para>
/// </remarks>
internal sealed class HotkeyRetryWorker : BackgroundService
{
    private readonly GlobalHotkeyService _hotkeys;
    private readonly IUiDispatcher _ui;
    private readonly HotkeyOptions _options;
    private readonly ISystemResumeMonitor _resume;

    public HotkeyRetryWorker(
        GlobalHotkeyService hotkeys,
        IUiDispatcher ui,
        ISystemResumeMonitor resume,
        IOptions<HotkeyOptions> options)
    {
        _hotkeys = hotkeys;
        _ui = ui;
        _resume = resume;
        _options = options.Value;

        // Waking is a likely moment for a conflicting application to have gone away, and for
        // Windows to have released registrations, so do not wait out the interval.
        _resume.Resumed += OnResumed;
    }

    private void OnResumed(object? sender, EventArgs e) => Retry();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(_options.RetryInterval, stoppingToken).ConfigureAwait(false);
                Retry();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private void Retry() => _ui.Post(() => _hotkeys.RetryFailedRegistrations());

    public override void Dispose()
    {
        _resume.Resumed -= OnResumed;
        base.Dispose();
    }
}
