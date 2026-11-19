using System.Collections.Concurrent;
using System.Windows.Automation;
using BatteryBar.Core;

namespace BatteryBar;

/// <summary>
/// Secondary taskbars draw their clock and notification button in XAML without a child window, so their
/// position is read through UI Automation (buttons with AutomationId "SystemTrayIcon"). The query crosses into
/// explorer, so it runs on a background thread and only now and then; results are cached per taskbar.
/// </summary>
internal sealed class SecondaryTrayProbe
{
    private readonly ConcurrentDictionary<IntPtr, int> _trayLeft = new();
    private int _running;

    public int? TrayLeft(IntPtr taskbar) => _trayLeft.TryGetValue(taskbar, out int left) ? left : null;

    /// <summary>Re-reads the given taskbars in the background; <paramref name="changed"/> runs (off the UI thread) if a value moved.</summary>
    public void RefreshAsync(IReadOnlyList<IntPtr> taskbars, Action changed)
    {
        if (taskbars.Count == 0 || Interlocked.Exchange(ref _running, 1) == 1)
        {
            return;
        }

        Task.Run(() =>
        {
            bool anyChange = false;
            try
            {
                foreach (IntPtr taskbar in taskbars)
                {
                    int? left = Query(taskbar);
                    if (left is int value)
                    {
                        anyChange |= !_trayLeft.TryGetValue(taskbar, out int old) || old != value;
                        _trayLeft[taskbar] = value;
                    }
                }

                foreach (IntPtr stale in _trayLeft.Keys.Except(taskbars))
                {
                    _trayLeft.TryRemove(stale, out _);
                }
            }
            catch (Exception ex)
            {
                Log.Error("UI Automation tray query failed", ex);
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }

            if (anyChange)
            {
                try
                {
                    changed();
                }
                catch (Exception ex)
                {
                    Log.Error("tray change callback failed", ex);
                }
            }
        });
    }

    private static int? Query(IntPtr taskbar)
    {
        try
        {
            AutomationElement bar = AutomationElement.FromHandle(taskbar);
            var condition = new PropertyCondition(AutomationElement.AutomationIdProperty, "SystemTrayIcon");
            AutomationElementCollection buttons = bar.FindAll(TreeScope.Descendants, condition);
            double left = double.MaxValue;
            foreach (AutomationElement button in buttons)
            {
                System.Windows.Rect rect = button.Current.BoundingRectangle;
                if (!rect.IsEmpty && rect.Width > 0)
                {
                    left = Math.Min(left, rect.Left);
                }
            }

            return left == double.MaxValue ? null : (int)Math.Floor(left);
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
    }
}
