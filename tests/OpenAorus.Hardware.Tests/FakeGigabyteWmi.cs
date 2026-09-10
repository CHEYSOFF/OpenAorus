using OpenAorus.Hardware.Wmi;

namespace OpenAorus.Hardware.Tests;

public sealed record WmiCall(WmiClass Class, string Method, IReadOnlyDictionary<string, object> Args)
{
    public int Data => Convert.ToInt32(Args["Data"]);
    public override string ToString() =>
        $"{Class}.{Method}({string.Join(",", Args.Select(kv => $"{kv.Key}={kv.Value}"))})";
}

public sealed class FakeGigabyteWmi : IGigabyteWmi
{
    public List<WmiCall> Calls { get; } = new();
    public Dictionary<string, Dictionary<string, object>> Responses { get; } = new();
    public HashSet<string> FailOn { get; } = new();

    /// <summary>The fan table GetFanIndexValue answers from, by slot index.</summary>
    /// <remarks>Responses cannot express an answer that depends on an in parameter, and this is the
    /// one method that has one. An index that is not in here answers like any other unstubbed
    /// method: success, with nothing in it.</remarks>
    public Dictionary<int, (int Temperature, int Duty)> FanTable { get; set; } = new();

    public WmiResult Invoke(WmiClass cls, string method, IReadOnlyDictionary<string, object>? args = null)
    {
        Calls.Add(new WmiCall(cls, method, args ?? new Dictionary<string, object>()));
        if (FailOn.Contains(method)) return WmiResult.Fail($"{method} failed (fake)");
        if (method == "GetFanIndexValue" && args is not null && args.TryGetValue("Index", out var index) &&
            FanTable.TryGetValue(Convert.ToInt32(index), out var slot))
        {
            // "Temperture" is the BIOS's own spelling and the name the real provider answers under.
            return WmiResult.Ok(new Dictionary<string, object>
            {
                ["Temperture"] = slot.Temperature,
                ["Value"] = slot.Duty,
            });
        }
        return Responses.TryGetValue(method, out var o) ? WmiResult.Ok(o) : WmiResult.Ok();
    }

    public void Respond(string method, object data) =>
        Responses[method] = new Dictionary<string, object> { ["Data"] = data };

    public IEnumerable<string> MethodsCalled => Calls.Select(c => c.Method);
}
