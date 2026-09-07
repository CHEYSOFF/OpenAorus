using System.Windows.Threading;
using OpenAorus.Hardware.Sensors;

namespace OpenAorus.App.ViewModels;

/// <summary>Reads sensors on a thread-pool thread on a timer and raises Updated on the UI thread.</summary>
public sealed class SensorPoller
{
    private readonly SensorReader _reader;
    private readonly DispatcherTimer _timer = new();
    private bool _reading;

    public event Action<SensorSnapshot>? Updated;

    /// <summary>The fastest this will poll, in milliseconds, whatever it is asked for.</summary>
    /// <remarks>Read by <see cref="FanWatchdog"/> as well: it is told the cadence its caller is
    /// polling at, and no cadence below this one is real.</remarks>
    public const int MinIntervalMs = 250;

    /// <summary>The interval this is actually running at, in milliseconds.</summary>
    /// <remarks>Not the number it was asked for - the one it settled on after
    /// <see cref="MinIntervalMs"/>. <see cref="MainViewModel"/> hands it to
    /// <see cref="FanWatchdog.Observe"/>, which needs the real cadence to tell a late poll from an
    /// ordinary one, so it must be what the timer is doing rather than what a setting says.</remarks>
    public int IntervalMs { get; private set; }

    public SensorPoller(SensorReader reader, int intervalMs)
    {
        _reader = reader;
        SetInterval(intervalMs);
        _timer.Tick += async (_, _) => await TickAsync();
    }

    public void Start() { _timer.Start(); _ = TickAsync(); }
    public void Stop() => _timer.Stop();

    public void SetInterval(int ms)
    {
        IntervalMs = Math.Max(MinIntervalMs, ms);
        _timer.Interval = TimeSpan.FromMilliseconds(IntervalMs);
    }

    private async Task TickAsync()
    {
        if (_reading) return;
        _reading = true;
        try
        {
            var snap = await Task.Run(_reader.Read);
            Updated?.Invoke(snap);
        }
        finally { _reading = false; }
    }
}
