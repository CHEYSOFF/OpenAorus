using OpenAorus.Hardware.Profiles;

namespace OpenAorus.Hardware.Ui;

public enum BannerKind { None, Info, Warning, Error }

/// <summary>How much of the banner an override notice is entitled to hold on to.</summary>
/// <remarks>
/// <para>
/// The distinction is between news and consequence. Most of what the banner reports is news the
/// owner has not heard - a write that failed, a curve that was saved - and an override notice
/// stands aside for the next one of those, whatever it is.
/// </para>
/// <para>
/// Some notices name a cause the app can expect to see reported back at it as an error. A machine
/// whose Gigabyte WMI schema is not registered has no <c>GB_WMIACPI_Get</c> either, so every
/// sensor poll fails too; a settings file that had to be repaired is a state the owner is being
/// asked to look at, not a reading. Letting a derived error take the banner replaces a cause the
/// owner can act on with a symptom they cannot, once per poll, a second after startup.
/// </para>
/// </remarks>
public enum NoticeRank
{
    /// <summary>The next report of any kind reveals what is underneath. The default.</summary>
    Ordinary,

    /// <summary>
    /// A failing sensor read does not displace this notice - it is recorded underneath and
    /// surfaces if anything else supersedes it.
    /// </summary>
    /// <remarks>Nothing else changes: a failure or notice the owner's own action produced still
    /// supersedes it, and so does a sensor read that succeeded, because a machine that is
    /// answering again is news this notice was not covering - and on a machine the owner has just
    /// fixed, it is what takes the notice down.</remarks>
    OutranksDerivedErrors,
}

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
/// An <see cref="ProfileStatus.Unknown"/> model's banner is permanent: no method here ever changes it -
/// except <see cref="ReportOverrideNotice"/>, described below.
///
/// On top of all three sits an optional override notice: a message that must reach the owner regardless
/// of model status (e.g. "your settings were reset"). It is set by <see cref="ReportOverrideNotice"/>,
/// which is the one method that ignores the unknown-model guard, and it visually replaces whatever
/// Kind/Text would otherwise report without touching the baseline/sensor/explicit state underneath. The
/// next call to <see cref="ReportSensorResult"/>, <see cref="ReportFailure"/>, <see cref="ReportNotice"/>
/// or <see cref="ReportSuccess"/> on a non-unknown model clears the override and reveals whatever that
/// underlying state is - e.g. an untested model's baseline warning, not an empty banner. On an unknown
/// model those methods still no-op entirely, so an override notice raised there is as permanent as the
/// baseline error it sits on top of.
///
/// One exception, and it belongs to the notice rather than to any feature: a notice raised as
/// <see cref="NoticeRank.OutranksDerivedErrors"/> is not cleared by a sensor read that FAILED, because
/// on the machines those notices are about the failing read is a consequence of what the notice already
/// explains. The read is still recorded in the sensor state underneath, so anything that does supersede
/// the notice reveals it. Everything else is unchanged, the successful read included.
/// </summary>
public sealed class BannerState
{
    private enum Source { Baseline, Sensor, Explicit }

    private readonly ProfileStatus _status;
    private readonly BannerKind _baselineKind;
    private readonly string _baselineText;
    private Source _source = Source.Baseline;

    private BannerKind _normalKind;
    private string _normalText;

    private bool _overrideActive;
    private BannerKind _overrideKind;
    private string _overrideText = "";
    private NoticeRank _overrideRank;

    public BannerKind Kind => _overrideActive ? _overrideKind : _normalKind;
    public string Text => _overrideActive ? _overrideText : _normalText;

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
        _normalKind = _baselineKind;
        _normalText = _baselineText;
    }

    /// <summary>
    /// Report a one-off notice that must reach the owner on every model, including an
    /// <see cref="ProfileStatus.Unknown"/> model whose banner is otherwise permanent. Layers over whatever
    /// the banner is currently tracking without disturbing it - see the class remarks for how and when it
    /// is revealed again.
    /// </summary>
    /// <param name="kind">How to draw it.</param>
    /// <param name="text">What it says.</param>
    /// <param name="rank">Whether a failing sensor read is independent news or a consequence of what
    /// this notice explains. See <see cref="NoticeRank"/>; the default is the older behaviour.</param>
    public void ReportOverrideNotice(BannerKind kind, string text, NoticeRank rank = NoticeRank.Ordinary)
    {
        _overrideActive = true;
        _overrideKind = kind;
        _overrideText = text;
        _overrideRank = rank;
    }

    /// <summary>Report the outcome of a sensor poll. Only clears a banner this same method previously raised.</summary>
    /// <remarks>A failed poll leaves an <see cref="NoticeRank.OutranksDerivedErrors"/> notice showing:
    /// it is a consequence of what that notice already explains, and the owner needs the cause. The
    /// reading is still recorded underneath either way, so nothing is lost by not showing it yet.</remarks>
    public void ReportSensorResult(bool ok, string? error)
    {
        if (_status == ProfileStatus.Unknown) return;

        // The one report that does not automatically take an override notice down.
        var outranked = !ok && _overrideActive && _overrideRank == NoticeRank.OutranksDerivedErrors;
        if (!outranked) ClearOverride();

        if (!ok)
        {
            if (_source == Source.Baseline && _normalKind == BannerKind.None)
            {
                _normalKind = BannerKind.Error;
                _normalText = error ?? "sensor read failed";
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
        ClearOverride();

        _normalKind = kind;
        _normalText = text;
        _source = Source.Explicit;
    }

    /// <summary>
    /// Report a successful hardware action. Clears only a banner it is entitled to supersede - a
    /// previously reported failure or notice - and leaves an active sensor error or the baseline alone.
    /// </summary>
    public void ReportSuccess()
    {
        if (_status == ProfileStatus.Unknown) return;
        ClearOverride();
        if (_source != Source.Explicit) return;

        _source = Source.Baseline;
        RevertToBaseline();
    }

    private void ClearOverride()
    {
        _overrideActive = false;
        _overrideRank = NoticeRank.Ordinary;
    }

    private void RevertToBaseline()
    {
        _normalKind = _baselineKind;
        _normalText = _baselineText;
    }
}
