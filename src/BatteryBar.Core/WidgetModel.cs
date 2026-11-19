namespace BatteryBar.Core;

/// <summary>One icon + value cell of the widget. <see cref="Device"/> is null when nothing of that kind exists.</summary>
public sealed record WidgetSlot(DeviceKind Kind, DeviceBattery? Device)
{
    public const int LowPercent = 20;

    /// <summary>"97%", or "--" when unknown / not connected.</summary>
    public string Text => Device is { Connected: true, Percent: int p } ? $"{p}%" : "--";

    public bool IsLow => Device is { Connected: true, Percent: int p, Charging: false } d
        && (p < LowPercent || (d.AlertAtOrBelow is int threshold && p <= threshold));

    public bool IsCharging => Device is { Connected: true, Charging: true };

    public bool IsDimmed => Device is not { Connected: true };
}

public static class WidgetModel
{
    /// <summary>Kinds that always get a cell, in display order.</summary>
    public static readonly DeviceKind[] FixedKinds = [DeviceKind.Mouse, DeviceKind.Headset, DeviceKind.Gamepad];

    /// <summary>
    /// Mouse, headset and gamepad always get one cell (the best device of that kind: connected first,
    /// then lowest battery); devices of other kinds are appended.
    /// </summary>
    public static IReadOnlyList<WidgetSlot> BuildSlots(IEnumerable<DeviceBattery> devices)
    {
        var list = devices.ToList();
        var slots = new List<WidgetSlot>();
        foreach (DeviceKind kind in FixedKinds)
        {
            DeviceBattery? best = list.Where(d => d.Kind == kind)
                .OrderByDescending(d => d.Connected)
                .ThenBy(d => d.Percent ?? 101)
                .FirstOrDefault();
            slots.Add(new WidgetSlot(kind, best));
        }

        slots.AddRange(list.Where(d => d.Kind == DeviceKind.Other).Select(d => new WidgetSlot(DeviceKind.Other, d)));
        return slots;
    }

    /// <summary>Tooltip text: one line per device with its full name.</summary>
    public static string BuildTooltip(IEnumerable<DeviceBattery> devices, DateTime? lastPoll)
    {
        var lines = devices
            .OrderBy(d => Array.IndexOf(FixedKinds, d.Kind) is int i && i >= 0 ? i : FixedKinds.Length)
            .ThenBy(d => d.Name)
            .Select(Describe)
            .ToList();
        if (lines.Count == 0)
        {
            lines.Add(Strings.T("NoDevices"));
        }

        if (lastPoll is DateTime time)
        {
            lines.Add(Strings.F("LastPoll", time));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string Describe(DeviceBattery d)
    {
        if (!d.Connected)
        {
            return $"{d.Name}: {Strings.T("NotConnected")}";
        }

        string value = d.Percent is int p ? Strings.Percent(p) : Strings.T("Unknown");
        var notes = new List<string>();
        if (d.Detail is not null)
        {
            notes.Add(d.Detail);
        }

        if (d.Charging)
        {
            notes.Add(Strings.T("Charging"));
        }

        return notes.Count == 0 ? $"{d.Name}: {value}" : $"{d.Name}: {value} ({string.Join(", ", notes)})";
    }
}

/// <summary>Decides when to raise the low-battery notification: once per device per discharge.</summary>
public sealed class LowBatteryTracker
{
    public const int AlertPercent = 15;

    /// <summary>The device is re-armed once it is charging or back above this level.</summary>
    public const int RearmPercent = 20;

    private readonly HashSet<string> _alerted = new();

    /// <summary>Returns the devices that should be announced now.</summary>
    public IReadOnlyList<DeviceBattery> Update(IEnumerable<DeviceBattery> devices)
    {
        var due = new List<DeviceBattery>();
        foreach (DeviceBattery d in devices)
        {
            if (!d.Connected || d.Percent is not int percent)
            {
                continue;
            }

            int threshold = d.AlertAtOrBelow ?? AlertPercent - 1;
            if (d.Charging || percent >= Math.Max(RearmPercent, threshold + 1))
            {
                _alerted.Remove(d.Id);
            }
            else if (percent <= threshold && _alerted.Add(d.Id))
            {
                due.Add(d);
            }
        }

        return due;
    }
}
