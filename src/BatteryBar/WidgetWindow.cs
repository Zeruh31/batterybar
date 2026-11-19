using System.Drawing.Imaging;
using BatteryBar.Core;
using static BatteryBar.NativeMethods;

namespace BatteryBar;

/// <summary>
/// One borderless, per-pixel-alpha overlay on one taskbar, just left of its clock / notification area.
/// A separate top-most tool window (no taskbar button, never activated), not a child of explorer.
/// </summary>
internal sealed class WidgetWindow : Form
{
    private readonly WidgetHost _host;
    private readonly int _taskbarCreatedMessage;

    private TaskbarInfo? _taskbar;
    private IReadOnlyList<WidgetSlot> _slots = [];
    private bool _lightTheme;
    private bool _hover;
    private string _renderKey = string.Empty;
    private Size _renderSize;
    private Point _location = new(int.MinValue, int.MinValue);

    public WidgetWindow(WidgetHost host, IntPtr taskbar, int taskbarCreatedMessage)
    {
        _host = host;
        _taskbarCreatedMessage = taskbarCreatedMessage;
        TaskbarHandle = taskbar;
        Text = "BatteryBar";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        Location = new Point(-32000, -32000);
        Size = new Size(1, 1);
    }

    public IntPtr TaskbarHandle { get; }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.Style = WS_POPUP;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    /// <summary>Positions, renders (when something changed), shows or hides, and re-asserts top-most.</summary>
    public void Apply(TaskbarInfo taskbar, IReadOnlyList<WidgetSlot> slots, bool lightTheme, bool force)
    {
        _taskbar = taskbar;
        _slots = slots;
        _lightTheme = lightTheme;
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        if (!taskbar.Usable || taskbar.Covered)
        {
            if (Visible)
            {
                ShowWindow(Handle, SW_HIDE);
                Visible = false;
            }

            return;
        }

        string key = $"{string.Join("|", slots.Select(s => $"{s.Kind}:{s.Text}:{s.IsLow}:{s.IsCharging}:{s.IsDimmed}"))}/{taskbar.Dpi}/{taskbar.Taskbar.Height}/{lightTheme}/{_hover}";
        if (force || key != _renderKey)
        {
            using Bitmap bitmap = _host.Renderer.Render(slots, taskbar.Scale, taskbar.Taskbar.Height, lightTheme, _hover);
            _renderSize = bitmap.Size;
            Point target = TargetLocation(taskbar, _renderSize);
            ApplyBitmap(bitmap, target);
            _renderKey = key;
            _location = target;
        }
        else
        {
            Point target = TargetLocation(taskbar, _renderSize);
            if (target != _location)
            {
                SetWindowPos(Handle, HWND_TOPMOST, target.X, target.Y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
                _location = target;
            }
        }

        if (!Visible)
        {
            Visible = true;
            ShowWindow(Handle, SW_SHOWNOACTIVATE);
        }

        // The taskbar is top-most too and wins whenever it is clicked; take the top spot back (never activating).
        SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_MOUSEACTIVATE)
        {
            m.Result = MA_NOACTIVATE;
            return;
        }

        if (m.Msg == WM_DPICHANGED)
        {
            // Size and position come from the taskbar's monitor; skip WinForms' own rescaling.
            _host.RequestLayout(force: true);
            m.Result = IntPtr.Zero;
            return;
        }

        if (m.Msg == _taskbarCreatedMessage && _taskbarCreatedMessage != 0)
        {
            // Explorer restarted: taskbars are new windows, so enumerate them again.
            Log.Info("TaskbarCreated: explorer restarted");
            _host.RequestLayout(force: true);
        }
        else if (m.Msg is WM_DISPLAYCHANGE or WM_SETTINGCHANGE)
        {
            _host.RequestLayout(force: true);
        }

        base.WndProc(ref m);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        SetHover(true);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        SetHover(false);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Right)
        {
            _host.ShowMenu(Cursor.Position);
        }
    }

    private static Point TargetLocation(TaskbarInfo taskbar, Size size)
    {
        int gap = (int)Math.Round(4 * taskbar.Scale);
        int x = taskbar.Tray.Left - gap - size.Width;
        int y = taskbar.Taskbar.Top + (taskbar.Taskbar.Height - size.Height) / 2;
        return new Point(x, y);
    }

    private void SetHover(bool hover)
    {
        try
        {
            _hover = hover;
            if (_taskbar is not null)
            {
                Apply(_taskbar, _slots, _lightTheme, force: false);
            }
        }
        catch (Exception ex)
        {
            Log.Error("hover update failed", ex);
        }
    }

    /// <summary>Copies the premultiplied pixels 1:1 into a DIB section and hands it to UpdateLayeredWindow.</summary>
    private unsafe void ApplyBitmap(Bitmap bitmap, Point location)
    {
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memoryDc = CreateCompatibleDC(screenDc);
        var header = new BITMAPINFOHEADER
        {
            Size = sizeof(BITMAPINFOHEADER),
            Width = bitmap.Width,
            Height = -bitmap.Height,
            Planes = 1,
            BitCount = 32,
        };
        IntPtr dib = CreateDIBSection(memoryDc, ref header, DIB_RGB_COLORS, out IntPtr bits, IntPtr.Zero, 0);
        if (dib == IntPtr.Zero || bits == IntPtr.Zero)
        {
            Log.Error($"CreateDIBSection failed: {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
            if (dib != IntPtr.Zero)
            {
                DeleteObject(dib);
            }

            DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
            return;
        }

        IntPtr previous = SelectObject(memoryDc, dib);
        try
        {
            BitmapData data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                int rowBytes = bitmap.Width * 4;
                for (int y = 0; y < bitmap.Height; y++)
                {
                    Buffer.MemoryCopy((byte*)data.Scan0 + y * data.Stride, (byte*)bits + y * rowBytes, rowBytes, rowBytes);
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }

            var size = new SIZE { Cx = bitmap.Width, Cy = bitmap.Height };
            var source = new POINT();
            var destination = new POINT { X = location.X, Y = location.Y };
            var blend = new BLENDFUNCTION { BlendOp = AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA };
            if (!UpdateLayeredWindow(Handle, screenDc, ref destination, ref size, memoryDc, ref source, 0, ref blend, ULW_ALPHA))
            {
                Log.Error($"UpdateLayeredWindow failed: {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
            }
        }
        finally
        {
            SelectObject(memoryDc, previous);
            DeleteObject(dib);
            DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
