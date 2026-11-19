using BatteryBar.Core.Providers;

namespace BatteryBar.Core;

public static class BatteryProviders
{
    /// <summary>All providers the app polls. A new device family is added to this list.</summary>
    public static IReadOnlyList<IBatteryProvider> CreateDefault() =>
    [
        new LogitechHidppProvider(),
        new FlydigiProvider(),
    ];
}
