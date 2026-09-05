using OpenAorus.Hardware.Profiles;

namespace OpenAorus.Hardware.Ui;

public enum BannerKind { None, Info, Warning, Error }

/// <summary>
/// Owns the single owner-facing banner shown above the fan/sensor UI.
///
/// Every change to the banner comes from one of three sources, tracked internally so later reports know
/// what they are and are not allowed to overwrite:
/// - Baseline: the model's own status (an <see cref="ProfileStatus.Untested"/> warning, an
///   <see cref="ProfileStatus.Unknown"/> read-only error, or nothing for a tested model).
/// - Sensor: raised by <see cref="ReportSensorResult"/> when a poll fails. Cleared only by a later
///   successful sensor read - nothing else touches it, and it never overwrites something already showing.
/// - Explicit: raised by <see cref="ReportFailure"/> or <see cref="ReportNotice"/> - a caller-reported
///   failure or message. Persists until superseded by another explicit report or by
///   <see cref="ReportSuccess"/>; a successful sensor read never clears it.
///
/// An <see cref="ProfileStatus.Unknown"/> model's banner is permanent: no method here ever changes it.
/// </summary>
public sealed class BannerState
{
    private enum Source { Baseline, Sensor, Explicit }

    private readonly ProfileStatus _status;
    private readonly BannerKind _baselineKind;
    private readonly string _baselineText;
    private Source _source = Source.Baseline;

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
            if (_source == Source.Baseline && Kind == BannerKind.None)
            {
                Kind = BannerKind.Error;
                Text = error ?? "sensor read failed";
                _source = Source.Sensor;
            }
            // Otherwise something is already showing (a baseline warning, an explicit failure/notice, or
            // an existing sensor error) - leave it alone rather than fighting it.
        }
        else if (_source == Source.Sensor)
        {
            _source = Source.Baseline;
            RevertToBaseline();
        }
    }

    /// <summary>Report a failure (e.g. a fan-mode write) that must persist until explicitly superseded.</summary>
    public void ReportFailure(string error) => ReportNotice(BannerKind.Error, error);

    /// <summary>
    /// Report a caller-supplied banner message (e.g. curve validation, a battery-limit failure). Persists
    /// with the same rules as <see cref="ReportFailure"/>: it stays until another explicit report or a
    /// <see cref="ReportSuccess"/> supersedes it, and a successful sensor read never clears it.
    /// </summary>
    public void ReportNotice(BannerKind kind, string text)
    {
        if (_status == ProfileStatus.Unknown) return;

        Kind = kind;
        Text = text;
        _source = Source.Explicit;
    }

    /// <summary>
    /// Report a successful hardware action. Clears only a banner it is entitled to supersede - a
    /// previously reported failure or notice - and leaves an active sensor error or the baseline alone.
    /// </summary>
    public void ReportSuccess()
    {
        if (_status == ProfileStatus.Unknown) return;
        if (_source != Source.Explicit) return;

        _source = Source.Baseline;
        RevertToBaseline();
    }

    private void RevertToBaseline()
    {
        Kind = _baselineKind;
        Text = _baselineText;
    }
}
