using BatteryBar.Core;
using BatteryBar.Core.Hid;
using BatteryBar.Core.Hidpp;

// Diagnostic tool: lists HID++ collections and prints what every provider reads.
// Usage: BatteryBar.Probe [--raw]   (--raw also dumps every HID++ request/answer)
Log.FilePath = Path.Combine(AppContext.BaseDirectory, "probe.log");
Log.Echo = Console.WriteLine;
Log.Verbose = args.Contains("--raw");

Console.WriteLine("== Logitech HID collections ==");
var collections = HidEnumerator.Enumerate(0x046D);
foreach (HidCollectionInfo c in collections)
{
    Console.WriteLine($"{(HidppChannel.IsHidppCollection(c) ? "*" : " ")} {c}");
}

if (args.Contains("--deep"))
{
    foreach (var group in collections.Where(HidppChannel.IsHidppCollection).GroupBy(c => c.GroupKey))
    {
        Console.WriteLine($"== group {group.Key} ==");
        using HidppChannel? channel = HidppChannel.Open(group);
        if (channel is null)
        {
            Console.WriteLine("  cannot open");
            continue;
        }

        foreach (byte index in new byte[] { 0xFF, 1, 2, 3, 4, 5, 6 })
        {
            HidppResponse ping = channel.Request(index, 0x00, 0x1, [0, 0, 0x5A], 1000);
            Console.WriteLine($"  idx {index:X2} ping: {ping.Status} err={ping.ErrorCode:X2} {Convert.ToHexString(ping.Params)}");
            if (!ping.IsOk)
            {
                continue;
            }

            foreach (ushort feature in new ushort[] { 0x0005, 0x1000, 0x1001, 0x1004, 0x1F20 })
            {
                HidppResponse f = channel.Request(index, 0x00, 0x0, [(byte)(feature >> 8), (byte)feature], 1000);
                Console.WriteLine($"    feature {feature:X4}: {f.Status} {Convert.ToHexString(f.Params)}");
            }
        }

        for (byte slot = 0; slot < 6; slot++)
        {
            HidppResponse pairing = channel.RawRequest(HidppMessages.ReportShort, 0xFF, 0x83, 0xB5, [(byte)(0x20 + slot)], 1000);
            Console.WriteLine($"  reg B5 {0x20 + slot:X2}: {pairing.Status} err={pairing.ErrorCode:X2} {Convert.ToHexString(pairing.Params)}");
            if (pairing.IsOk)
            {
                HidppResponse name = channel.RawRequest(HidppMessages.ReportShort, 0xFF, 0x83, 0xB5, [(byte)(0x40 + slot)], 1000);
                Console.WriteLine($"  reg B5 {0x40 + slot:X2}: {name.Status} err={name.ErrorCode:X2} {Convert.ToHexString(name.Params)}");
            }
        }
    }
}

Console.WriteLine("== Providers ==");
foreach (IBatteryProvider provider in BatteryProviders.CreateDefault())
{
    using (provider)
    {
        foreach (DeviceBattery d in provider.Poll(CancellationToken.None))
        {
            Console.WriteLine($"[{provider.Name}] {d}");
        }
    }
}
