using static BatteryBar.NativeMethods;

namespace BatteryBar;

/// <param name="Handle">Shell_TrayWnd (primary) or Shell_SecondaryTrayWnd.</param>
/// <param name="Taskbar">Taskbar rectangle in physical pixels.</param>
/// <param name="Tray">Notification area (clock etc.); the widget goes right to its left.</param>
/// <param name="Scale">Effective DPI of that monitor / 96.</param>
/// <param name="Usable">False for an auto-hidden or vertical taskbar.</param>
/// <param name="Covered">A full-screen application owns this monitor; nothing may be drawn on it.</param>
internal sealed record TaskbarInfo(IntPtr Handle, bool IsPrimary, Rectangle Taskbar, Rectangle Tray, float Scale, uint Dpi, bool Usable, bool Covered);

internal static class TaskbarLocator
{
    /// <summary>Secondary taskbars have no tray window; at 100 % the clock + notification button take ~101 px.</summary>
    private const int SecondaryTrayFallbackWidth = 104;

    private static readonly HashSet<string> ShellClasses =
        ["Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland"];

    private static readonly int OwnProcessId = Environment.ProcessId;

    /// <summary>All taskbars: the primary one first, then every secondary-monitor taskbar.</summary>
    /// <param name="secondaryTrayLeft">Screen x of the left edge of a secondary taskbar's clock area, when known (UI Automation).</param>
    public static List<TaskbarInfo> LocateAll(Func<IntPtr, int?> secondaryTrayLeft)
    {
        var result = new List<TaskbarInfo>();
        FullscreenState fullscreen = FullscreenState.Read();

        IntPtr primary = FindWindowW("Shell_TrayWnd", null);
        if (primary != IntPtr.Zero && Describe(primary, true, fullscreen, null) is TaskbarInfo main)
        {
            result.Add(main);
        }

        IntPtr secondary = IntPtr.Zero;
        for (int guard = 0; guard < 16; guard++)
        {
            secondary = FindWindowExW(IntPtr.Zero, secondary, "Shell_SecondaryTrayWnd", null);
            if (secondary == IntPtr.Zero)
            {
                break;
            }

            if (Describe(secondary, false, fullscreen, secondaryTrayLeft(secondary)) is TaskbarInfo info)
            {
                result.Add(info);
            }
        }

        return result;
    }

    /// <summary>The tray's left edge must sit in the right half of the taskbar to be believed.</summary>
    internal static Rectangle ResolveTray(Rectangle bar, Rectangle? trayWindow, int? trayLeft, float scale)
    {
        if (trayWindow is Rectangle tray && tray.Width > 0 && bar.Contains(tray))
        {
            return tray;
        }

        if (trayLeft is int left && left > bar.Left + bar.Width / 2 && left < bar.Right)
        {
            return Rectangle.FromLTRB(left, bar.Top, bar.Right, bar.Bottom);
        }

        int width = (int)Math.Round((trayWindow is null ? SecondaryTrayFallbackWidth : 240) * scale);
        return Rectangle.FromLTRB(bar.Right - width, bar.Top, bar.Right, bar.Bottom);
    }

    private static TaskbarInfo? Describe(IntPtr taskbar, bool isPrimary, FullscreenState fullscreen, int? trayLeft)
    {
        if (!GetWindowRect(taskbar, out RECT barRect))
        {
            return null;
        }

        Rectangle bar = barRect.ToRectangle();
        IntPtr monitor = MonitorFromWindow(taskbar, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { Size = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfoW(monitor, ref info);
        Rectangle screen = info.Monitor.ToRectangle();

        uint dpi = 96;
        if (GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 && dpiX > 0)
        {
            dpi = dpiX;
        }

        float scale = dpi / 96f;
        Rectangle? trayWindow = null;
        if (isPrimary)
        {
            IntPtr notify = FindWindowExW(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
            trayWindow = notify != IntPtr.Zero && GetWindowRect(notify, out RECT trayRect) ? trayRect.ToRectangle() : Rectangle.Empty;
        }

        Rectangle tray = ResolveTray(bar, trayWindow, trayLeft, scale);

        // Only a horizontal taskbar that is actually on screen (not auto-hidden) is supported.
        Rectangle visible = Rectangle.Intersect(bar, screen);
        bool usable = IsWindowVisible(taskbar) && bar.Width > bar.Height && visible.Height >= bar.Height / 2 && visible.Height > 4;
        bool covered = fullscreen.Covers(monitor, screen, taskbar);
        return new TaskbarInfo(taskbar, isPrimary, bar, tray, scale, dpi, usable, covered);
    }

    /// <summary>Snapshot of "is a full-screen app in front", evaluated per monitor.</summary>
    private sealed class FullscreenState
    {
        private int _notificationState;
        private IntPtr _foregroundMonitor;
        private Rectangle _foregroundRect;
        private bool _hasForeground;

        public static FullscreenState Read()
        {
            var state = new FullscreenState();
            SHQueryUserNotificationState(out state._notificationState);
            IntPtr foreground = GetForegroundWindow();
            if (foreground != IntPtr.Zero && !IsIconic(foreground))
            {
                GetWindowThreadProcessId(foreground, out uint processId);
                if (processId != OwnProcessId && !ShellClasses.Contains(GetClassName(foreground)) && GetWindowRect(foreground, out RECT rect))
                {
                    state._hasForeground = true;
                    state._foregroundRect = rect.ToRectangle();
                    state._foregroundMonitor = MonitorFromWindow(foreground, MONITOR_DEFAULTTONEAREST);
                }
            }

            return state;
        }

        public bool Covers(IntPtr monitor, Rectangle screen, IntPtr taskbar)
        {
            // Presentation mode blocks everything.
            if (_notificationState == QUNS_PRESENTATION_MODE)
            {
                return true;
            }

            // Explorer drops a taskbar out of the top-most band while a full-screen app is active on its monitor.
            if ((GetWindowLong(taskbar, GWL_EXSTYLE) & WS_EX_TOPMOST) == 0)
            {
                return true;
            }

            if (!_hasForeground || _foregroundMonitor != monitor)
            {
                return false;
            }

            // Borderless "fullscreen" games are plain windows covering the whole monitor, taskbar included.
            Rectangle r = _foregroundRect;
            bool coversMonitor = r.Left <= screen.Left && r.Top <= screen.Top && r.Right >= screen.Right && r.Bottom >= screen.Bottom;
            return coversMonitor || _notificationState is QUNS_BUSY or QUNS_RUNNING_D3D_FULL_SCREEN;
        }
    }
}
