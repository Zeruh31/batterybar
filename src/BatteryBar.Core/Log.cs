using System.Text;

namespace BatteryBar.Core;

/// <summary>Tiny append-only file log in %LOCALAPPDATA%\BatteryBar. Never throws.</summary>
public static class Log
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly object Gate = new();

    public static string Directory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BatteryBar");

    public static string FilePath { get; set; } = Path.Combine(Directory, "batterybar.log");

    /// <summary>Optional extra sink (the probe tool mirrors lines to the console).</summary>
    public static Action<string>? Echo { get; set; }

    public static bool Verbose { get; set; }

    public static void Debug(string message)
    {
        if (Verbose)
        {
            Write("DBG", message);
        }
    }

    public static void Info(string message) => Write("INF", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERR", ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}";
        Echo?.Invoke(line);
        lock (Gate)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaxBytes)
                {
                    File.Move(FilePath, FilePath + ".old", overwrite: true);
                }

                File.AppendAllText(FilePath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // Logging must never take the app down.
            }
        }
    }
}
