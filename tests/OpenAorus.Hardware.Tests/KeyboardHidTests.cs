using OpenAorus.Hardware.Lighting;

namespace OpenAorus.Hardware.Tests;

public class KeyboardHidTests
{
    [Fact]
    public void Report_constants_match_the_protocol()
    {
        Assert.Equal(264, KeyboardHid.ReportLength);
        Assert.Equal(0x07, KeyboardHid.ReportId);
    }

    [Fact]
    public void Supported_ids_cover_both_vendor_ids_and_the_three_ione_pids()
    {
        Assert.Contains((ushort)0x1044, KeyboardHid.SupportedVids);
        Assert.Contains((ushort)0x0414, KeyboardHid.SupportedVids);
        Assert.Equal(new ushort[] { 0x7A3C, 0x7A3D, 0x7A3F }, KeyboardHid.SupportedPids);
    }

    [Fact]
    public void Fake_records_written_reports_and_reports_their_command()
    {
        var hid = new FakeKeyboardHid();
        var report = new byte[264];
        report[0] = 0x07;
        report[1] = 0x02;
        Assert.True(hid.SetFeature(report));
        Assert.Single(hid.Written);
        Assert.Equal(0x02, hid.Command(0));
    }

    [Fact]
    public void Fake_write_failure_is_one_shot()
    {
        var hid = new FakeKeyboardHid { FailNextWrite = true };
        Assert.False(hid.SetFeature(new byte[264]));
        Assert.True(hid.SetFeature(new byte[264]));
    }
}
