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

    public SensorPoller(SensorReader reader, int intervalMs)
    {
        _reader = reader;
        _timer.Interval = TimeSpan.FromMilliseconds(intervalMs);
        _timer.Tick += async (_, _) => await TickAsync();
    }

    public void Start() { _timer.Start(); _ = TickAsync(); }
    public void Stop() => _timer.Stop();
    public void SetInterval(int ms) => _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(250, ms));

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
