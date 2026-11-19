using System.Diagnostics;
using BatteryBar.Core.Flydigi;
using BatteryBar.Core.Hid;

namespace BatteryBar.Core.Providers;

/// <summary>
/// Flydigi VADER 5 Pro on its 2.4G receiver (VID 0x37D7 / PID 0x2401). XInput reports the pad as "wired",
/// so the battery comes from the receiver's vendor HID interface (MI_01, usage page 0xFFA0) with the
/// read-only heartbeat (status) query (docs/flydigi-protocol.md). Flydigi's own software is not needed and
/// is not talked to; the interface is opened shared, so that software keeps working alongside this app.
/// </summary>
public sealed class FlydigiProvider : IBatteryProvider
{
    private const int TimeoutMs = 600;
    private const int Attempts = 3;

    /// <summary>The receiver ignores a request that follows the previous one within ~1 s.</summary>
    private static readonly TimeSpan MinSendSpacing = TimeSpan.FromMilliseconds(1500);

    private readonly Stopwatch _sinceLastSend = new();

    public string Name => "Flydigi";

    public IReadOnlyList<DeviceBattery> Poll(CancellationToken cancellationToken)
    {
        HidCollectionInfo? target;
        try
        {
            target = HidEnumerator.Enumerate(FlydigiProtocol.VendorId)
                .FirstOrDefault(c => FlydigiProtocol.IsCommandInterface(c.VendorId, c.ProductId, c.UsagePage));
        }
        catch (Exception ex)
        {
            Log.Error("Flydigi enumeration failed", ex);
            return [];
        }

        if (target is null)
        {
            // Receiver not plugged in: nothing to show.
            return [];
        }

        string id = "flydigi#" + target.GroupKey;
        const string defaultName = "Flydigi VADER 5 Pro";
        FlydigiStatus? status;
        try
        {
            status = Query(target, cancellationToken);
        }
        catch (Exception ex)
        {
            Log.Error("Flydigi query failed", ex);
            status = null;
        }

        if (status is null || status.DeviceType == 0)
        {
            // No answer (pad off, asleep or out of range) or the receiver reports no controller.
            Log.Debug($"Flydigi: {(status is null ? "no answer" : "device type 0")}");
            return [DeviceBattery.Disconnected(id, defaultName, DeviceKind.Gamepad)];
        }

        Log.Debug($"Flydigi: type {status.DeviceType} connect {status.ConnectType} battery 0x{status.BatteryRaw:X2}");
        if (!status.IsSupportedDevice)
        {
            // Another Flydigi model may encode the battery differently: show "unknown" rather than a wrong value.
            return [new DeviceBattery(id, $"Flydigi (model {status.DeviceType})", DeviceKind.Gamepad, null, false, true)
            {
                Detail = Strings.T("UnsupportedModel"),
            }];
        }

        return [new DeviceBattery(id, defaultName, DeviceKind.Gamepad, status.Percent, status.Charging, true)
        {
            Detail = status.DisplayLevel is int level ? Strings.F("Level", level) : null,
            // Level 1 of 5 (20 %) is the last step before empty.
            AlertAtOrBelow = 20,
        }];
    }

    public void Dispose()
    {
    }

    /// <summary>How long to wait before the next send; never negative. Null = nothing sent yet.</summary>
    internal static TimeSpan SpacingDelay(TimeSpan? sinceLastSend, TimeSpan minSpacing)
    {
        if (sinceLastSend is not TimeSpan elapsed)
        {
            return TimeSpan.Zero;
        }

        TimeSpan wait = minSpacing - elapsed;
        return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
    }

    /// <summary>Sends the heartbeat (up to 3 times, spaced at least 1.5 s apart) and returns the first decoded answer.</summary>
    private FlydigiStatus? Query(HidCollectionInfo target, CancellationToken cancellationToken)
    {
        using HidHandle? handle = HidHandle.Open(target);
        if (handle is null)
        {
            return null;
        }

        byte[] heartbeat = FlydigiProtocol.BuildHeartbeat(target.OutputReportLength);
        for (int attempt = 0; attempt < Attempts && !cancellationToken.IsCancellationRequested; attempt++)
        {
            TimeSpan wait = SpacingDelay(_sinceLastSend.IsRunning ? _sinceLastSend.Elapsed : null, MinSendSpacing);
            if (wait > TimeSpan.Zero && cancellationToken.WaitHandle.WaitOne(wait))
            {
                return null;
            }

            // Queue the read first so a ~25 ms answer cannot be missed.
            if (!handle.StartRead())
            {
                return null;
            }

            _sinceLastSend.Restart();
            if (!handle.Write(heartbeat, TimeoutMs))
            {
                Log.Debug("Flydigi: heartbeat write failed");
                continue;
            }

            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < TimeoutMs)
            {
                int remaining = TimeoutMs - (int)clock.ElapsedMilliseconds;
                if (remaining <= 0 || !handle.ReadEvent.WaitOne(remaining))
                {
                    break;
                }

                byte[]? report = handle.CompleteRead();
                // Other traffic on the interface (e.g. answers to Flydigi's own software) is skipped.
                if (report is not null && FlydigiProtocol.Parse(report) is FlydigiStatus status)
                {
                    Log.Debug($"Flydigi <- {Convert.ToHexString(report)}");
                    return status;
                }

                if (!handle.StartRead())
                {
                    return null;
                }
            }
        }

        return null;
    }
}
