using OpenAorus.Hardware.Wmi;

namespace OpenAorus.Hardware.Tests;

public class WmiResultTests
{
    [Fact]
    public void GetInt_converts_byte_and_ushort_outputs()
    {
        var r = WmiResult.Ok(new Dictionary<string, object> { ["Data"] = (byte)229, ["Value"] = (ushort)700 });
        Assert.Equal(229, r.GetInt("Data"));
        Assert.Equal(700, r.GetInt("Value"));
    }

    [Fact]
    public void GetInt_returns_fallback_when_missing_or_failed()
    {
        Assert.Equal(-1, WmiResult.Ok().GetInt("Data", -1));
        Assert.Equal(-1, WmiResult.Fail("boom").GetInt("Data", -1));
    }

    [Fact]
    public void SetData_extension_sends_byte_named_Data_to_Set_class()
    {
        var fake = new FakeGigabyteWmi();
        fake.SetData("SetFixedFanSpeed", 100);
        var call = Assert.Single(fake.Calls);
        Assert.Equal(WmiClass.Set, call.Class);
        Assert.Equal("SetFixedFanSpeed", call.Method);
        Assert.Equal((byte)100, call.Args["Data"]);
    }

    [Fact]
    public void Get_extension_calls_Get_class_with_no_args()
    {
        var fake = new FakeGigabyteWmi();
        fake.Respond("getCpuTemp", (ushort)61);
        Assert.Equal(61, fake.Get("getCpuTemp").GetInt("Data"));
        Assert.Equal(WmiClass.Get, fake.Calls[0].Class);
        Assert.Empty(fake.Calls[0].Args);
    }
}
