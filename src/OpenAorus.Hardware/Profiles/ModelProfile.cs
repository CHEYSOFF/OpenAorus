namespace OpenAorus.Hardware.Profiles;

public enum ProfileStatus { Tested, Untested, Unknown }

/// <summary>Per-model capability table. Add a tested entry once an owner confirms duty read-back matches GCC.</summary>
public sealed record ModelProfile(
    string Name,
    ProfileStatus Status,
    int DutyMax,
    int FanCount,
    bool HasGpuTemp1,
    bool RpmByteSwapped)
{
    private static readonly string[] FamilyPrefixes = { "AORUS", "AERO", "GIGABYTE" };

    private static readonly Dictionary<string, ModelProfile> Tested = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AORUS 17G KD"] = new("AORUS 17G KD", ProfileStatus.Tested, DutyMax: 229, FanCount: 2, HasGpuTemp1: true, RpmByteSwapped: true),
    };

    public bool CanWrite => Status != ProfileStatus.Unknown;

    public static ModelProfile Detect(string? productName)
    {
        var name = (productName ?? string.Empty).Trim();
        if (Tested.TryGetValue(name, out var tested)) return tested;
        if (FamilyPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            return new ModelProfile(name, ProfileStatus.Untested, 229, 2, true, true);
        return new ModelProfile(name.Length == 0 ? "(unknown)" : name, ProfileStatus.Unknown, 229, 2, true, true);
    }

    public byte ToDuty(int percent)
    {
        var clamped = Math.Clamp(percent, 0, 100);
        return (byte)Math.Round(clamped * DutyMax / 100.0, MidpointRounding.AwayFromZero);
    }

    public int ToPercent(int duty)
    {
        var clamped = Math.Clamp(duty, 0, DutyMax);
        return (int)Math.Round(clamped * 100.0 / DutyMax, MidpointRounding.AwayFromZero);
    }
}
