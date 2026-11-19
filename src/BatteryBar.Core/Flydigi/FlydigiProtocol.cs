namespace BatteryBar.Core.Flydigi;

/// <param name="DeviceType">130 = F5 (VADER 5 Pro); 0 when the receiver has no controller.</param>
/// <param name="Level">Battery level 0-5 (low nibble); 0 = unknown.</param>
/// <param name="Status">High nibble: 0 on battery, 1 charging, 2 full (charge done).</param>
public sealed record FlydigiStatus(int DeviceType, int ConnectType, byte BatteryRaw, int Level, int Status)
{
    public bool IsSupportedDevice => DeviceType == FlydigiProtocol.DeviceTypeVader5Pro;

    public bool Charging => Status is 1 or 2;

    /// <summary>Level 1-5 as 20-100 %, full = 100 %; null when the level is unknown or the device is not a VADER 5 Pro.</summary>
    public int? Percent => !IsSupportedDevice ? null
        : Status == 2 ? 100
        : Level is >= 1 and <= 5 ? Level * 20
        : null;

    /// <summary>Level for display ("level 5/5"); a full battery counts as 5 even if the nibble is 0.</summary>
    public int? DisplayLevel => !IsSupportedDevice ? null
        : Status == 2 ? 5
        : Level is >= 1 and <= 5 ? Level
        : null;
}

/// <summary>
/// Flydigi receiver protocol, heartbeat only (see docs/flydigi-protocol.md).
/// The heartbeat is a read-only status query answered with device info and battery; nothing else is ever sent.
/// </summary>
public static class FlydigiProtocol
{
    public const ushort VendorId = 0x37D7;
    public const ushort VendorUsagePage = 0xFFA0;
    public const int DeviceTypeVader5Pro = 130;

    private const byte Magic0 = 0x5A;
    private const byte Magic1 = 0xA5;
    private const byte CmdHeartbeat = 0x01;

    /// <summary>Command interface: usage page 0xFFA0, PID high nibble 2, PID &amp; 0xFF00 != 0x2800.</summary>
    public static bool IsCommandInterface(ushort vendorId, ushort productId, ushort usagePage) =>
        vendorId == VendorId && usagePage == VendorUsagePage && (productId >> 12) == 2 && (productId & 0xFF00) != 0x2800;

    /// <summary>
    /// "00 5A A5 01 02 03 00..." padded to the output report length: report id 0 (the interface has none),
    /// magic, command 0x01, payload length 2, checksum = 0x01 + 0x02.
    /// </summary>
    public static byte[] BuildHeartbeat(int outputReportLength)
    {
        var report = new byte[Math.Max(outputReportLength, 6)];
        report[1] = Magic0;
        report[2] = Magic1;
        report[3] = CmdHeartbeat;
        report[4] = 0x02;
        report[5] = (byte)(report[3] + report[4]);
        return report;
    }

    /// <summary>
    /// Decodes a heartbeat answer as read from Windows (report id byte 0 first). Returns null for anything else:
    /// other commands, bad magic, later packets of a multi-packet answer, short reports.
    /// </summary>
    public static FlydigiStatus? Parse(ReadOnlySpan<byte> report)
    {
        // Windows prefixes the report id; this interface has none, so the first byte is 0.
        ReadOnlySpan<byte> d = report.Length > 0 && report[0] == 0x00 ? report[1..] : report;
        if (d.Length < 19 || d[0] != Magic0 || d[1] != Magic1 || d[2] != CmdHeartbeat)
        {
            return null;
        }

        // d[3] = packet count, d[4] = packet index; only the first packet carries the status block.
        bool multiPacket = d[4] < d[3];
        if (multiPacket && d[4] != 0)
        {
            return null;
        }

        int i = multiPacket ? 5 : 4;
        int deviceType = d[i];
        int connectType = d[i + 1];
        byte battery = d[i + 6]; // after device type, connect type and the 4-byte MAC
        return new FlydigiStatus(deviceType, connectType, battery, battery & 0x0F, battery >> 4);
    }
}
