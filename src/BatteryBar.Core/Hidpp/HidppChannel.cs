using System.Diagnostics;
using BatteryBar.Core.Hid;

namespace BatteryBar.Core.Hidpp;

/// <summary>
/// The HID++ collections (short / long / very long) of one USB interface, opened in shared mode.
/// Reads are taken from every collection because answers may arrive on a different one than the request
/// (e.g. a receiver answers a long request with a short HID++ 1.0 error).
/// </summary>
public sealed class HidppChannel : IDisposable
{
    /// <summary>Our software id (low nibble of byte 3). Non-zero so notifications are never mistaken for answers.</summary>
    public const byte SoftwareId = 0x0B;

    private readonly List<HidHandle> _handles;

    private HidppChannel(List<HidHandle> handles)
    {
        _handles = handles;
    }

    /// <summary>
    /// True for Logitech's HID++ vendor pages (0xFF00 receivers and older devices, 0xFF43 newer devices and
    /// headsets) when the report sizes match HID++ short/long/very long reports.
    /// </summary>
    public static bool IsHidppCollection(HidCollectionInfo info) =>
        info.UsagePage is 0xFF00 or 0xFF43
        && (info.InputReportLength == HidppMessages.ShortLength
            || info.InputReportLength == HidppMessages.LongLength
            || info.InputReportLength == HidppMessages.VeryLongLength);

    public static HidppChannel? Open(IEnumerable<HidCollectionInfo> collections)
    {
        var handles = new List<HidHandle>();
        foreach (HidCollectionInfo info in collections.Where(IsHidppCollection))
        {
            HidHandle? handle = HidHandle.Open(info);
            if (handle is not null)
            {
                handles.Add(handle);
            }
        }

        if (handles.Count == 0)
        {
            return null;
        }

        return new HidppChannel(handles);
    }

    /// <summary>Sends a HID++ 2.0 feature request and waits for its answer.</summary>
    public HidppResponse Request(byte deviceIndex, byte featureIndex, byte function, ReadOnlySpan<byte> parameters, int timeoutMs = 1500)
    {
        byte reportId = FindHandle(HidppMessages.LongLength) is not null || parameters.Length > 3
            ? HidppMessages.ReportLong
            : HidppMessages.ReportShort;
        byte functionAndSwId = (byte)((function << 4) | SoftwareId);
        return RawRequest(reportId, deviceIndex, featureIndex, functionAndSwId, parameters, timeoutMs);
    }

    /// <summary>Sends any HID++ report (also HID++ 1.0 register access) and waits for the matching answer.</summary>
    public HidppResponse RawRequest(byte reportId, byte deviceIndex, byte byte2, byte byte3, ReadOnlySpan<byte> parameters, int timeoutMs)
    {
        HidHandle? writer = FindHandle(HidppMessages.LengthOf(reportId));
        if (writer is null)
        {
            return HidppResponse.IoError;
        }

        byte[] request = HidppMessages.Build(reportId, deviceIndex, byte2, (byte)(byte3 >> 4), (byte)(byte3 & 0x0F), parameters);

        // Queue reads before writing so a fast answer cannot slip past.
        var readers = _handles.Where(h => h.StartRead()).ToList();
        if (!writer.Write(request, timeoutMs))
        {
            return HidppResponse.IoError;
        }

        Log.Debug($"-> {Convert.ToHexString(request)}");
        var clock = Stopwatch.StartNew();
        while (readers.Count > 0)
        {
            int remaining = timeoutMs - (int)clock.ElapsedMilliseconds;
            if (remaining <= 0)
            {
                break;
            }

            int signaled = WaitHandle.WaitAny(readers.Select(h => h.ReadEvent).ToArray(), remaining);
            if (signaled == WaitHandle.WaitTimeout)
            {
                break;
            }

            HidHandle reader = readers[signaled];
            byte[]? report = reader.CompleteRead();
            if (report is not null)
            {
                HidppResponse? response = HidppMessages.Match(report, deviceIndex, byte2, byte3);
                if (response is not null)
                {
                    Log.Debug($"<- {Convert.ToHexString(report)}");
                    return response;
                }
            }

            if (!reader.StartRead())
            {
                readers.RemoveAt(signaled);
            }
        }

        Log.Debug("<- timeout");
        return HidppResponse.Timeout;
    }

    public void Dispose()
    {
        foreach (HidHandle handle in _handles)
        {
            handle.Dispose();
        }

        _handles.Clear();
    }

    private HidHandle? FindHandle(int reportLength) =>
        _handles.FirstOrDefault(h => h.Info.OutputReportLength == reportLength);
}
