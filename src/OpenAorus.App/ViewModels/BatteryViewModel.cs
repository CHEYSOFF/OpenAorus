using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenAorus.Hardware.Battery;
using OpenAorus.Hardware.Ui;

namespace OpenAorus.App.ViewModels;

public partial class BatteryViewModel : ObservableObject
{
    private readonly AppServices _s;
    private readonly Action<BannerKind, string> _banner;

    [ObservableProperty] private bool _limitEnabled;
    [ObservableProperty] private int _stopPercent;
    [ObservableProperty] private int _cycleCount;
    [ObservableProperty] private string _statusText = "";

    /// <summary>Whether the charge limit may be written. The model gate and the schema gate.</summary>
    public bool CanWrite => _s.Profile.CanWrite && _s.Schema.WritesUnlocked;

    /// <summary>Re-reads <see cref="CanWrite"/> after the schema card has changed the machine.</summary>
    /// <remarks>Pushed rather than polled: the card is in a dialog on top of this window, and the
    /// owner who has just pressed Check it works must not have to restart the app to reach the
    /// charge limit.</remarks>
    public void NoteWritesChanged() => OnPropertyChanged(nameof(CanWrite));

    public BatteryViewModel(AppServices s, Action<BannerKind, string> banner)
    {
        _s = s;
        _banner = banner;
        _limitEnabled = s.Settings.ChargeLimitEnabled;
        _stopPercent = s.Settings.ChargeStopPercent;
    }

    public async Task RefreshAsync()
    {
        var st = await Task.Run(_s.Battery.Read);
        if (!st.Ok) { StatusText = "battery read failed"; return; }
        CycleCount = st.CycleCount;
        LimitEnabled = st.CustomLimitEnabled;
        if (st.CustomLimitEnabled) StopPercent = st.StopPercent;
        StatusText = st.CustomLimitEnabled ? $"EC stops charging at {st.StopPercent} %" : "EC charges to 100 %";
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (!CanWrite) return;
        StopPercent = Math.Clamp(StopPercent / 5 * 5, BatteryController.MinStop, BatteryController.MaxStop);
        var r = await Task.Run(() => _s.Battery.SetLimit(LimitEnabled, StopPercent));
        if (!r.Success) { _banner(BannerKind.Error, r.Error!); return; }
        _s.Settings.ChargeLimitEnabled = LimitEnabled;
        _s.Settings.ChargeStopPercent = StopPercent;
        _s.Store.Save(_s.Settings);
        await RefreshAsync();
    }
}
