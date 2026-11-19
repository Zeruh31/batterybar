namespace BatteryBar.Core;

/// <summary>Polls all providers on a background task at a fixed interval (or on demand).</summary>
public sealed class BatteryMonitor : IDisposable
{
    private readonly IReadOnlyList<IBatteryProvider> _providers;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _wake = new(0);
    private Task? _loop;
    private volatile bool _disposed;

    public BatteryMonitor(IReadOnlyList<IBatteryProvider> providers, TimeSpan interval)
    {
        _providers = providers;
        _interval = interval;
    }

    /// <summary>Raised on a background thread after each poll.</summary>
    public event Action<IReadOnlyList<DeviceBattery>>? Updated;

    public void Start() => _loop ??= Task.Run(LoopAsync);

    /// <summary>Starts a poll right away instead of waiting for the next interval.</summary>
    public void RefreshNow()
    {
        try
        {
            if (!_disposed)
            {
                _wake.Release();
            }
        }
        catch (ObjectDisposedException)
        {
            // Shutting down.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        bool stopped = true;
        try
        {
            stopped = _loop?.Wait(TimeSpan.FromSeconds(5)) ?? true;
        }
        catch (AggregateException)
        {
            // The loop ended with an exception; it is no longer running.
        }

        if (!stopped)
        {
            // A provider is still inside a device call. Leave its resources alone; the process is exiting anyway.
            Log.Error("poll loop did not stop within 5 s; skipping cleanup");
            return;
        }

        foreach (IBatteryProvider provider in _providers)
        {
            provider.Dispose();
        }

        _cts.Dispose();
        _wake.Dispose();
    }

    private async Task LoopAsync()
    {
        CancellationToken token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            var devices = new List<DeviceBattery>();
            foreach (IBatteryProvider provider in _providers)
            {
                try
                {
                    devices.AddRange(provider.Poll(token));
                }
                catch (Exception ex)
                {
                    Log.Error($"provider {provider.Name} failed", ex);
                }
            }

            try
            {
                Updated?.Invoke(devices);
            }
            catch (Exception ex)
            {
                Log.Error("update handler failed", ex);
            }

            try
            {
                await _wake.WaitAsync(_interval, token).ConfigureAwait(false);
                while (_wake.CurrentCount > 0)
                {
                    _wake.Wait(0);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
