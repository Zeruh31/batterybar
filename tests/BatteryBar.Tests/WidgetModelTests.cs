using BatteryBar.Core;

namespace BatteryBar.Tests;

public class WidgetModelTests
{
    private static DeviceBattery Device(string id, DeviceKind kind, int? percent, bool connected = true, bool charging = false) =>
        new(id, id, kind, percent, charging, connected);

    [Fact]
    public void BuildSlots_AlwaysHasMouseHeadsetGamepad()
    {
        var slots = WidgetModel.BuildSlots([Device("m", DeviceKind.Mouse, 97), Device("h", DeviceKind.Headset, 74)]);

        Assert.Equal([DeviceKind.Mouse, DeviceKind.Headset, DeviceKind.Gamepad], slots.Select(s => s.Kind));
        Assert.Equal(["97%", "74%", "--"], slots.Select(s => s.Text));
        Assert.True(slots[2].IsDimmed);
    }

    [Fact]
    public void BuildSlots_PrefersConnectedDevice()
    {
        var slots = WidgetModel.BuildSlots([Device("old", DeviceKind.Mouse, null, connected: false), Device("new", DeviceKind.Mouse, 50)]);

        Assert.Equal("new", slots[0].Device!.Id);
    }

    [Fact]
    public void BuildSlots_AppendsOtherKinds()
    {
        var slots = WidgetModel.BuildSlots([Device("k", DeviceKind.Other, 80)]);

        Assert.Equal(4, slots.Count);
        Assert.Equal(DeviceKind.Other, slots[3].Kind);
    }

    [Fact]
    public void Slot_LowAndCharging()
    {
        Assert.True(new WidgetSlot(DeviceKind.Mouse, Device("m", DeviceKind.Mouse, 19)).IsLow);
        Assert.False(new WidgetSlot(DeviceKind.Mouse, Device("m", DeviceKind.Mouse, 20)).IsLow);
        Assert.False(new WidgetSlot(DeviceKind.Mouse, Device("m", DeviceKind.Mouse, 10, charging: true)).IsLow);
        Assert.True(new WidgetSlot(DeviceKind.Mouse, Device("m", DeviceKind.Mouse, 10, charging: true)).IsCharging);
    }

    [Fact]
    public void Slot_DisconnectedShowsDashes()
    {
        var slot = new WidgetSlot(DeviceKind.Headset, Device("h", DeviceKind.Headset, 50, connected: false));

        Assert.Equal("--", slot.Text);
        Assert.True(slot.IsDimmed);
    }
}

public class LowBatteryTrackerTests
{
    private static DeviceBattery Mouse(int percent, bool charging = false) =>
        new("m", "Mouse", DeviceKind.Mouse, percent, charging, true);

    [Fact]
    public void AlertsOncePerDischarge()
    {
        var tracker = new LowBatteryTracker();

        Assert.Empty(tracker.Update([Mouse(16)]));
        Assert.Single(tracker.Update([Mouse(14)]));
        Assert.Empty(tracker.Update([Mouse(12)]));
        Assert.Empty(tracker.Update([Mouse(5)]));
    }

    [Fact]
    public void RearmsAfterCharging()
    {
        var tracker = new LowBatteryTracker();
        Assert.Single(tracker.Update([Mouse(10)]));

        Assert.Empty(tracker.Update([Mouse(11, charging: true)]));
        Assert.Single(tracker.Update([Mouse(14)]));
    }

    [Fact]
    public void IgnoresDisconnectedDevices()
    {
        var tracker = new LowBatteryTracker();

        Assert.Empty(tracker.Update([new DeviceBattery("m", "Mouse", DeviceKind.Mouse, 5, false, false)]));
    }
}
