namespace BatteryBar.Core;

public enum DeviceKind
{
    Mouse,
    Headset,
    Gamepad,
    Other,
}

/// <summary>One battery-powered device as seen by a provider at a single poll.</summary>
/// <param name="Id">Stable key used to remember the device between polls (e.g. for low-battery alerts).</param>
/// <param name="Percent">0-100, or null when the device does not report a level (asleep, unknown).</param>
public sealed record DeviceBattery(
    string Id,
    string Name,
    DeviceKind Kind,
    int? Percent,
    bool Charging,
    bool Connected)
{
    /// <summary>Extra tooltip text, e.g. "seviye 5/5" for devices that only report coarse levels.</summary>
    public string? Detail { get; init; }

    /// <summary>
    /// Per-device low-battery threshold (warn when Percent &lt;= this). Null uses the default (below 15 %).
    /// Coarse devices need it: a 5-level gamepad jumps from 40 % to 20 %.
    /// </summary>
    public int? AlertAtOrBelow { get; init; }

    public static DeviceBattery Disconnected(string id, string name, DeviceKind kind) =>
        new(id, name, kind, null, false, false);
}

/// <summary>A source of battery readings. Implementations must not throw for an absent or sleeping device.</summary>
public interface IBatteryProvider : IDisposable
{
    string Name { get; }

    /// <summary>Reads all devices this provider knows about. Called from a background thread.</summary>
    IReadOnlyList<DeviceBattery> Poll(CancellationToken cancellationToken);
}
