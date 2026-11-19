namespace BatteryBar.Core.Hidpp;

public enum HidppStatus
{
    Ok,
    /// <summary>HID++ 2.0 error report (feature index 0xFF).</summary>
    Hidpp20Error,
    /// <summary>HID++ 1.0 error report (sub id 0x8F), typically sent by a receiver for an absent or sleeping device.</summary>
    Hidpp10Error,
    Timeout,
    IoError,
}

/// <summary>Result of one HID++ request. <see cref="Params"/> starts right after the function/software-id byte.</summary>
public sealed record HidppResponse(HidppStatus Status, byte ErrorCode, byte[] Params)
{
    public bool IsOk => Status == HidppStatus.Ok;

    public static HidppResponse Timeout { get; } = new(HidppStatus.Timeout, 0, []);

    public static HidppResponse IoError { get; } = new(HidppStatus.IoError, 0, []);
}

/// <summary>Pure HID++ framing helpers (no I/O), kept separate so they can be unit tested.</summary>
public static class HidppMessages
{
    public const byte ReportShort = 0x10;
    public const byte ReportLong = 0x11;
    public const byte ReportVeryLong = 0x12;

    public const int ShortLength = 7;
    public const int LongLength = 20;
    public const int VeryLongLength = 64;

    /// <summary>Device index of the receiver itself, or of a device connected directly (USB/its own dongle).</summary>
    public const byte DirectIndex = 0xFF;

    public const byte Hidpp10ErrorSubId = 0x8F;
    public const byte Hidpp20ErrorFeature = 0xFF;

    public static int LengthOf(byte reportId) => reportId switch
    {
        ReportShort => ShortLength,
        ReportLong => LongLength,
        ReportVeryLong => VeryLongLength,
        _ => 0,
    };

    public static byte[] Build(byte reportId, byte deviceIndex, byte featureIndex, byte function, byte softwareId, ReadOnlySpan<byte> parameters)
    {
        int length = LengthOf(reportId);
        if (length == 0 || parameters.Length > length - 4)
        {
            throw new ArgumentException("Parameters do not fit the report.", nameof(parameters));
        }

        var report = new byte[length];
        report[0] = reportId;
        report[1] = deviceIndex;
        report[2] = featureIndex;
        report[3] = (byte)((function << 4) | (softwareId & 0x0F));
        parameters.CopyTo(report.AsSpan(4));
        return report;
    }

    /// <summary>
    /// Returns the response to the request (device, feature, function|swid), or null when the report
    /// belongs to someone else (another application, a notification).
    /// </summary>
    public static HidppResponse? Match(ReadOnlySpan<byte> report, byte deviceIndex, byte featureIndex, byte functionAndSwId)
    {
        if (report.Length < ShortLength || report[1] != deviceIndex)
        {
            return null;
        }

        byte reportId = report[0];
        if (reportId != ReportShort && reportId != ReportLong && reportId != ReportVeryLong)
        {
            return null;
        }

        if (report[2] == featureIndex && report[3] == functionAndSwId)
        {
            return new HidppResponse(HidppStatus.Ok, 0, report[4..].ToArray());
        }

        if (report[2] == Hidpp20ErrorFeature && report[3] == featureIndex && report[4] == functionAndSwId)
        {
            return new HidppResponse(HidppStatus.Hidpp20Error, report[5], []);
        }

        if (report[2] == Hidpp10ErrorSubId && report[3] == featureIndex && report[4] == functionAndSwId)
        {
            return new HidppResponse(HidppStatus.Hidpp10Error, report[5], []);
        }

        return null;
    }
}
