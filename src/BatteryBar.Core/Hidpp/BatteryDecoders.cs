namespace BatteryBar.Core.Hidpp;

public static class HidppFeature
{
    public const ushort DeviceName = 0x0005;
    public const ushort BatteryStatus = 0x1000;
    public const ushort BatteryVoltage = 0x1001;
    public const ushort UnifiedBattery = 0x1004;
    public const ushort AdcMeasurement = 0x1F20;
}

public readonly record struct BatteryReading(int? Percent, bool Charging, int? Millivolts = null);

/// <summary>Decoders for the battery features. Input is the response parameters (byte 4 onward).</summary>
public static class BatteryDecoders
{
    /// <summary>
    /// Generic Li-ion curve for Logitech devices (the same points as HeadsetControl's default Logitech estimate),
    /// mV high to low. Used when no G HUB discharge model is installed for the device (see <see cref="GHubPowerModel"/>).
    /// </summary>
    public static readonly (int Millivolts, int Percent)[] DefaultCurve =
        [(4100, 100), (3850, 50), (3780, 30), (3730, 20), (3650, 5), (3300, 0)];

    /// <summary>
    /// 0x1004 UNIFIED_BATTERY getStatus (fn 1): [stateOfCharge %, level flags, chargingStatus, externalPower].
    /// <paramref name="supportsPercentage"/> comes from getCapabilities; null when that query failed.
    /// The percentage is used only when it is plausible (1-100) and not ruled out by the capabilities;
    /// otherwise the level flags give a rough value, and with no flags the level is unknown (null), never 0.
    /// </summary>
    public static BatteryReading UnifiedBattery(ReadOnlySpan<byte> status, bool? supportsPercentage)
    {
        int? percent = supportsPercentage != false && status[0] is > 0 and <= 100
            ? status[0]
            : LevelFlagsToPercent(status[1]);
        byte charging = status[2];
        // 1 = charging, 2 = slow charging, 3 = charge complete (still on power).
        return new BatteryReading(percent, charging is 1 or 2 or 3);
    }

    /// <summary>0x1004 getCapabilities (fn 0): [supported levels, flags]; flag bit 1 = state of charge in percent.</summary>
    public static bool UnifiedSupportsPercentage(ReadOnlySpan<byte> capabilities) => (capabilities[1] & 0x02) != 0;

    /// <summary>0x1000 BATTERY_STATUS getBatteryLevelStatus (fn 0): [level %, next level %, status].</summary>
    public static BatteryReading BatteryStatus(ReadOnlySpan<byte> response)
    {
        int level = response[0];
        byte status = response[2];
        // 1 recharging, 2 almost full, 3 full (on power), 4 slow recharge.
        bool charging = status is >= 1 and <= 4;
        return new BatteryReading(level is > 0 and <= 100 ? level : status == 3 ? 100 : null, charging);
    }

    /// <summary>0x1001 BATTERY_VOLTAGE getBatteryInfo (fn 0): [mV hi, mV lo, flags]; flag bit 7 = external power.</summary>
    public static BatteryReading BatteryVoltage(ReadOnlySpan<byte> response, (int Millivolts, int Percent)[] curve)
    {
        int millivolts = (response[0] << 8) | response[1];
        bool charging = (response[2] & 0x80) != 0;
        return new BatteryReading(VoltageToPercent(millivolts, curve), charging, millivolts);
    }

    /// <summary>
    /// 0x1F20 ADC_MEASUREMENT (fn 0), used by the PRO X / G PRO headsets: [mV hi, mV lo, flags].
    /// Flags 0x01 = running on battery, 0x03 = charging (HeadsetControl).
    /// </summary>
    public static BatteryReading AdcMeasurement(ReadOnlySpan<byte> response, (int Millivolts, int Percent)[] curve)
    {
        int millivolts = (response[0] << 8) | response[1];
        byte flags = response[2];
        bool charging = (flags & 0x03) == 0x03;
        return new BatteryReading(VoltageToPercent(millivolts, curve), charging, millivolts);
    }

    /// <summary>Linear interpolation on a curve sorted from high to low voltage; null for an implausible reading.</summary>
    public static int? VoltageToPercent(int millivolts, (int Millivolts, int Percent)[] curve)
    {
        if (millivolts <= 0)
        {
            return null;
        }

        if (millivolts >= curve[0].Millivolts)
        {
            return curve[0].Percent;
        }

        for (int i = 1; i < curve.Length; i++)
        {
            (int hiMv, int hiPct) = curve[i - 1];
            (int loMv, int loPct) = curve[i];
            if (millivolts >= loMv)
            {
                double t = (double)(millivolts - loMv) / (hiMv - loMv);
                return (int)Math.Round(loPct + t * (hiPct - loPct));
            }
        }

        return curve[^1].Percent;
    }

    private static int? LevelFlagsToPercent(byte flags) =>
        (flags & 0x08) != 0 ? 100
        : (flags & 0x04) != 0 ? 50
        : (flags & 0x02) != 0 ? 20
        : (flags & 0x01) != 0 ? 5
        : null;
}
