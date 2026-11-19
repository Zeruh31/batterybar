using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace BatteryBar.Core.Hid;

/// <summary>One HID top-level collection (one device interface path).</summary>
public sealed record HidCollectionInfo(
    string Path,
    ushort VendorId,
    ushort ProductId,
    ushort UsagePage,
    ushort Usage,
    int InputReportLength,
    int OutputReportLength,
    string Product)
{
    /// <summary>Collections of the same physical USB interface share this key.</summary>
    public string GroupKey => HidEnumerator.GroupKeyFromPath(Path);

    public override string ToString() =>
        $"{VendorId:X4}:{ProductId:X4} page={UsagePage:X4} usage={Usage:X4} in={InputReportLength} out={OutputReportLength} '{Product}' {Path}";
}

public static partial class HidEnumerator
{
    [GeneratedRegex(@"vid_([0-9a-f]{4})&pid_([0-9a-f]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex VidPidRegex();

    [GeneratedRegex(@"&col[0-9a-f]+", RegexOptions.IgnoreCase)]
    private static partial Regex CollectionRegex();

    /// <summary>Lists present HID collections, optionally only those of one vendor.</summary>
    public static IReadOnlyList<HidCollectionInfo> Enumerate(ushort? vendorId = null)
    {
        var result = new List<HidCollectionInfo>();
        foreach (string path in GetInterfacePaths())
        {
            if (vendorId is ushort vid && ParseVidPid(path)?.Vid != vid)
            {
                continue;
            }

            HidCollectionInfo? info = TryDescribe(path);
            if (info is not null)
            {
                result.Add(info);
            }
        }

        return result;
    }

    internal static (ushort Vid, ushort Pid)? ParseVidPid(string path)
    {
        Match m = VidPidRegex().Match(path);
        if (!m.Success)
        {
            return null;
        }

        return (ushort.Parse(m.Groups[1].Value, NumberStyles.HexNumber), ushort.Parse(m.Groups[2].Value, NumberStyles.HexNumber));
    }

    /// <summary>
    /// "\\?\hid#vid_046d&amp;pid_c54d&amp;mi_02&amp;col01#a&amp;1234abcd&amp;0&amp;0000#{guid}" and its col02 sibling
    /// map to the same key: the collection suffix and the last instance segment are dropped.
    /// </summary>
    internal static string GroupKeyFromPath(string path)
    {
        string[] parts = path.ToLowerInvariant().Split('#');
        if (parts.Length < 3)
        {
            return path.ToLowerInvariant();
        }

        string hardware = CollectionRegex().Replace(parts[1], string.Empty);
        string instance = parts[2];
        int lastAmp = instance.LastIndexOf('&');
        if (lastAmp > 0)
        {
            instance = instance[..lastAmp];
        }

        return hardware + "#" + instance;
    }

    private static List<string> GetInterfacePaths()
    {
        Guid guid = HidNative.HidInterfaceGuid;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (HidNative.CM_Get_Device_Interface_List_SizeW(out uint length, ref guid, null, HidNative.CM_GET_DEVICE_INTERFACE_LIST_PRESENT) != HidNative.CR_SUCCESS)
            {
                return [];
            }

            var buffer = new char[length];
            int cr = HidNative.CM_Get_Device_Interface_ListW(ref guid, null, buffer, length, HidNative.CM_GET_DEVICE_INTERFACE_LIST_PRESENT);
            if (cr == HidNative.CR_BUFFER_SMALL)
            {
                continue;
            }

            if (cr != HidNative.CR_SUCCESS)
            {
                return [];
            }

            return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
        }

        return [];
    }

    private static unsafe HidCollectionInfo? TryDescribe(string path)
    {
        // Access 0 is enough for attributes and caps, and works even for collections owned by the system (mouse, keyboard).
        using SafeFileHandle handle = HidNative.CreateFileW(path, 0, HidNative.FILE_SHARE_READ | HidNative.FILE_SHARE_WRITE, IntPtr.Zero, HidNative.OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return null;
        }

        var attributes = new HidNative.HIDD_ATTRIBUTES { Size = (uint)sizeof(HidNative.HIDD_ATTRIBUTES) };
        if (!HidNative.HidD_GetAttributes(handle, ref attributes))
        {
            return null;
        }

        if (!HidNative.HidD_GetPreparsedData(handle, out IntPtr preparsed))
        {
            return null;
        }

        HidNative.HIDP_CAPS caps;
        try
        {
            if (HidNative.HidP_GetCaps(preparsed, out caps) != HidNative.HIDP_STATUS_SUCCESS)
            {
                return null;
            }
        }
        finally
        {
            HidNative.HidD_FreePreparsedData(preparsed);
        }

        string product = string.Empty;
        byte* buffer = stackalloc byte[256];
        if (HidNative.HidD_GetProductString(handle, buffer, 256))
        {
            product = Encoding.Unicode.GetString(buffer, 256).TrimEnd('\0');
            int nul = product.IndexOf('\0');
            if (nul >= 0)
            {
                product = product[..nul];
            }
        }

        return new HidCollectionInfo(path, attributes.VendorID, attributes.ProductID, caps.UsagePage, caps.Usage,
            caps.InputReportByteLength, caps.OutputReportByteLength, product);
    }
}
