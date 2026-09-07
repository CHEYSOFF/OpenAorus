using System.IO;
using System.Reflection;
using OpenAorus.App;

namespace OpenAorus.Hardware.Tests;

/// <summary>
/// That the two authored icons are in the assembly, under the names the tray asks for, with the
/// sizes Windows actually reaches for.
/// </summary>
/// <remarks>
/// <para>
/// The tray no longer draws its icon; it pulls a hand-authored .ico out of the assembly. That
/// makes two things breakable that a compiler cannot see: the file can fall out of the csproj, and
/// the LogicalName in the csproj can drift from the string in <see cref="TrayIcon"/>. Either one
/// throws at the first line of the app's own startup - after the WMI probe, with a window about to
/// be shown - which is the worst possible place to find out.
/// </para>
/// <para>
/// The bytes are read as an ICO container rather than through System.Drawing, so this needs
/// neither GDI nor a desktop: the test project deliberately has no WinForms reference.
/// </para>
/// </remarks>
public class TrayIconAssetTests
{
    /// <summary>The sizes the .ico must carry. 16 is the one the notification area draws at
    /// 100 % DPI, 20 at 125 % and 24 at 150 %; 32 and 48 are what the shell falls back to for
    /// everything larger, and 256 is what the file view and the installer use.</summary>
    private static readonly int[] RequiredSizes = { 16, 20, 24, 32, 48, 256 };

    /// <summary>The resource names, read back off <see cref="TrayIcon"/>'s own constants so this
    /// pins the actual coupling and not a copy of it.</summary>
    public static TheoryData<string> ResourceNames()
    {
        var data = new TheoryData<string>();
        foreach (var field in typeof(TrayIcon).GetFields(BindingFlags.NonPublic | BindingFlags.Static))
            if (field.IsLiteral && field.FieldType == typeof(string))
                data.Add((string)field.GetRawConstantValue()!);
        return data;
    }

    [Fact]
    public void The_tray_names_two_icons_a_normal_one_and_an_error_one()
    {
        Assert.Equal(2, ResourceNames().Count);
    }

    [Theory]
    [MemberData(nameof(ResourceNames))]
    public void Each_named_icon_is_in_the_assembly(string name)
    {
        using var stream = typeof(TrayIcon).Assembly.GetManifestResourceStream(name);
        Assert.NotNull(stream);
        Assert.True(stream!.Length > 0);
    }

    [Theory]
    [MemberData(nameof(ResourceNames))]
    public void Each_named_icon_carries_every_size_windows_reaches_for(string name)
    {
        var entries = Read(name);

        foreach (var size in RequiredSizes)
            Assert.Contains(entries, e => e.Width == size && e.Height == size);
    }

    [Theory]
    [MemberData(nameof(ResourceNames))]
    public void Every_entry_is_thirty_two_bit_and_inside_the_file(string name)
    {
        var entries = Read(name);

        // 32-bit throughout: the tile has rounded corners, so a 1-bit mask alone would leave
        // them jagged against whatever the taskbar is painted with.
        Assert.All(entries, e => Assert.Equal(32, e.BitCount));
        Assert.All(entries, e => Assert.True(e.Length > 0));
    }

    [Theory]
    [MemberData(nameof(ResourceNames))]
    public void The_sixteen_is_authored_rather_than_scaled_from_the_two_fifty_six(string name)
    {
        var entries = Read(name);
        var small = entries.Single(e => e.Width == 16);
        var large = entries.Single(e => e.Width == 256);

        // Not a check on how it looks - only a machine-checkable trace of the fact that the two
        // were drawn separately. A 16 produced by resampling the 256 through the same code path
        // would be byte-identical to one produced here, so what is pinned instead is that the
        // small entries are uncompressed DIBs and the 256 is PNG: the layout the shell expects,
        // and the layout that falls out of authoring each size on its own grid.
        Assert.False(IsPng(small.Data));
        Assert.True(IsPng(large.Data));
    }

    private sealed record Entry(int Width, int Height, int BitCount, int Length, byte[] Data);

    private static bool IsPng(byte[] data) =>
        data.Length >= 4 && data[0] == 0x89 && data[1] == 'P' && data[2] == 'N' && data[3] == 'G';

    private static IReadOnlyList<Entry> Read(string name)
    {
        using var stream = typeof(TrayIcon).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"'{name}' is not in the assembly.");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var bytes = ms.ToArray();

        Assert.Equal(0, BitConverter.ToUInt16(bytes, 0));   // reserved
        Assert.Equal(1, BitConverter.ToUInt16(bytes, 2));   // type: icon, not cursor
        var count = BitConverter.ToUInt16(bytes, 4);
        Assert.True(count > 0);

        var entries = new List<Entry>();
        for (var i = 0; i < count; i++)
        {
            var at = 6 + 16 * i;
            var width = bytes[at] == 0 ? 256 : bytes[at];               // 0 means 256
            var height = bytes[at + 1] == 0 ? 256 : bytes[at + 1];
            var bits = BitConverter.ToUInt16(bytes, at + 6);
            var length = BitConverter.ToInt32(bytes, at + 8);
            var offset = BitConverter.ToInt32(bytes, at + 12);
            Assert.InRange(offset, 0, bytes.Length);
            Assert.InRange(offset + length, 0, bytes.Length);
            entries.Add(new Entry(width, height, bits, length, bytes[offset..(offset + length)]));
        }
        return entries;
    }
}
