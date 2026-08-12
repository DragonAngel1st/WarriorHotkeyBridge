using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using WarriorHotkeyBridge.Configuration;
using WarriorHotkeyBridge.Diagnostics;
using WarriorHotkeyBridge.Models;

namespace WarriorHotkeyBridge.Chrome;

/// <inheritdoc cref="IChromeConnectionManager"/>
internal sealed class ChromeConnectionManager : IChromeConnectionManager
{
    private readonly ChromeOptions _options;
    private readonly ILogger<ChromeConnectionManager> _logger;

    /// <summary>Serialises connect/disconnect so concurrent callers cannot race two connections.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private ChromeState _state = ChromeState.Disconnected;
    private bool _disposed;

    public ChromeConnectionManager(IOptions<ChromeOptions> options, ILogger<ChromeConnectionManager> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public ChromeState State => _state;

    public IBrowser? Browser => _browser;

    public event EventHandler<ChromeStateChangedEventArgs>? StateChanged;

    public async Task<bool> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Fast path: no lock, no allocation. This runs on the command path.
        if (_state is ChromeState.Connected && _browser is { IsConnected: true })
        {
            return true;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Re-check: another caller may have connected while we waited for the gate.
            if (_state is ChromeState.Connected && _browser is { IsConnected: true })
            {
                return true;
            }

            await DiscardBrowserAsync().ConfigureAwait(false);

            SetState(ChromeState.Connecting, $"connecting to {_options.CdpEndpoint}");

            _playwright ??= await Playwright.CreateAsync().ConfigureAwait(false);

            _logger.ChromeConnecting(_options.CdpEndpoint);

            IBrowser browser = await _playwright.Chromium.ConnectOverCDPAsync(
                _options.CdpEndpoint,
                new BrowserTypeConnectOverCDPOptions { Timeout = (float)_options.ConnectTimeout.TotalMilliseconds })
                .ConfigureAwait(false);

            // Chrome closing, crashing or being restarted all surface here. The handler only
            // records the fact; reconnection is driven by the watchdog so that a disconnect
            // during shutdown cannot resurrect the connection.
            browser.Disconnected += OnBrowserDisconnected;

            _browser = browser;
            SetState(ChromeState.Connected, null);

            _logger.ChromeConnected(browser.Version, browser.Contexts.Count);
            return true;
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException or IOException or HttpRequestException)
        {
            // Chrome simply not being up is the single most common state for this application,
            // so it is reported as a plain warning rather than an error with a stack trace.
            _logger.ChromeConnectFailed(_options.CdpEndpoint, ex.Message);
            await DiscardBrowserAsync().ConfigureAwait(false);
            SetState(ChromeState.Disconnected, ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            // Anything else is genuinely unexpected (a missing Playwright driver, for example)
            // and must be loud rather than looking like a routine "Chrome is closed".
            _logger.ChromeConnectFaulted(ex);
            await DiscardBrowserAsync().ConfigureAwait(false);
            SetState(ChromeState.Faulted, ex.Message);
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void OnBrowserDisconnected(object? sender, IBrowser e)
    {
        _logger.ChromeDisconnected();
        SetState(ChromeState.Disconnected, "Chrome closed the DevTools connection.");
    }

    public async Task CloseBrowserAsync()
    {
        IBrowser? browser = _browser;

        if (browser is not null && browser.IsConnected)
        {
            try
            {
                // Browser.close over CDP rather than Playwright's CloseAsync: for a
                // ConnectOverCDP connection CloseAsync only detaches - which is precisely why
                // the rest of this class can use it safely - so it would leave Chrome running.
                ICDPSession session = await browser.NewBrowserCDPSessionAsync().ConfigureAwait(false);
                await session.SendAsync("Browser.close").ConfigureAwait(false);

                _logger.ChromeClosedByRequest();
            }
            catch (Exception ex) when (ex is PlaywrightException or TimeoutException or ObjectDisposedException)
            {
                // Chrome may already be going away, which is the outcome we wanted anyway.
                _logger.ChromeDiscardFailed(ex.Message);
            }
        }

        await DisconnectAsync().ConfigureAwait(false);
    }

    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);

        try
        {
            await DiscardBrowserAsync().ConfigureAwait(false);
            SetState(ChromeState.Disconnected, "disconnected by request");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Releases our side of the connection. Never closes Chrome: we attached to the operator's
    /// browser and closing it would take their trading platform down with us.
    /// </summary>
    private async Task DiscardBrowserAsync()
    {
        if (_browser is null)
        {
            return;
        }

        IBrowser browser = _browser;
        _browser = null;
        browser.Disconnected -= OnBrowserDisconnected;

        try
        {
            await browser.CloseAsync().ConfigureAwait(false);
        }
        catch (PlaywrightException ex)
        {
            // Expected when Chrome has already gone away; there is nothing left to release.
            _logger.ChromeDiscardFailed(ex.Message);
        }
    }

    private void SetState(ChromeState next, string? reason)
    {
        ChromeState previous = _state;

        if (previous == next)
        {
            return;
        }

        _state = next;
        StateChanged?.Invoke(this, new ChromeStateChangedEventArgs(previous, next, reason));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await DiscardBrowserAsync().ConfigureAwait(false);

        _playwright?.Dispose();
        _playwright = null;
        _gate.Dispose();
    }
}
