using OpenAorus.Hardware.Lighting;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// The fake keyboard decides which report fails, and several controller tests turn on the
/// difference between one rejected report and every following one. That makes its own
/// behaviour worth pinning: a fake that fails twice would make a passing test out of a
/// controller that stopped at the wrong report.
/// </summary>
public class FakeKeyboardHidTests
{
    private static byte[] Report() => new byte[KeyboardHid.ReportLength];

    [Fact]
    public void FailWriteAt_consumes_FailNextWrite_so_exactly_one_write_fails()
    {
        var hid = new FakeKeyboardHid { FailWriteAt = 0, FailNextWrite = true };

        Assert.False(hid.SetFeature(Report()));  // write 0, the one that was asked for
        Assert.True(hid.SetFeature(Report()));   // write 1 must not inherit the other knob
    }
}
