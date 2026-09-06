using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OpenAorus.Hardware.Lighting;

/// <summary>
/// Finds the Ione lighting collection and exchanges 264-byte feature reports with it.
/// The collection is identified by its HID capabilities (usage page 0xFF01, usage 1,
/// feature length 264), never by parsing the device path: the same PID exposes about
/// ten collections and only one of them is the lighting endpoint.
/// </summary>
public sealed class KeyboardHid : IKeyboardHid
{
    public const int ReportLength = 264;
    public const byte ReportId = 0x07;

    public static IReadOnlyList<ushort> SupportedVids { get; } = new ushort[] { 0x1044, 0x0414 };
    public static IReadOnlyList<ushort> SupportedPids { get; } = new ushort[] { 0x7A3C, 0x7A3D, 0x7A3F };

    private const ushort LightingUsagePage = 0xFF01;
    private const ushort LightingUsage = 0x0001;

    private readonly SafeFileHandle? _handle;

    public bool IsPresent => _handle is { IsInvalid: false };
    public KeyboardIdentity? Identity { get; }

    private KeyboardHid(SafeFileHandle? handle, KeyboardIdentity? identity)
    {
        _handle = handle;
        Identity = identity;
    }

    public static KeyboardHid Open()
    {
        foreach (var path in EnumerateHidPaths())
        {
            SafeFileHandle handle = CreateFile(path, 0, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (handle.IsInvalid) { handle.Dispose(); continue; }

            var attrs = new HiddAttributes { Size = Marshal.SizeOf<HiddAttributes>() };
            if (!HidD_GetAttributes(handle, ref attrs)
                || !SupportedVids.Contains(attrs.VendorId)
                || !SupportedPids.Contains(attrs.ProductId))
            {
                handle.Dispose();
                continue;
            }

            if (!HidD_GetPreparsedData(handle, out var preparsed)) { handle.Dispose(); continue; }
            try
            {
                if (HidP_GetCaps(preparsed, out var caps) != HidpStatusSuccess
                    || caps.UsagePage != LightingUsagePage
                    || caps.Usage != LightingUsage
                    || caps.FeatureReportByteLength != ReportLength)
                {
                    handle.Dispose();
                    continue;
                }
            }
            finally { HidD_FreePreparsedData(preparsed); }

            return new KeyboardHid(handle, new KeyboardIdentity(attrs.VendorId, attrs.ProductId, path, ReadProduct(handle)));
        }
        return new KeyboardHid(null, null);
    }

    public bool SetFeature(byte[] report)
    {
        if (_handle is null || _handle.IsInvalid) return false;
        if (report.Length != ReportLength) throw new ArgumentException($"Report must be {ReportLength} bytes.", nameof(report));
        return HidD_SetFeature(_handle, report, (uint)report.Length);
    }

    public byte[]? GetFeature()
    {
        if (_handle is null || _handle.IsInvalid) return null;
        var buffer = new byte[ReportLength];
        buffer[0] = ReportId;
        return HidD_GetFeature(_handle, buffer, (uint)buffer.Length) ? buffer : null;
    }

    public void Dispose() => _handle?.Dispose();

    private static string ReadProduct(SafeFileHandle handle)
    {
        var buffer = new byte[256];
        return HidD_GetProductString(handle, buffer, (uint)buffer.Length)
            ? System.Text.Encoding.Unicode.GetString(buffer).TrimEnd('\0')
            : string.Empty;
    }

    private static IEnumerable<string> EnumerateHidPaths()
    {
        HidD_GetHidGuid(out var guid);
        var set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (set == new IntPtr(-1)) yield break;
        try
        {
            var data = new SpDeviceInterfaceData { CbSize = Marshal.SizeOf<SpDeviceInterfaceData>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data); i++)
            {
                SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out var required, IntPtr.Zero);
                if (required == 0) continue;
                var buffer = Marshal.AllocHGlobal((int)required);
                try
                {
                    Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 4 + Marshal.SystemDefaultCharSize);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref data, buffer, required, out _, IntPtr.Zero)) continue;
                    var path = Marshal.PtrToStringAuto(buffer + 4);
                    if (!string.IsNullOrEmpty(path)) yield return path;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    private const int FileShareReadWrite = 0x3;
    private const int OpenExisting = 3;
    private const int DigcfPresent = 0x2;
    private const int DigcfDeviceInterface = 0x10;
    private const int HidpStatusSuccess = 0x00110000;

    [StructLayout(LayoutKind.Sequential)]
    private struct HiddAttributes { public int Size; public ushort VendorId; public ushort ProductId; public ushort VersionNumber; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData { public int CbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidpCaps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")] private static extern bool HidD_GetAttributes(SafeFileHandle handle, ref HiddAttributes attributes);
    [DllImport("hid.dll")] private static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr preparsed);
    [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(IntPtr preparsed);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr preparsed, out HidpCaps caps);
    [DllImport("hid.dll")] private static extern bool HidD_SetFeature(SafeFileHandle handle, byte[] buffer, uint length);
    [DllImport("hid.dll")] private static extern bool HidD_GetFeature(SafeFileHandle handle, byte[] buffer, uint length);
    [DllImport("hid.dll", CharSet = CharSet.Unicode)] private static extern bool HidD_GetProductString(SafeFileHandle handle, byte[] buffer, uint length);

    [DllImport("setupapi.dll", CharSet = CharSet.Auto)] private static extern IntPtr SetupDiGetClassDevs(ref Guid guid, IntPtr enumerator, IntPtr hwnd, int flags);
    [DllImport("setupapi.dll")] private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr deviceInfo, ref Guid guid, uint index, ref SpDeviceInterfaceData data);
    [DllImport("setupapi.dll", CharSet = CharSet.Auto)] private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SpDeviceInterfaceData data, IntPtr detail, uint detailSize, out uint required, IntPtr deviceInfo);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)] private static extern SafeFileHandle CreateFile(string path, int access, int share, IntPtr security, int disposition, int flags, IntPtr template);
}
