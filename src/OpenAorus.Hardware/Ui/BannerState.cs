using OpenAorus.Hardware.Profiles;

namespace OpenAorus.Hardware.Ui;

public enum BannerKind { None, Info, Warning, Error }

/// <summary>
/// Owns the single owner-facing banner shown above the fan/sensor UI.
///
/// Rules, in priority order:
/// - An <see cref="ProfileStatus.Unknown"/> model's read-only banner is permanent: nothing ever changes it.
/// - A reported failure (<see cref="ReportFailure"/>) persists until something explicitly supersedes it -
///   a later successful hardware action (<see cref="ReportSuccess"/>) or another failure - never a sensor
///   read on its own, transient or not.
/// - A sensor error may only be cleared by a later successful sensor read, and only reverts to the
///   model's baseline state (an <see cref="ProfileStatus.Untested"/> model's warning, or nothing for a
///   tested model) - it never overwrites something already showing (a baseline warning, an active
///   failure, or another sensor error).
/// </summary>
public sealed class BannerState
{
    private readonly ProfileStatus _status;
    private readonly BannerKind _baselineKind;
    private readonly string _baselineText;
    private bool _sensorErrorActive;

    public BannerKind Kind { get; private set; }
    public string Text { get; private set; }

    public BannerState(ModelProfile profile)
    {
        _status = profile.Status;
        (_baselineKind, _baselineText) = _status switch
        {
            ProfileStatus.Unknown => (BannerKind.Error,
                $"'{profile.Name}' is not a recognised Gigabyte laptop. Read-only mode. Export diagnostics and open an issue."),
            ProfileStatus.Untested => (BannerKind.Warning,
                "Untested model - compare fan duty read-back with Gigabyte Control Center before trusting it."),
            _ => (BannerKind.None, ""),
        };
        Kind = _baselineKind;
        Text = _baselineText;
    }

    /// <summary>Report the outcome of a sensor poll. Only clears a banner this same method previously raised.</summary>
    public void ReportSensorResult(bool ok, string? error)
    {
        if (_status == ProfileStatus.Unknown) return;

        if (!ok)
        {
            if (Kind == BannerKind.None)
            {
                Kind = BannerKind.Error;
                Text = error ?? "sensor read failed";
                _sensorErrorActive = true;
            }
            // Otherwise something is already showing (a baseline warning, an active failure, or an
            // existing sensor error) - leave it alone rather than fighting it.
        }
        else if (_sensorErrorActive)
        {
            _sensorErrorActive = false;
            RevertToBaseline();
        }
    }

    /// <summary>Report a failure (e.g. a fan-mode write) that must persist until explicitly superseded.</summary>
    public void ReportFailure(string error)
    {
        if (_status == ProfileStatus.Unknown) return;

        _sensorErrorActive = false;
        Kind = BannerKind.Error;
        Text = error;
    }

    /// <summary>Report a successful hardware action, clearing any active error back to the baseline.</summary>
    public void ReportSuccess()
    {
        if (_status == ProfileStatus.Unknown) return;
        if (Kind != BannerKind.Error) return;

        _sensorErrorActive = false;
        RevertToBaseline();
    }

    private void RevertToBaseline()
    {
        Kind = _baselineKind;
        Text = _baselineText;
    }
}
