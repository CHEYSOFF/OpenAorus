using OpenAorus.Hardware.Fans;
using OpenAorus.Hardware.Sensors;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// The rolling average behind the two temperatures the window shows.
/// </summary>
/// <remarks>
/// Purely cosmetic, and deliberately so. CPU temperature genuinely moves twenty degrees between
/// one poll and the next, so a number redrawn from the raw sample once a second is a blur rather
/// than a reading. Nothing that decides anything is allowed near this - see
/// <see cref="MainViewModelWatchdogTests"/> for the test that pins the watchdog on the raw
/// samples - so the only thing it owes anyone is being a fair summary of the recent past.
/// </remarks>
public class TemperatureAverageTests
{
    [Fact]
    public void A_fresh_average_reads_zero()
    {
        Assert.Equal(0, new TemperatureAverage(4).Value);
    }

    [Fact]
    public void It_averages_what_it_has_before_the_window_is_full()
    {
        var a = new TemperatureAverage(4);
        Assert.Equal(60, a.Add(60));
        Assert.Equal(65, a.Add(70));
        Assert.Equal(70, a.Add(80));
    }

    [Fact]
    public void It_averages_only_the_last_few_once_the_window_is_full()
    {
        var a = new TemperatureAverage(3);
        a.Add(90);
        a.Add(90);
        a.Add(90);

        // The three 90s walk out of the window one at a time, so the old readings stop counting
        // rather than weighing on the number for ever.
        Assert.Equal(80, a.Add(60));
        Assert.Equal(70, a.Add(60));
        Assert.Equal(60, a.Add(60));
    }

    [Fact]
    public void It_rounds_rather_than_truncating()
    {
        var a = new TemperatureAverage(2);
        a.Add(60);
        Assert.Equal(61, a.Add(61));   // 60.5, and the honest whole number for it is 61
    }

    [Fact]
    public void A_single_spike_barely_moves_it()
    {
        var a = new TemperatureAverage(6);
        for (var i = 0; i < 6; i++) a.Add(60);

        // The whole point. One poll at 100 °C is a boost, not a fever, and a number that jumps to
        // it and back a second later is unreadable.
        Assert.InRange(a.Add(100), 60, 70);
    }

    [Theory]
    [InlineData(0)]     // what a failed getCpuTemp surfaces as
    [InlineData(255)]   // what a wedged controller reports
    public void An_implausible_reading_is_not_averaged_in(int celsius)
    {
        var a = new TemperatureAverage(4);
        a.Add(70);
        a.Add(70);

        // A dropped read is not a temperature, and averaging it in would produce exactly the
        // lurch this exists to remove - a display diving to 35 °C because one WMI call failed.
        Assert.Equal(70, a.Add(celsius));
        Assert.Equal(70, a.Value);
    }

    [Fact]
    public void An_implausible_reading_before_any_real_one_leaves_it_at_zero()
    {
        var a = new TemperatureAverage(4);
        Assert.Equal(0, a.Add(0));
    }

    [Fact]
    public void The_display_window_is_short_enough_to_still_be_a_live_reading()
    {
        // A few seconds of history at roughly a poll a second: long enough to settle the number,
        // short enough that it still follows the machine.
        Assert.InRange(TemperatureAverage.DisplaySamples, 3, 10);
    }

    [Fact]
    public void The_display_window_is_not_the_watchdogs_run()
    {
        // Two different jobs, and no longer even the same unit: this is a count of samples and
        // the watchdog's run is a duration in seconds. Tying them together - by number or by
        // making one read the other - would mean a change made for legibility silently moving
        // when the fans get taken off the owner. The count stays here and out of FanSafety.
        Assert.NotEqual(FanSafety.WatchdogSecondsToFire, TemperatureAverage.DisplaySamples);
        Assert.NotEqual(FanSafety.WatchdogSecondsToRelease, TemperatureAverage.DisplaySamples);
    }

    [Fact]
    public void A_window_of_less_than_one_sample_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TemperatureAverage(0));
    }
}
