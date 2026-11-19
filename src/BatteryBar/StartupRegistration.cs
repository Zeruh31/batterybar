using BatteryBar.Core;
using Microsoft.Win32;

namespace BatteryBar;

/// <summary>"Start with Windows": a value under HKCU\...\Run. Written only when the user ticks the menu item.</summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "BatteryBar";

    private static string Command => $"\"{Environment.ProcessPath}\"";

    public static bool IsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value && string.Equals(value, Command, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Error("cannot read Run key", ex);
            return false;
        }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                key.SetValue(ValueName, Command, RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            Log.Info($"start with Windows: {enabled}");
        }
        catch (Exception ex)
        {
            Log.Error("cannot write Run key", ex);
        }
    }
}
