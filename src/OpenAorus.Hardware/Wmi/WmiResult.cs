namespace OpenAorus.Hardware.Wmi;

public sealed record WmiResult(bool Success, IReadOnlyDictionary<string, object> Out, string? Error)
{
    private static readonly IReadOnlyDictionary<string, object> Empty = new Dictionary<string, object>();

    public static WmiResult Ok(IReadOnlyDictionary<string, object>? outParams = null) => new(true, outParams ?? Empty, null);
    public static WmiResult Fail(string error) => new(false, Empty, error);

    public int GetInt(string name, int fallback = 0)
    {
        if (!Success || !Out.TryGetValue(name, out var v) || v is null) return fallback;
        try { return Convert.ToInt32(v); } catch (Exception) { return fallback; }
    }
}
