namespace OpenAorus.Hardware.Wmi;

public enum WmiClass { Get, Set }

/// <summary>The only door to the embedded controller. Real impl: <see cref="GigabyteWmi"/>.</summary>
public interface IGigabyteWmi
{
    WmiResult Invoke(WmiClass cls, string method, IReadOnlyDictionary<string, object>? args = null);
}

public static class GigabyteWmiExtensions
{
    public static WmiResult SetData(this IGigabyteWmi wmi, string method, byte data) =>
        wmi.Invoke(WmiClass.Set, method, new Dictionary<string, object> { ["Data"] = data });

    public static WmiResult Get(this IGigabyteWmi wmi, string method) =>
        wmi.Invoke(WmiClass.Get, method);
}
