using System.Xml.Linq;
using BatteryBar.Core;
using BatteryBar.Core.Hid;
using BatteryBar.Core.Hidpp;
using BatteryBar.Core.Providers;

namespace BatteryBar.Tests;

public class HidppMessagesTests
{
    [Fact]
    public void Build_LongRequest_PacksFunctionAndSoftwareId()
    {
        byte[] report = HidppMessages.Build(HidppMessages.ReportLong, 0x01, 0x00, 0x1, 0x0B, [0, 0, 0x5A]);

        Assert.Equal(20, report.Length);
        Assert.Equal(new byte[] { 0x11, 0x01, 0x00, 0x1B, 0x00, 0x00, 0x5A }, report[..7]);
        Assert.All(report[7..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void Build_TooManyParameters_Throws()
    {
        Assert.Throws<ArgumentException>(() => HidppMessages.Build(HidppMessages.ReportShort, 1, 0, 0, 1, [1, 2, 3, 4]));
    }

    [Fact]
    public void Match_Answer_ReturnsParams()
    {
        // Real ping answer from the PRO X Wireless dongle: protocol 4.2, echo 0x5A.
        byte[] report = Convert.FromHexString("11FF001B04025A00000000000000000000000000");

        HidppResponse? response = HidppMessages.Match(report, 0xFF, 0x00, 0x1B);

        Assert.NotNull(response);
        Assert.Equal(HidppStatus.Ok, response.Status);
        Assert.Equal(new byte[] { 0x04, 0x02, 0x5A }, response.Params[..3]);
    }

    [Fact]
    public void Match_Hidpp10Error_FromReceiver()
    {
        // Real LIGHTSPEED receiver answer for an empty / sleeping slot: error 0x08.
        byte[] report = Convert.FromHexString("10018F001B0800");

        HidppResponse? response = HidppMessages.Match(report, 0x01, 0x00, 0x1B);

        Assert.NotNull(response);
        Assert.Equal(HidppStatus.Hidpp10Error, response.Status);
        Assert.Equal(0x08, response.ErrorCode);
    }

    [Fact]
    public void Match_Hidpp20Error()
    {
        byte[] report = Convert.FromHexString("11FFFF060B05000000000000000000000000000000");

        HidppResponse? response = HidppMessages.Match(report[..20], 0xFF, 0x06, 0x0B);

        Assert.NotNull(response);
        Assert.Equal(HidppStatus.Hidpp20Error, response.Status);
        Assert.Equal(0x05, response.ErrorCode);
    }

    [Theory]
    [InlineData("11FF001A04025A00000000000000000000000000")] // other software id (e.g. G HUB)
    [InlineData("11FE001B04025A00000000000000000000000000")] // other device index
    [InlineData("11FF040004025A00000000000000000000000000")] // notification
    [InlineData("0201020304050607")]                         // not a HID++ report
    public void Match_ForeignReport_ReturnsNull(string hex)
    {
        Assert.Null(HidppMessages.Match(Convert.FromHexString(hex), 0xFF, 0x00, 0x1B));
    }
}

public class BatteryDecoderTests
{
    [Fact]
    public void UnifiedBattery_Percentage()
    {
        BatteryReading reading = BatteryDecoders.UnifiedBattery([97, 0x08, 0x00, 0x00], supportsPercentage: true);

        Assert.Equal(97, reading.Percent);
        Assert.False(reading.Charging);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(0, false)]
    [InlineData(4, false)]
    public void UnifiedBattery_ChargingStatus(byte status, bool charging)
    {
        Assert.Equal(charging, BatteryDecoders.UnifiedBattery([50, 0x04, status, 0], true).Charging);
    }

    [Fact]
    public void UnifiedBattery_LevelOnlyDevice_MapsFlags()
    {
        Assert.Equal(20, BatteryDecoders.UnifiedBattery([0, 0x02, 0, 0], supportsPercentage: false).Percent);
        Assert.Equal(100, BatteryDecoders.UnifiedBattery([0, 0x08, 0, 0], supportsPercentage: false).Percent);
    }

    [Fact]
    public void UnifiedBattery_CapabilitiesUnknown_UsesPlausiblePercentage()
    {
        Assert.Equal(97, BatteryDecoders.UnifiedBattery([97, 0x08, 0, 0], supportsPercentage: null).Percent);
    }

    [Fact]
    public void UnifiedBattery_CapabilitiesUnknown_ZeroPercent_FallsBackToLevelFlags()
    {
        Assert.Equal(50, BatteryDecoders.UnifiedBattery([0, 0x04, 0, 0], supportsPercentage: null).Percent);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    public void UnifiedBattery_NoUsableData_IsUnknownNotZero(bool? supportsPercentage)
    {
        Assert.Null(BatteryDecoders.UnifiedBattery([0, 0x00, 0, 0], supportsPercentage).Percent);
    }

    [Fact]
    public void UnifiedBattery_UnknownReading_NeverRaisesLowBatteryAlert()
    {
        BatteryReading reading = BatteryDecoders.UnifiedBattery([0, 0x00, 0, 0], supportsPercentage: null);
        var device = new DeviceBattery("m", "Mouse", DeviceKind.Mouse, reading.Percent, reading.Charging, true);

        Assert.Empty(new LowBatteryTracker().Update([device]));
        Assert.False(new WidgetSlot(DeviceKind.Mouse, device).IsLow);
    }

    [Fact]
    public void UnifiedCapabilities_PercentageFlag()
    {
        Assert.True(BatteryDecoders.UnifiedSupportsPercentage([0x0F, 0x02]));
        Assert.False(BatteryDecoders.UnifiedSupportsPercentage([0x0F, 0x01]));
    }

    [Fact]
    public void BatteryStatus_LevelAndCharging()
    {
        BatteryReading reading = BatteryDecoders.BatteryStatus([40, 20, 1]);

        Assert.Equal(40, reading.Percent);
        Assert.True(reading.Charging);
    }

    [Fact]
    public void AdcMeasurement_HeadsetSample_DecodesVoltageAndFlags()
    {
        // PRO X Wireless answer: 0x0F10 = 3856 mV, flags 0x01 (on battery).
        BatteryReading reading = BatteryDecoders.AdcMeasurement([0x0F, 0x10, 0x01], BatteryDecoders.DefaultCurve);

        Assert.Equal(3856, reading.Millivolts);
        Assert.False(reading.Charging);
        Assert.Equal(51, reading.Percent); // 3850 mV = 50 %, 4100 mV = 100 %
    }

    [Fact]
    public void AdcMeasurement_ChargingFlags()
    {
        Assert.True(BatteryDecoders.AdcMeasurement([0x10, 0x00, 0x03], BatteryDecoders.DefaultCurve).Charging);
    }

    [Theory]
    [InlineData(4300, 100)]
    [InlineData(4100, 100)]
    [InlineData(3975, 75)] // halfway between 3850 (50) and 4100 (100)
    [InlineData(3000, 0)]
    public void VoltageToPercent_Interpolates(int millivolts, int expected)
    {
        Assert.Equal(expected, BatteryDecoders.VoltageToPercent(millivolts, BatteryDecoders.DefaultCurve));
    }

    [Fact]
    public void VoltageToPercent_ZeroIsUnknown()
    {
        Assert.Null(BatteryDecoders.VoltageToPercent(0, BatteryDecoders.DefaultCurve));
    }

    [Fact]
    public void GHubDischargeCurve_RemainingCapacity()
    {
        // 4.10 V full, 4.00 V after 25 of 100 mAh, 3.80 V after 75, 3.30 V empty.
        var document = XDocument.Parse("""
            <powermodel vid="0x46d" pid="0x0aba"><battery><discharge format="default"><![CDATA[
            1,4.100,0
            2,4.000,25
            3,3.800,75
            4,3.300,100
            ]]></discharge></battery></powermodel>
            """);

        var curve = GHubPowerModel.ParseDischargeCurve(document);

        Assert.NotNull(curve);
        Assert.Equal(100, BatteryDecoders.VoltageToPercent(4100, curve));
        Assert.Equal(75, BatteryDecoders.VoltageToPercent(4000, curve));
        Assert.Equal(25, BatteryDecoders.VoltageToPercent(3800, curve));
        Assert.Equal(0, BatteryDecoders.VoltageToPercent(3200, curve));
    }

    [Fact]
    public void GHubDischargeCurve_MissingTable_ReturnsNull()
    {
        var document = XDocument.Parse("""<powermodel pid="0x40b8"><battery><capacity watts_per_hour="1.06"/></battery></powermodel>""");

        Assert.Null(GHubPowerModel.ParseDischargeCurve(document));
    }
}

public class HidEnumeratorTests
{
    [Fact]
    public void GroupKey_JoinsCollectionsOfOneInterface()
    {
        string col1 = @"\\?\HID#VID_046D&PID_C54D&MI_02&Col01#a&1234abcd&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        string col2 = @"\\?\HID#VID_046D&PID_C54D&MI_02&Col02#a&1234abcd&0&0001#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        string other = @"\\?\HID#VID_046D&PID_0ABA&MI_03&Col02#a&5678ef01&0&0001#{4d1e55b2-f16f-11cf-88cb-001111000030}";

        Assert.Equal(HidEnumerator.GroupKeyFromPath(col1), HidEnumerator.GroupKeyFromPath(col2));
        Assert.NotEqual(HidEnumerator.GroupKeyFromPath(col1), HidEnumerator.GroupKeyFromPath(other));
    }

    [Fact]
    public void ParseVidPid()
    {
        Assert.Equal(((ushort)0x046D, (ushort)0x0ABA), HidEnumerator.ParseVidPid(@"\\?\HID#VID_046D&PID_0ABA&MI_03&Col02#x#{g}"));
        Assert.Null(HidEnumerator.ParseVidPid(@"\\?\something-else"));
    }

    [Theory]
    [InlineData(0xFF00, 7, true)]
    [InlineData(0xFF43, 20, true)]
    [InlineData(0xFF00, 64, true)]
    [InlineData(0xFFA0, 20, false)]
    [InlineData(0x000C, 20, false)]
    [InlineData(0xFF00, 2, false)]
    public void HidppCollectionFilter(int usagePage, int inputLength, bool expected)
    {
        var info = new HidCollectionInfo("p", 0x046D, 0xC54D, (ushort)usagePage, 1, inputLength, inputLength, "x");

        Assert.Equal(expected, HidppChannel.IsHidppCollection(info));
    }
}

public class ProviderHelperTests
{
    [Theory]
    [InlineData(3, DeviceKind.Mouse)]
    [InlineData(8, DeviceKind.Headset)]
    [InlineData(12, DeviceKind.Gamepad)]
    [InlineData(0, DeviceKind.Other)]
    public void KindFromDeviceType(byte type, DeviceKind kind)
    {
        Assert.Equal(kind, LogitechHidppProvider.KindFromDeviceType(type));
    }

    [Theory]
    [InlineData("PRO X Wireless Gaming Headset", DeviceKind.Headset)]
    [InlineData("PRO X SUPERLIGHT 2", DeviceKind.Mouse)]
    [InlineData("Wireless Gamepad F710", DeviceKind.Gamepad)]
    [InlineData("MX Keys", DeviceKind.Other)]
    public void GuessKind(string name, DeviceKind kind)
    {
        Assert.Equal(kind, LogitechHidppProvider.GuessKind(name));
    }
}
