using BatteryBar.Core;
using BatteryBar.Core.Flydigi;

namespace BatteryBar.Tests;

public class FlydigiProtocolTests
{
    // Heartbeat answer of a VADER 5 Pro on its 2.4G receiver (report id stripped), battery 0x05, MAC zeroed.
    private const string RealAnswer = "5AA50101008202000000000545010071540467351700000000000010261F0037";

    /// <summary>The real answer as Windows delivers it (report id 0 first), with the battery byte replaced.</summary>
    private static byte[] Answer(byte battery, byte deviceType = 0x82)
    {
        byte[] data = Convert.FromHexString(RealAnswer);
        data[5] = deviceType;
        data[11] = battery;
        return [0x00, .. data];
    }

    [Fact]
    public void BuildHeartbeat_MatchesDocumentedRequest()
    {
        byte[] request = FlydigiProtocol.BuildHeartbeat(33);

        Assert.Equal(33, request.Length);
        Assert.Equal(Convert.FromHexString("005AA5010203"), request[..6]);
        Assert.All(request[6..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void Parse_RealAnswer_Level5OnBattery()
    {
        FlydigiStatus? status = FlydigiProtocol.Parse(Answer(0x05));

        Assert.NotNull(status);
        Assert.Equal(130, status.DeviceType);
        Assert.Equal(2, status.ConnectType);
        Assert.Equal(5, status.Level);
        Assert.False(status.Charging);
        Assert.Equal(100, status.Percent);
        Assert.Equal(5, status.DisplayLevel);
    }

    [Fact]
    public void Parse_WithoutReportIdByte()
    {
        Assert.Equal(5, FlydigiProtocol.Parse(Convert.FromHexString(RealAnswer))?.Level);
    }

    [Fact]
    public void Parse_Charging()
    {
        FlydigiStatus status = FlydigiProtocol.Parse(Answer(0x13))!;

        Assert.True(status.Charging);
        Assert.Equal(1, status.Status);
        Assert.Equal(60, status.Percent);
        Assert.Equal(3, status.DisplayLevel);
    }

    [Fact]
    public void Parse_Full()
    {
        FlydigiStatus status = FlydigiProtocol.Parse(Answer(0x20))!;

        Assert.True(status.Charging);
        Assert.Equal(100, status.Percent);
        Assert.Equal(5, status.DisplayLevel);
    }

    [Fact]
    public void Parse_LevelZero_IsUnknown()
    {
        FlydigiStatus status = FlydigiProtocol.Parse(Answer(0x00))!;

        Assert.Equal(0, status.Level);
        Assert.Null(status.Percent);
        Assert.Null(status.DisplayLevel);
    }

    [Fact]
    public void Parse_OtherModel_IsUnknown()
    {
        FlydigiStatus status = FlydigiProtocol.Parse(Answer(0x05, deviceType: 0x50))!;

        Assert.False(status.IsSupportedDevice);
        Assert.Null(status.Percent);
    }

    [Theory]
    [InlineData("5BA50101008202000000000545010071540467351700000000000010261F0037")] // bad magic
    [InlineData("5AA60101008202000000000545010071540467351700000000000010261F0037")] // bad magic
    [InlineData("5AA50201008202000000000545010071540467351700000000000010261F0037")] // other command
    [InlineData("5AA50102018202000000000545010071540467351700000000000010261F0037")] // second packet
    [InlineData("5AA5010100820200")]                                                 // too short
    public void Parse_Rejects(string hex)
    {
        Assert.Null(FlydigiProtocol.Parse(Convert.FromHexString(hex)));
    }

    [Theory]
    [InlineData(0x37D7, 0x2401, 0xFFA0, true)]
    [InlineData(0x37D7, 0x2401, 0xFFEE, false)]
    [InlineData(0x37D7, 0x2801, 0xFFA0, false)]
    [InlineData(0x046D, 0x2401, 0xFFA0, false)]
    public void CommandInterfaceSelection(int vid, int pid, int usagePage, bool expected)
    {
        Assert.Equal(expected, FlydigiProtocol.IsCommandInterface((ushort)vid, (ushort)pid, (ushort)usagePage));
    }
}

public class FlydigiSpacingTests
{
    private static readonly TimeSpan Spacing = TimeSpan.FromMilliseconds(1500);

    [Fact]
    public void NothingSentYet_NoWait()
    {
        Assert.Equal(TimeSpan.Zero, BatteryBar.Core.Providers.FlydigiProvider.SpacingDelay(null, Spacing));
    }

    [Fact]
    public void RecentSend_WaitsForTheRest()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(900), BatteryBar.Core.Providers.FlydigiProvider.SpacingDelay(TimeSpan.FromMilliseconds(600), Spacing));
    }

    [Theory]
    [InlineData(1500)]
    [InlineData(1501)]
    [InlineData(60000)]
    public void OldSend_NeverNegative(int elapsedMs)
    {
        Assert.Equal(TimeSpan.Zero, BatteryBar.Core.Providers.FlydigiProvider.SpacingDelay(TimeSpan.FromMilliseconds(elapsedMs), Spacing));
    }
}

public class BatteryMonitorTests
{
    private sealed class CountingProvider : IBatteryProvider
    {
        public int Polls;
        public bool Disposed;

        public string Name => "test";

        public IReadOnlyList<DeviceBattery> Poll(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Polls);
            return [];
        }

        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void Dispose_StopsLoopThenDisposesProviders_AndRefreshAfterwardIsSafe()
    {
        var provider = new CountingProvider();
        var monitor = new BatteryMonitor([provider], TimeSpan.FromHours(1));
        using var polled = new ManualResetEventSlim();
        monitor.Updated += _ => polled.Set();
        monitor.Start();
        Assert.True(polled.Wait(TimeSpan.FromSeconds(5)));

        monitor.Dispose();
        monitor.RefreshNow();
        monitor.Dispose();

        Assert.True(provider.Disposed);
        Assert.Equal(1, provider.Polls);
    }
}

public class CoarseLevelDeviceTests
{
    private static DeviceBattery Pad(int? percent, bool charging = false) =>
        new("pad", "Flydigi VADER 5 Pro", DeviceKind.Gamepad, percent, charging, true) { AlertAtOrBelow = 20, Detail = "level" };

    [Fact]
    public void LowBattery_AlertsAtLevelOne_Once()
    {
        var tracker = new LowBatteryTracker();

        Assert.Empty(tracker.Update([Pad(40)]));
        Assert.Single(tracker.Update([Pad(20)]));
        Assert.Empty(tracker.Update([Pad(20)]));
    }

    [Fact]
    public void LowBattery_RearmsAtLevelTwo()
    {
        var tracker = new LowBatteryTracker();
        Assert.Single(tracker.Update([Pad(20)]));

        Assert.Empty(tracker.Update([Pad(40)]));
        Assert.Single(tracker.Update([Pad(20)]));
    }

    [Fact]
    public void Slot_LevelOneIsShownLow()
    {
        Assert.True(new WidgetSlot(DeviceKind.Gamepad, Pad(20)).IsLow);
        Assert.False(new WidgetSlot(DeviceKind.Gamepad, Pad(40)).IsLow);
    }
}
