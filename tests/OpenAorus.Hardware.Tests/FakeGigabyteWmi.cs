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

    public WmiResult Invoke(WmiClass cls, string method, IReadOnlyDictionary<string, object>? args = null)
    {
        Calls.Add(new WmiCall(cls, method, args ?? new Dictionary<string, object>()));
        if (FailOn.Contains(method)) return WmiResult.Fail($"{method} failed (fake)");
        return Responses.TryGetValue(method, out var o) ? WmiResult.Ok(o) : WmiResult.Ok();
    }

    public void Respond(string method, object data) =>
        Responses[method] = new Dictionary<string, object> { ["Data"] = data };

    public IEnumerable<string> MethodsCalled => Calls.Select(c => c.Method);
}
