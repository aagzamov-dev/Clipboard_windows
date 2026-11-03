using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Clipboard.Windows.Native;

internal static class LocalImageFile
{
    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString { public ushort Length, MaximumLength; public nint Buffer; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public uint Length; public nint RootDirectory, ObjectName; public uint Attributes;
        public nint SecurityDescriptor, SecurityQualityOfService;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatus { public nint Status; public nuint Information; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint QueryDosDevice(string device, StringBuilder target, int capacity);
    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(out SafeFileHandle handle, uint access, ref ObjectAttributes attributes,
        out IoStatus status, nint allocation, uint fileAttributes, uint share, uint disposition, uint options, nint ea, uint eaLength);

    internal static FileStream? Open(string path)
    {
        // Do not probe UNC/device names, mapped shares, SUBST aliases, or linked directories.
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] is not ('\\' or '/')) return null;
        string full = Path.GetFullPath(path);
        var device = new StringBuilder(1024);
        if (QueryDosDevice(full[..2], device, device.Capacity) == 0) return null;
        string volume = device.ToString(); const string prefix = @"\Device\HarddiskVolume";
        if (!volume.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || volume.Length == prefix.Length || !volume[prefix.Length..].All(char.IsAsciiDigit)) return null;
        string nativePath = volume + full[2..];
        if (nativePath.Length > 32766) return null;
        nint buffer = Marshal.StringToHGlobalUni(nativePath), name = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        try
        {
            Marshal.StructureToPtr(new UnicodeString { Length = (ushort)(nativePath.Length * 2), MaximumLength = (ushort)((nativePath.Length + 1) * 2), Buffer = buffer }, name, false);
            // OBJ_DONT_REPARSE rejects redirection anywhere in the path during the open,
            // closing the check/open race. The device path avoids the DOS drive alias itself.
            var attributes = new ObjectAttributes { Length = (uint)Marshal.SizeOf<ObjectAttributes>(), ObjectName = name, Attributes = 0x1040 };
            int result = NtCreateFile(out var handle, 0x120089, ref attributes, out _, 0, 0, 7, 1, 0x200060, 0, 0);
            if (result < 0) { handle?.Dispose(); return null; }
            try
            {
                if ((File.GetAttributes(handle) & FileAttributes.ReparsePoint) != 0) { handle.Dispose(); return null; }
                return new FileStream(handle, FileAccess.Read);
            }
            catch { handle.Dispose(); throw; }
        }
        finally { Marshal.FreeHGlobal(name); Marshal.FreeHGlobal(buffer); }
    }
}
