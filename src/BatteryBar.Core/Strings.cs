using System.Globalization;

namespace BatteryBar.Core;

/// <summary>
/// English and Turkish UI strings. English is the default; Turkish is used when the Windows display
/// language is Turkish (or when "--lang tr" is passed).
/// </summary>
public static class Strings
{
    private static readonly Dictionary<string, (string En, string Tr)> Table = new()
    {
        ["Refresh"] = ("Refresh", "Yenile"),
        ["StartWithWindows"] = ("Start with Windows", "Windows ile başlat"),
        ["Exit"] = ("Exit", "Çıkış"),
        ["LowBatteryTitle"] = ("Battery low", "Pil zayıf"),
        ["NoDevices"] = ("No devices found", "Cihaz bulunamadı"),
        ["LastPoll"] = ("Last read: {0:HH:mm}", "Son okuma: {0:HH:mm}"),
        ["NotConnected"] = ("not connected", "bağlı değil"),
        ["Unknown"] = ("unknown", "bilinmiyor"),
        ["Charging"] = ("charging", "şarj oluyor"),
        ["Level"] = ("level {0}/5", "seviye {0}/5"),
        ["UnsupportedModel"] = ("unsupported model", "desteklenmeyen model"),
    };

    public static bool IsTurkish { get; set; } = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "tr";

    /// <summary>Applies "--lang en" / "--lang tr"; anything else keeps the Windows display language.</summary>
    public static void ApplyCommandLine(IReadOnlyList<string> args)
    {
        for (int i = 0; i + 1 < args.Count; i++)
        {
            if (string.Equals(args[i], "--lang", StringComparison.OrdinalIgnoreCase))
            {
                string lang = args[i + 1].Trim().ToLowerInvariant();
                if (lang is "en" or "tr")
                {
                    IsTurkish = lang == "tr";
                }
            }
        }
    }

    public static string T(string key) => Table.TryGetValue(key, out var s) ? (IsTurkish ? s.Tr : s.En) : key;

    public static string F(string key, params object?[] args) => string.Format(CultureInfo.InvariantCulture, T(key), args);

    /// <summary>A percentage as written in the UI language: "74%" in English, "%74" in Turkish.</summary>
    public static string Percent(int value) => IsTurkish ? $"%{value}" : $"{value}%";
}
