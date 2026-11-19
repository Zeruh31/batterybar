using System.Text;
using BatteryBar.Core.Hid;
using BatteryBar.Core.Hidpp;

namespace BatteryBar.Core.Providers;

/// <summary>
/// Reads Logitech devices over HID++ 2.0, directly (no G HUB). Receivers (LIGHTSPEED/Unifying) are
/// walked slot by slot; devices with their own dongle (e.g. PRO X Wireless headset) answer on index 0xFF.
/// Only read-only queries are sent: ping, feature lookup, device name/type and battery status.
/// </summary>
public sealed class LogitechHidppProvider : IBatteryProvider
{
    public const ushort VendorId = 0x046D;

    private const int RequestTimeoutMs = 1500;
    private const byte PingData = 0x5A;

    private readonly Dictionary<string, KnownDevice> _known = new();

    public string Name => "Logitech HID++";

    public IReadOnlyList<DeviceBattery> Poll(CancellationToken cancellationToken)
    {
        var results = new List<DeviceBattery>();
        var seen = new HashSet<string>();
        IReadOnlyList<HidCollectionInfo> collections;
        try
        {
            collections = HidEnumerator.Enumerate(VendorId);
        }
        catch (Exception ex)
        {
            Log.Error("HID enumeration failed", ex);
            return KnownAsDisconnected(seen);
        }

        foreach (IGrouping<string, HidCollectionInfo> group in collections.Where(HidppChannel.IsHidppCollection).GroupBy(c => c.GroupKey))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                PollInterface(group.Key, group.ToList(), results, seen, cancellationToken);
            }
            catch (Exception ex)
            {
                Log.Error($"HID++ poll failed for {group.Key}", ex);
            }
        }

        results.AddRange(KnownAsDisconnected(seen));
        return results;
    }

    public void Dispose()
    {
    }

    private void PollInterface(string groupKey, List<HidCollectionInfo> group, List<DeviceBattery> results, HashSet<string> seen, CancellationToken cancellationToken)
    {
        using HidppChannel? channel = HidppChannel.Open(group);
        if (channel is null)
        {
            return;
        }

        string product = group.Select(c => c.Product).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)) ?? "Logitech";
        ushort usbPid = group[0].ProductId;
        HidppResponse directPing = Ping(channel, HidppMessages.DirectIndex);
        if (directPing.IsOk)
        {
            // A device on its own USB link or dongle (headset, wired mouse).
            AddIfPresent(ReadDevice(channel, HidppMessages.DirectIndex, groupKey + "#ff", product, usbPid, directPing), results, seen);
            return;
        }

        // A receiver: the paired devices sit on indexes 1..6.
        for (byte index = 1; index <= 6 && !cancellationToken.IsCancellationRequested; index++)
        {
            string id = $"{groupKey}#{index}";
            AddIfPresent(ReadDevice(channel, index, id, product, null, Ping(channel, index)), results, seen);
        }
    }

    private static void AddIfPresent(DeviceBattery? device, List<DeviceBattery> results, HashSet<string> seen)
    {
        if (device is not null)
        {
            results.Add(device);
            seen.Add(device.Id);
        }
    }

    private static HidppResponse Ping(HidppChannel channel, byte index) =>
        channel.Request(index, 0x00, 0x1, [0, 0, PingData], RequestTimeoutMs);

    /// <summary>Returns null when nothing is paired on that slot.</summary>
    /// <param name="usbPid">Product id of a directly connected device; null for a receiver slot (its wireless id is read from the receiver).</param>
    private DeviceBattery? ReadDevice(HidppChannel channel, byte index, string id, string product, ushort? usbPid, HidppResponse ping)
    {
        _known.TryGetValue(id, out KnownDevice? known);
        if (!ping.IsOk)
        {
            // Asleep, switched off, out of range or nothing paired. A LIGHTSPEED receiver answers 0x08
            // (unknown device) in both the empty-slot and the sleeping case, so its pairing register decides.
            Log.Debug($"{id}: ping {ping.Status} 0x{ping.ErrorCode:X2}");
            if (known is null && index != HidppMessages.DirectIndex && ReadPairing(channel, index) is KnownDevice paired)
            {
                known = paired;
                _known[id] = known;
                Log.Info($"found sleeping {known.Kind} '{known.Name}' at {id}");
            }

            return known is null ? null : DeviceBattery.Disconnected(id, known.Name, known.Kind);
        }

        var features = new FeatureMap(channel, index);
        if (known is null)
        {
            (string name, DeviceKind kind) = ReadIdentity(channel, index, features, product);
            ushort? modelPid = usbPid ?? ReadPairing(channel, index)?.ModelPid;
            known = new KnownDevice(name, kind, modelPid);
            _known[id] = known;
            Log.Info($"found {kind} '{name}' at {id} (HID++ {ping.Params[0]}.{ping.Params[1]}, pid {modelPid:X4})");
        }

        BatteryReading? battery = ReadBattery(channel, index, features, known);
        if (battery is not BatteryReading reading)
        {
            // Headset dongles answer even when the headset itself is off; the battery query then fails.
            return DeviceBattery.Disconnected(id, known.Name, known.Kind);
        }

        Log.Debug($"{id}: {known.Name} {reading}");
        return new DeviceBattery(id, known.Name, known.Kind, reading.Percent, reading.Charging, true);
    }

    private static BatteryReading? ReadBattery(HidppChannel channel, byte index, FeatureMap features, KnownDevice device)
    {
        if (features.Get(HidppFeature.UnifiedBattery) is byte unified)
        {
            HidppResponse caps = channel.Request(index, unified, 0x0, [], RequestTimeoutMs);
            HidppResponse status = channel.Request(index, unified, 0x1, [], RequestTimeoutMs);
            if (status.IsOk)
            {
                bool? percentage = caps.IsOk ? BatteryDecoders.UnifiedSupportsPercentage(caps.Params) : null;
                return BatteryDecoders.UnifiedBattery(status.Params, percentage);
            }
        }

        if (features.Get(HidppFeature.BatteryStatus) is byte batteryStatus)
        {
            HidppResponse status = channel.Request(index, batteryStatus, 0x0, [], RequestTimeoutMs);
            if (status.IsOk)
            {
                return BatteryDecoders.BatteryStatus(status.Params);
            }
        }

        if (features.Get(HidppFeature.BatteryVoltage) is byte voltage)
        {
            HidppResponse status = channel.Request(index, voltage, 0x0, [], RequestTimeoutMs);
            if (status.IsOk)
            {
                return BatteryDecoders.BatteryVoltage(status.Params, CurveFor(device));
            }
        }

        if (features.Get(HidppFeature.AdcMeasurement) is byte adc)
        {
            HidppResponse status = channel.Request(index, adc, 0x0, [], RequestTimeoutMs);
            if (status.IsOk)
            {
                BatteryReading reading = BatteryDecoders.AdcMeasurement(status.Params, CurveFor(device));
                Log.Debug($"ADC {reading.Millivolts} mV flags 0x{status.Params[2]:X2}");
                // A switched-off headset makes its dongle answer with an error or 0 mV (Percent null).
                return reading.Percent is null ? null : reading;
            }
        }

        return null;
    }

    private static (int Millivolts, int Percent)[] CurveFor(KnownDevice device) =>
        (device.ModelPid is ushort pid ? GHubPowerModel.TryGetCurve(pid) : null)
        ?? BatteryDecoders.DefaultCurve;

    /// <summary>
    /// HID++ 1.0 receiver register 0x2B5 (read only): pairing info (0x20+slot) and name (0x40+slot).
    /// Works while the device sleeps, so a mouse that is off at start-up still gets its slot.
    /// </summary>
    private static KnownDevice? ReadPairing(HidppChannel channel, byte index)
    {
        byte slot = (byte)(index - 1);
        HidppResponse pairing = channel.RawRequest(HidppMessages.ReportShort, HidppMessages.DirectIndex, 0x83, 0xB5, [(byte)(0x20 + slot)], RequestTimeoutMs);
        if (!pairing.IsOk || pairing.Params.Length < 8)
        {
            return null;
        }

        ushort wpid = (ushort)((pairing.Params[3] << 8) | pairing.Params[4]);
        DeviceKind kind = KindFromReceiverType((byte)(pairing.Params[7] & 0x0F));
        string name = $"Logitech {wpid:X4}";
        HidppResponse nameReply = channel.RawRequest(HidppMessages.ReportShort, HidppMessages.DirectIndex, 0x83, 0xB5, [(byte)(0x40 + slot)], RequestTimeoutMs);
        if (nameReply.IsOk && nameReply.Params.Length > 2)
        {
            int length = Math.Min(nameReply.Params[1], nameReply.Params.Length - 2);
            string parsed = Encoding.UTF8.GetString(nameReply.Params, 2, length).TrimEnd('\0').Trim();
            if (parsed.Length > 0)
            {
                name = parsed;
            }
        }

        return new KnownDevice(name, kind == DeviceKind.Other ? GuessKind(name) : kind, wpid);
    }

    /// <summary>Device type nibble of the receiver pairing register.</summary>
    internal static DeviceKind KindFromReceiverType(byte type) => type switch
    {
        2 or 8 or 9 => DeviceKind.Mouse, // mouse, trackball, touchpad
        _ => DeviceKind.Other,
    };

    private static (string Name, DeviceKind Kind) ReadIdentity(HidppChannel channel, byte index, FeatureMap features, string product)
    {
        string? name = null;
        DeviceKind? kind = null;
        if (features.Get(HidppFeature.DeviceName) is byte nameFeature)
        {
            HidppResponse count = channel.Request(index, nameFeature, 0x0, [], RequestTimeoutMs);
            if (count.IsOk && count.Params[0] > 0)
            {
                var builder = new StringBuilder();
                int length = count.Params[0];
                while (builder.Length < length)
                {
                    HidppResponse part = channel.Request(index, nameFeature, 0x1, [(byte)builder.Length], RequestTimeoutMs);
                    if (!part.IsOk)
                    {
                        break;
                    }

                    int take = Math.Min(length - builder.Length, part.Params.Length);
                    string chunk = Encoding.UTF8.GetString(part.Params, 0, take).TrimEnd('\0');
                    if (chunk.Length == 0)
                    {
                        break;
                    }

                    builder.Append(chunk);
                }

                name = builder.ToString().Trim();
            }

            HidppResponse type = channel.Request(index, nameFeature, 0x2, [], RequestTimeoutMs);
            if (type.IsOk)
            {
                kind = KindFromDeviceType(type.Params[0]);
            }
        }

        name = string.IsNullOrWhiteSpace(name) ? product : name;
        kind ??= GuessKind(name);
        return (name, kind.Value);
    }

    /// <summary>0x0005 getDeviceType values.</summary>
    internal static DeviceKind KindFromDeviceType(byte type) => type switch
    {
        3 or 4 or 5 => DeviceKind.Mouse, // mouse, trackpad, trackball
        8 => DeviceKind.Headset,
        11 or 12 => DeviceKind.Gamepad, // joystick, gamepad
        _ => DeviceKind.Other,
    };

    internal static DeviceKind GuessKind(string name)
    {
        string n = name.ToUpperInvariant();
        if (n.Contains("HEADSET") || n.Contains("PRO X WIRELESS") || n.Contains("ASTRO"))
        {
            return DeviceKind.Headset;
        }

        if (n.Contains("MOUSE") || n.Contains("SUPERLIGHT") || n.Contains("G PRO WIRELESS") || n.Contains("G502") || n.Contains("G305"))
        {
            return DeviceKind.Mouse;
        }

        if (n.Contains("GAMEPAD") || n.Contains("F710") || n.Contains("CONTROLLER"))
        {
            return DeviceKind.Gamepad;
        }

        return DeviceKind.Other;
    }

    private List<DeviceBattery> KnownAsDisconnected(HashSet<string> seen) =>
        _known.Where(k => !seen.Contains(k.Key))
            .Select(k => DeviceBattery.Disconnected(k.Key, k.Value.Name, k.Value.Kind))
            .ToList();

    /// <param name="ModelPid">USB product id (direct device) or wireless product id (receiver slot); selects the voltage curve.</param>
    private sealed record KnownDevice(string Name, DeviceKind Kind, ushort? ModelPid = null);

    /// <summary>Lazily resolves feature ids to indexes through the root feature (0x0000 getFeature).</summary>
    private sealed class FeatureMap(HidppChannel channel, byte index)
    {
        private readonly Dictionary<ushort, byte?> _indexes = new();

        public byte? Get(ushort feature)
        {
            if (_indexes.TryGetValue(feature, out byte? cached))
            {
                return cached;
            }

            HidppResponse response = channel.Request(index, 0x00, 0x0, [(byte)(feature >> 8), (byte)feature], RequestTimeoutMs);
            byte? result = response.IsOk && response.Params[0] != 0 ? response.Params[0] : null;
            _indexes[feature] = result;
            return result;
        }
    }
}
