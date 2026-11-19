using BatteryBar.Core;

namespace BatteryBar.Tests;

/// <summary>
/// Everything that depends on the global UI language lives in this one class, so its tests run one after
/// another and never race on <see cref="Strings.IsTurkish"/>.
/// </summary>
public class LocalizationTests : IDisposable
{
    private readonly bool _saved = Strings.IsTurkish;

    public void Dispose() => Strings.IsTurkish = _saved;

    private static DeviceBattery Device(string name, DeviceKind kind, int? percent, bool connected = true, bool charging = false) =>
        new(name, name, kind, percent, charging, connected);

    [Fact]
    public void Tooltip_English()
    {
        Strings.IsTurkish = false;

        string text = WidgetModel.BuildTooltip(
            [Device("PRO X Wireless Gaming Headset", DeviceKind.Headset, 74, charging: true), Device("PRO X 2 DEX", DeviceKind.Mouse, null, connected: false)],
            new DateTime(2026, 1, 2, 9, 5, 0));

        Assert.Equal(
            ["PRO X 2 DEX: not connected", "PRO X Wireless Gaming Headset: 74% (charging)", "Last read: 09:05"],
            text.Split(Environment.NewLine));
    }

    [Fact]
    public void Tooltip_Turkish()
    {
        Strings.IsTurkish = true;

        string text = WidgetModel.BuildTooltip(
            [Device("PRO X Wireless Gaming Headset", DeviceKind.Headset, 74), Device("PRO X 2 DEX", DeviceKind.Mouse, null, connected: false)],
            null);

        Assert.Equal(["PRO X 2 DEX: bağlı değil", "PRO X Wireless Gaming Headset: %74"], text.Split(Environment.NewLine));
    }

    [Fact]
    public void Tooltip_NoDevices()
    {
        Strings.IsTurkish = false;

        Assert.Equal("No devices found", WidgetModel.BuildTooltip([], null));
    }

    [Theory]
    [InlineData(false, "Flydigi VADER 5 Pro: 100% (level 5/5)")]
    [InlineData(true, "Flydigi VADER 5 Pro: %100 (seviye 5/5)")]
    public void Tooltip_ShowsCoarseLevel(bool turkish, string expected)
    {
        Strings.IsTurkish = turkish;
        var pad = new DeviceBattery("pad", "Flydigi VADER 5 Pro", DeviceKind.Gamepad, 100, false, true) { Detail = Strings.F("Level", 5) };

        Assert.Equal(expected, WidgetModel.BuildTooltip([pad], null));
    }

    [Theory]
    [InlineData(new[] { "--lang", "tr" }, true)]
    [InlineData(new[] { "--LANG", "EN" }, false)]
    [InlineData(new[] { "--lang", "de" }, null)]
    [InlineData(new[] { "--lang" }, null)]
    public void CommandLine_SelectsLanguage(string[] args, bool? expected)
    {
        foreach (bool start in new[] { false, true })
        {
            Strings.IsTurkish = start;
            Strings.ApplyCommandLine(args);
            Assert.Equal(expected ?? start, Strings.IsTurkish);
        }
    }

    [Fact]
    public void UnknownKey_ReturnsKey()
    {
        Assert.Equal("NoSuchKey", Strings.T("NoSuchKey"));
    }
}
