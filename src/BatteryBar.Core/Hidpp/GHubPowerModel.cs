using System.Globalization;
using System.Xml.Linq;

namespace BatteryBar.Core.Hidpp;

/// <summary>
/// Voltage-reporting devices (0x1001 / 0x1F20) need a discharge curve to show a percentage.
/// If G HUB is installed, its per-device power model ("*battery*.xml" under %ProgramData%\LGHUB\depots,
/// rows of "minutes, volts, discharged mAh") is read at run time from that local installation so the percentage
/// matches G HUB. The files are only read; none of their content ships with this app.
/// </summary>
public static class GHubPowerModel
{
    private static readonly object Gate = new();
    private static Dictionary<ushort, string>? _filesByPid;
    private static readonly Dictionary<ushort, (int Millivolts, int Percent)[]?> Curves = new();

    public static string DepotsDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LGHUB", "depots");

    /// <summary>Curve for a USB or wireless product id, or null when G HUB has no model for it.</summary>
    public static (int Millivolts, int Percent)[]? TryGetCurve(ushort productId)
    {
        lock (Gate)
        {
            if (Curves.TryGetValue(productId, out var cached))
            {
                return cached;
            }

            _filesByPid ??= IndexFiles();
            (int, int)[]? curve = null;
            if (_filesByPid.TryGetValue(productId, out string? file))
            {
                try
                {
                    curve = ParseDischargeCurve(XDocument.Load(file));
                    Log.Info($"using G HUB discharge curve for {productId:X4}: {file} ({curve?.Length ?? 0} points)");
                }
                catch (Exception ex)
                {
                    Log.Error($"cannot read G HUB power model {file}", ex);
                }
            }

            Curves[productId] = curve;
            return curve;
        }
    }

    /// <summary>
    /// Turns the discharge table into (mV, %) points every 10 mV, highest first.
    /// Remaining % at a voltage = 1 - (discharged mAh at the first row at or below it) / total mAh.
    /// </summary>
    internal static (int Millivolts, int Percent)[]? ParseDischargeCurve(XDocument document)
    {
        string? table = document.Root?.Element("battery")?.Element("discharge")?.Value;
        if (string.IsNullOrWhiteSpace(table))
        {
            return null;
        }

        var rows = new List<(int Millivolts, double Discharged)>();
        foreach (string line in table.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] cells = line.Split(',');
            if (cells.Length >= 3
                && double.TryParse(cells[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double volts)
                && double.TryParse(cells[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double mah))
            {
                rows.Add(((int)Math.Round(volts * 1000), mah));
            }
        }

        if (rows.Count < 2)
        {
            return null;
        }

        double total = rows[^1].Discharged;
        int top = rows.Max(r => r.Millivolts);
        int bottom = rows.Min(r => r.Millivolts);
        if (total <= 0 || top <= bottom)
        {
            return null;
        }

        var points = new List<(int, int)>();
        for (int mv = top; mv >= bottom; mv -= 10)
        {
            int rowIndex = rows.FindIndex(r => r.Millivolts <= mv);
            double discharged = rowIndex < 0 ? total : rows[rowIndex].Discharged;
            points.Add((mv, (int)Math.Round(100 * (1 - discharged / total))));
        }

        points.Add((bottom - 1, 0));
        return points.ToArray();
    }

    private static Dictionary<ushort, string> IndexFiles()
    {
        var map = new Dictionary<ushort, string>();
        try
        {
            if (!Directory.Exists(DepotsDirectory))
            {
                return map;
            }

            foreach (string file in Directory.EnumerateFiles(DepotsDirectory, "*battery*.xml", SearchOption.AllDirectories))
            {
                try
                {
                    // Only the root element is needed to know which product the file is for.
                    using var reader = System.Xml.XmlReader.Create(file);
                    if (reader.MoveToContent() == System.Xml.XmlNodeType.Element && reader.Name == "powermodel"
                        && reader.GetAttribute("pid") is string pid
                        && ushort.TryParse(pid.Replace("0x", "", StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort productId))
                    {
                        map.TryAdd(productId, file);
                    }
                }
                catch (Exception)
                {
                    // Unreadable or unrelated file; skip it.
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("cannot scan G HUB depots", ex);
        }

        return map;
    }
}
