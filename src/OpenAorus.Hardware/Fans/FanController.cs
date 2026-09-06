using OpenAorus.Hardware.Profiles;
using OpenAorus.Hardware.Wmi;

namespace OpenAorus.Hardware.Fans;

/// <summary>
/// Applies fan modes with the exact GB_WMIACPI_Set sequences Gigabyte Control Center uses.
/// All writes are serialized; a 500 ms pause separates consecutive writes (GCC does the same).
/// </summary>
public sealed class FanController
{
    public const int StepDelayMs = 500;

    private readonly IGigabyteWmi _wmi;
    private readonly ModelProfile _profile;
    private readonly Func<int, Task> _delay;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FanMode? LastApplied { get; private set; }

    /// <summary>Raised once a mode's whole write sequence has reached the controller.</summary>
    /// <remarks>Every way a mode is applied - startup, resume, <c>--apply</c>, a mode click, the
    /// thermal watchdog - comes through <see cref="ApplyAsync"/>, so a listener that needs to know
    /// what the fans are actually running can hang off this one place instead of off each caller,
    /// where the next caller added would be the one that forgot.</remarks>
    public event Action<FanMode>? Applied;

    public FanController(IGigabyteWmi wmi, ModelProfile profile, Func<int, Task>? delay = null)
    {
        _wmi = wmi;
        _profile = profile;
        _delay = delay ?? (ms => Task.Delay(ms));
    }

    private sealed record Step(string Method, IReadOnlyDictionary<string, object> Args)
    {
        public static Step Data(string method, byte value) =>
            new(method, new Dictionary<string, object> { ["Data"] = value });

        public static Step Point(byte index, byte temperature, byte duty) =>
            new("SetFanIndexValue", new Dictionary<string, object>
            {
                ["Index"] = index, ["Temperture"] = temperature, ["Value"] = duty, // "Temperture" is the BIOS's spelling
            });
    }

    public async Task<WmiResult> ApplyAsync(FanMode mode, int fixedPercent = 50, FanCurve? curve = null, CancellationToken ct = default)
    {
        if (!_profile.CanWrite)
            return WmiResult.Fail($"Model '{_profile.Name}' is not recognised as a Gigabyte laptop; fan control is disabled.");

        List<Step> steps;
        try { steps = Build(mode, fixedPercent, curve ?? FanCurve.Default); }
        catch (ArgumentException ex) { return WmiResult.Fail(ex.Message); }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (var i = 0; i < steps.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var s = steps[i];
                var r = await Task.Run(() => _wmi.Invoke(WmiClass.Set, s.Method, s.Args), ct).ConfigureAwait(false);
                if (!r.Success)
                    return WmiResult.Fail($"Step {i + 1}/{steps.Count} {s.Method} failed: {r.Error}");
                if (i < steps.Count - 1)
                    await _delay(StepDelayMs).ConfigureAwait(false);
            }
            LastApplied = mode;
        }
        finally { _gate.Release(); }

        // Raised outside the gate: a handler that turns round and applies another mode would
        // otherwise deadlock on a semaphore this call still holds.
        Applied?.Invoke(mode);
        return WmiResult.Ok();
    }

    private List<Step> Build(FanMode mode, int fixedPercent, FanCurve curve)
    {
        var s = new List<Step> { Step.Data("SetCurrentFanStep", 0) };
        switch (mode)
        {
            case FanMode.Quiet:
            case FanMode.Normal:
            case FanMode.Gaming:
                s.Add(Step.Data("SetFixedFanStatus", 0));
                s.Add(Step.Data("SetStepFanStatus", 0));
                s.Add(Step.Data("SetAutoFanStatus", (byte)(mode == FanMode.Gaming ? 1 : 0)));
                s.Add(Step.Data("SetNvThermalTarget", (byte)(mode == FanMode.Quiet ? 1 : 0)));
                break;

            case FanMode.Turbo:
            case FanMode.Fixed:
            {
                // Fixed latches SetFixedFanStatus in the controller and ignores temperature for
                // as long as it stays latched - through app exit and through a reboot - so the
                // floor is applied here, where every caller passes, and not only at the slider.
                var duty = mode == FanMode.Turbo
                    ? (byte)_profile.DutyMax
                    : _profile.ToDuty(FanSafety.ClampFixedPercent(fixedPercent));
                s.Add(Step.Data("SetAutoFanStatus", 0));
                s.Add(Step.Data("SetNvThermalTarget", 0));
                s.Add(Step.Data("SetFixedFanSpeed", duty));
                s.Add(Step.Data("SetGPUFanDuty", duty));
                s.Add(Step.Data("SetStepFanStatus", 1));
                s.Add(Step.Data("SetFixedFanStatus", 1));
                break;
            }

            case FanMode.Custom:
            {
                var errors = curve.Validate();
                if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors));
                s.Add(Step.Data("SetFixedFanStatus", 0));
                s.Add(Step.Data("SetAutoFanStatus", 0));
                s.Add(Step.Data("SetNvThermalTarget", 0));
                s.Add(Step.Data("SetStepFanStatus", 1));
                byte index = 0;
                foreach (var p in curve.Points)
                    s.Add(Step.Point(index++, (byte)p.Temperature, _profile.ToDuty(p.DutyPercent)));
                if (index < FanCurve.MaxPoints)
                    s.Add(Step.Point(index, 0, 0)); // terminator: EC reads the table until (0,0)
                break;
            }

            default:
                throw new ArgumentException($"Unknown fan mode {mode}");
        }
        return s;
    }
}
