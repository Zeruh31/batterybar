using BatteryBar.Core;
using Microsoft.Win32;
using static BatteryBar.NativeMethods;

namespace BatteryBar;

/// <summary>
/// Owns the shared state (battery data, menu, tooltip, notifications) and one <see cref="WidgetWindow"/>
/// per taskbar: the primary taskbar and every secondary-monitor taskbar.
/// </summary>
internal sealed class WidgetHost : ApplicationContext
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);

    /// <summary>Layout ticks between UI Automation reads of the secondary clocks (they rarely move).</summary>
    private const int TrayProbeEveryTicks = 30;

    private readonly BatteryMonitor _monitor;
    private readonly LowBatteryTracker _lowBattery = new();
    private readonly SecondaryTrayProbe _trayProbe = new();
    private readonly Dictionary<IntPtr, WidgetWindow> _windows = new();
    private readonly System.Windows.Forms.Timer _layoutTimer = new() { Interval = 1000 };
    private readonly ToolTip _toolTip = new() { ShowAlways = true, InitialDelay = 300, AutoPopDelay = 15000 };
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _startupItem;
    private readonly int _taskbarCreatedMessage = RegisterWindowMessageW("TaskbarCreated");
    private readonly WinEventProc _foregroundProc;
    private readonly SynchronizationContext _ui;
    private IntPtr _foregroundHook;
    private NotifyIcon? _notifyIcon;

    private IReadOnlyList<DeviceBattery> _devices = [];
    private string _tooltip = WidgetModel.BuildTooltip([], null);
    private bool _lightTheme = WidgetRenderer.SystemUsesLightTheme();
    private int _ticks;
    private bool _closing;
    private int _layoutQueued;

    public WidgetHost()
    {
        if (SynchronizationContext.Current is not WindowsFormsSynchronizationContext)
        {
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        }

        _ui = SynchronizationContext.Current!;

        _menu.Items.Add(Strings.T("Refresh"), null, (_, _) => _monitor!.RefreshNow());
        _startupItem = new ToolStripMenuItem(Strings.T("StartWithWindows")) { CheckOnClick = true };
        _startupItem.Click += (_, _) => StartupRegistration.Set(_startupItem.Checked);
        _menu.Items.Add(_startupItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(Strings.T("Exit"), null, (_, _) => Shutdown());
        _menu.Opening += (_, _) => _startupItem.Checked = StartupRegistration.IsEnabled();

        _foregroundProc = OnForegroundChanged;
        _foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _foregroundProc, 0, 0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        _monitor = new BatteryMonitor(BatteryProviders.CreateDefault(), PollInterval);
        _monitor.Updated += devices => Post(() => OnDevicesUpdated(devices));
        _layoutTimer.Tick += (_, _) => OnTick();

        UpdateAll(force: true);
        _layoutTimer.Start();
        _monitor.Start();
        Log.Info("widget started");
    }

    public WidgetRenderer Renderer { get; } = new();

    /// <summary>Re-layouts all widgets soon (coalesced), from any thread.</summary>
    public void RequestLayout(bool force)
    {
        if (Volatile.Read(ref _layoutQueued) == 1 && !force)
        {
            return;
        }

        Volatile.Write(ref _layoutQueued, 1);
        Post(() =>
        {
            Volatile.Write(ref _layoutQueued, 0);
            UpdateAll(force);
        });
    }

    public void ShowMenu(Point screen)
    {
        try
        {
            _menu.Show(screen);
        }
        catch (Exception ex)
        {
            Log.Error("cannot show menu", ex);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Shutdown();
        }

        base.Dispose(disposing);
    }

    private void Shutdown()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        _layoutTimer.Stop();
        if (_foregroundHook != IntPtr.Zero)
        {
            UnhookWinEvent(_foregroundHook);
            _foregroundHook = IntPtr.Zero;
        }

        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _monitor.Dispose();
        foreach (WidgetWindow window in _windows.Values)
        {
            _toolTip.SetToolTip(window, null);
            window.Dispose();
        }

        _windows.Clear();
        _notifyIcon?.Dispose();
        Renderer.Dispose();
        _toolTip.Dispose();
        _menu.Dispose();
        _layoutTimer.Dispose();
        Log.Info("widget closed");
        ExitThread();
    }

    private void OnTick()
    {
        if (++_ticks % TrayProbeEveryTicks == 0)
        {
            ProbeSecondaryTrays();
        }

        UpdateAll(force: false);
    }

    /// <summary>Finds every taskbar, creates/removes widgets to match, and lays each one out. Never throws.</summary>
    private void UpdateAll(bool force)
    {
        if (_closing)
        {
            return;
        }

        try
        {
            List<TaskbarInfo> taskbars = TaskbarLocator.LocateAll(_trayProbe.TrayLeft);
            var alive = taskbars.Select(t => t.Handle).ToHashSet();
            foreach (IntPtr gone in _windows.Keys.Where(k => !alive.Contains(k)).ToList())
            {
                Log.Info($"taskbar {gone:X} gone");
                _toolTip.SetToolTip(_windows[gone], null);
                _windows[gone].Dispose();
                _windows.Remove(gone);
            }

            bool added = false;
            IReadOnlyList<WidgetSlot> slots = WidgetModel.BuildSlots(_devices);
            foreach (TaskbarInfo taskbar in taskbars)
            {
                if (!_windows.TryGetValue(taskbar.Handle, out WidgetWindow? window))
                {
                    window = new WidgetWindow(this, taskbar.Handle, _taskbarCreatedMessage);
                    window.Show();
                    _toolTip.SetToolTip(window, _tooltip);
                    _windows[taskbar.Handle] = window;
                    added = true;
                    Log.Info($"taskbar {taskbar.Handle:X} ({(taskbar.IsPrimary ? "primary" : "secondary")}) {taskbar.Taskbar} dpi {taskbar.Dpi} tray {taskbar.Tray}");
                }

                window.Apply(taskbar, slots, _lightTheme, force);
            }

            if (added)
            {
                ProbeSecondaryTrays();
            }
        }
        catch (Exception ex)
        {
            Log.Error("layout update failed", ex);
        }
    }

    private void ProbeSecondaryTrays()
    {
        var secondaries = _windows.Keys.Where(h => h != FindWindowW("Shell_TrayWnd", null)).ToList();
        _trayProbe.RefreshAsync(secondaries, () => RequestLayout(force: false));
    }

    private void OnDevicesUpdated(IReadOnlyList<DeviceBattery> devices)
    {
        _devices = devices;
        _lightTheme = WidgetRenderer.SystemUsesLightTheme();
        _tooltip = WidgetModel.BuildTooltip(devices, DateTime.Now);
        foreach (WidgetWindow window in _windows.Values)
        {
            _toolTip.SetToolTip(window, _tooltip);
        }

        Log.Info("poll: " + string.Join("; ", devices.Select(d => $"{d.Name}={(d.Connected ? d.Percent?.ToString() ?? "?" : "off")}{(d.Charging ? "+" : "")}")));
        IReadOnlyList<DeviceBattery> low = _lowBattery.Update(devices);
        if (low.Count > 0)
        {
            ShowLowBatteryNotification(low);
        }

        UpdateAll(force: true);
    }

    private void ShowLowBatteryNotification(IReadOnlyList<DeviceBattery> devices)
    {
        string text = string.Join(Environment.NewLine, devices.Select(d => $"{d.Name}: {Strings.Percent(d.Percent ?? 0)}"));
        Log.Info("low battery: " + text.Replace(Environment.NewLine, "; "));
        try
        {
            // A temporary tray icon carries the balloon (shown as a toast on Windows 11) and is removed afterwards.
            _notifyIcon ??= new NotifyIcon { Icon = SystemIcons.Warning, Text = "BatteryBar" };
            _notifyIcon.Visible = true;
            _notifyIcon.ShowBalloonTip(10000, Strings.T("LowBatteryTitle"), text, ToolTipIcon.Warning);
            var hide = new System.Windows.Forms.Timer { Interval = 30000 };
            hide.Tick += (_, _) =>
            {
                try
                {
                    hide.Dispose();
                    if (_notifyIcon is not null)
                    {
                        _notifyIcon.Visible = false;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("cannot hide notification icon", ex);
                }
            };
            hide.Start();
        }
        catch (Exception ex)
        {
            Log.Error("cannot show notification", ex);
        }
    }

    private void OnForegroundChanged(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint eventThread, uint eventTime)
    {
        // Runs on the UI thread (out-of-context hook). React at once: hide on the monitor of a full-screen game,
        // or come back on top after a taskbar was clicked. An exception must not escape into native code.
        try
        {
            UpdateAll(force: false);
        }
        catch (Exception ex)
        {
            Log.Error("foreground hook failed", ex);
        }
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e) =>
        Post(() =>
        {
            // Light/dark switches arrive here (category General); the theme is cached between changes.
            _lightTheme = WidgetRenderer.SystemUsesLightTheme();
            UpdateAll(force: true);
        });

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Post(() =>
        {
            // Monitor added/removed or resolution/DPI changed: taskbars and clocks move.
            UpdateAll(force: true);
            ProbeSecondaryTrays();
        });

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        try
        {
            if (e.Mode == PowerModes.Resume)
            {
                _monitor.RefreshNow();
            }
        }
        catch (Exception ex)
        {
            Log.Error("resume refresh failed", ex);
        }
    }

    private void Post(Action action) =>
        _ui.Post(_ =>
        {
            try
            {
                if (!_closing)
                {
                    action();
                }
            }
            catch (Exception ex)
            {
                Log.Error("UI callback failed", ex);
            }
        }, null);
}
