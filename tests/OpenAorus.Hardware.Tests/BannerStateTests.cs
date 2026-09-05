using OpenAorus.Hardware.Profiles;
using OpenAorus.Hardware.Ui;

namespace OpenAorus.Hardware.Tests;

public class BannerStateTests
{
    private static ModelProfile Tested() => ModelProfile.Detect("AORUS 17G KD");
    private static ModelProfile Untested() => ModelProfile.Detect("AORUS 15P XD");
    private static ModelProfile Unknown() => ModelProfile.Detect("ROG Zephyrus G14");

    [Fact]
    public void Untested_model_starts_with_a_warning()
    {
        var b = new BannerState(Untested());
        Assert.Equal(BannerKind.Warning, b.Kind);
        Assert.Contains("Untested model", b.Text);
    }

    [Fact]
    public void Untested_model_warning_survives_a_transient_sensor_error_and_its_recovery()
    {
        var b = new BannerState(Untested());
        var warningText = b.Text;

        b.ReportSensorResult(ok: false, error: "sensor read failed");
        Assert.Equal(BannerKind.Warning, b.Kind);
        Assert.Equal(warningText, b.Text);

        b.ReportSensorResult(ok: true, error: null);
        Assert.Equal(BannerKind.Warning, b.Kind);
        Assert.Equal(warningText, b.Text);
    }

    [Fact]
    public void Unknown_model_error_is_never_cleared_by_a_successful_read()
    {
        var b = new BannerState(Unknown());
        var errorText = b.Text;
        Assert.Equal(BannerKind.Error, b.Kind);

        b.ReportSensorResult(ok: true, error: null);
        Assert.Equal(BannerKind.Error, b.Kind);
        Assert.Equal(errorText, b.Text);
    }

    [Fact]
    public void Unknown_model_error_is_never_cleared_by_reported_success_or_failure()
    {
        var b = new BannerState(Unknown());
        var errorText = b.Text;

        b.ReportSuccess();
        Assert.Equal(BannerKind.Error, b.Kind);
        Assert.Equal(errorText, b.Text);

        b.ReportFailure("some other failure");
        Assert.Equal(BannerKind.Error, b.Kind);
        Assert.Equal(errorText, b.Text);
    }

    [Fact]
    public void Reported_failure_is_not_cleared_by_a_later_successful_sensor_read()
    {
        var b = new BannerState(Tested());
        b.ReportFailure("Startup apply failed: boom");

        b.ReportSensorResult(ok: true, error: null);

        Assert.Equal(BannerKind.Error, b.Kind);
        Assert.Equal("Startup apply failed: boom", b.Text);
    }

    [Fact]
    public void ReportSuccess_leaves_an_active_sensor_error_in_place()
    {
        var b = new BannerState(Tested());
        b.ReportSensorResult(ok: false, error: "sensor read failed");

        b.ReportSuccess();

        Assert.Equal(BannerKind.Error, b.Kind);
        Assert.Equal("sensor read failed", b.Text);
    }

    [Fact]
    public void Notice_survives_a_successful_sensor_read_and_is_replaced_by_a_later_failure()
    {
        var b = new BannerState(Tested());
        b.ReportNotice(BannerKind.Info, "curve saved");

        b.ReportSensorResult(ok: true, error: null);
        Assert.Equal(BannerKind.Info, b.Kind);
        Assert.Equal("curve saved", b.Text);

        b.ReportFailure("apply failed");
        Assert.Equal(BannerKind.Error, b.Kind);
        Assert.Equal("apply failed", b.Text);
    }

    [Fact]
    public void Reported_failure_is_cleared_by_a_subsequent_reported_success()
    {
        var b = new BannerState(Tested());
        b.ReportFailure("boom");

        b.ReportSuccess();

        Assert.Equal(BannerKind.None, b.Kind);
        Assert.Equal("", b.Text);
    }

    [Fact]
    public void Sensor_error_is_cleared_by_a_later_successful_read_on_a_tested_model()
    {
        var b = new BannerState(Tested());
        Assert.Equal(BannerKind.None, b.Kind);

        b.ReportSensorResult(ok: false, error: "sensor read failed");
        Assert.Equal(BannerKind.Error, b.Kind);
        Assert.Equal("sensor read failed", b.Text);

        b.ReportSensorResult(ok: true, error: null);
        Assert.Equal(BannerKind.None, b.Kind);
        Assert.Equal("", b.Text);
    }
}
