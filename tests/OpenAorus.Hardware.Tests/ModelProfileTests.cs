using OpenAorus.Hardware.Profiles;

namespace OpenAorus.Hardware.Tests;

public class ModelProfileTests
{
    [Fact]
    public void Aorus_17G_KD_is_tested_with_duty_max_229()
    {
        var p = ModelProfile.Detect("AORUS 17G KD");
        Assert.Equal(ProfileStatus.Tested, p.Status);
        Assert.Equal(229, p.DutyMax);
        Assert.Equal(2, p.FanCount);
        Assert.True(p.RpmByteSwapped);
        Assert.True(p.CanWrite);
    }

    [Theory]
    [InlineData("AORUS 15P XD")]
    [InlineData("AERO 16 KE5")]
    [InlineData("GIGABYTE GAMING A16")]
    [InlineData("aorus 17x ye5")]
    public void Gigabyte_family_names_are_untested_but_writable(string name)
    {
        var p = ModelProfile.Detect(name);
        Assert.Equal(ProfileStatus.Untested, p.Status);
        Assert.True(p.CanWrite);
        Assert.Equal(229, p.DutyMax);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ROG Zephyrus G14")]
    public void Other_names_are_unknown_and_read_only(string? name)
    {
        var p = ModelProfile.Detect(name);
        Assert.Equal(ProfileStatus.Unknown, p.Status);
        Assert.False(p.CanWrite);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(50, 115)]   // 114.5 rounds away from zero
    [InlineData(100, 229)]
    [InlineData(150, 229)]  // clamped
    [InlineData(-5, 0)]     // clamped
    public void ToDuty_scales_percent_to_229(int percent, int duty)
    {
        var p = ModelProfile.Detect("AORUS 17G KD");
        Assert.Equal((byte)duty, p.ToDuty(percent));
    }

    [Theory]
    [InlineData(229, 100)]
    [InlineData(115, 50)]
    [InlineData(0, 0)]
    [InlineData(255, 100)]  // clamped
    public void ToPercent_scales_duty_to_percent(int duty, int percent)
    {
        var p = ModelProfile.Detect("AORUS 17G KD");
        Assert.Equal(percent, p.ToPercent(duty));
    }
}
