using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenAorus.Hardware.Fans;

namespace OpenAorus.App.ViewModels;

public partial class CurvePointVm : ObservableObject
{
    [ObservableProperty] private int _temperature;
    [ObservableProperty] private int _dutyPercent;
    public CurvePointVm(int t, int d) { _temperature = t; _dutyPercent = d; }
    public FanCurvePoint ToPoint() => new(Temperature, DutyPercent);
}

public partial class CurveEditorViewModel : ObservableObject
{
    public ObservableCollection<CurvePointVm> Points { get; } = new();

    [ObservableProperty] private string _validationText = "";
    [ObservableProperty] private bool _isValid = true;

    public event Action? Changed;

    public CurveEditorViewModel(IEnumerable<FanCurvePoint> initial)
    {
        Points.CollectionChanged += OnCollectionChanged;
        LoadFrom(initial);
    }

    public void LoadFrom(IEnumerable<FanCurvePoint> points)
    {
        foreach (var p in Points) p.PropertyChanged -= OnPointChanged;
        Points.Clear();
        foreach (var p in points) Points.Add(Attach(new CurvePointVm(p.Temperature, p.DutyPercent)));
        Revalidate();
    }

    public FanCurve ToCurve() => new(Points.Select(p => p.ToPoint()));

    private CurvePointVm Attach(CurvePointVm vm) { vm.PropertyChanged += OnPointChanged; return vm; }

    private void OnCollectionChanged(object? s, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null) foreach (CurvePointVm p in e.NewItems) Attach(p);
        if (e.OldItems is not null) foreach (CurvePointVm p in e.OldItems) p.PropertyChanged -= OnPointChanged;
        Revalidate();
    }

    private void OnPointChanged(object? s, PropertyChangedEventArgs e) => Revalidate();

    public void Revalidate()
    {
        var errors = ToCurve().Validate();
        IsValid = errors.Count == 0;
        ValidationText = IsValid ? $"{Points.Count} points" : errors[0];
        Changed?.Invoke();
    }

    [RelayCommand]
    private void AddPoint()
    {
        if (Points.Count >= FanCurve.MaxPoints) return;
        var last = Points.LastOrDefault();
        var t = Math.Min(100, (last?.Temperature ?? 35) + 5);
        var d = Math.Min(100, (last?.DutyPercent ?? 25) + 5);
        Points.Add(new CurvePointVm(t, d));
    }

    [RelayCommand]
    private void RemovePoint(CurvePointVm? p)
    {
        if (p is not null && Points.Count > FanCurve.MinPoints) Points.Remove(p);
    }

    [RelayCommand]
    private void Reset() => LoadFrom(FanCurve.Default.Points);
}
