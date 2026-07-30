using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Handy.PInvoke;

namespace Handy;

public partial class RecordingOverlay : Window
{
    public enum State { Recording, Transcribing }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW  = 0x00000080;
    private const int WS_EX_NOACTIVATE  = 0x08000000;

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOMOVE     = 0x0002;
    private const uint SWP_NOSIZE     = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    public RecordingOverlay()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var ex = GetWindowLong32(hwnd, GWL_EXSTYLE);
            SetWindowLong32(hwnd, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        };
    }

    private static readonly Brush IdleBackground    = new SolidColorBrush(Color.FromArgb(0xCC, 0x11, 0x11, 0x11));
    private static readonly Brush SuccessBackground = new SolidColorBrush(Color.FromArgb(0xCC, 0x14, 0x53, 0x2D));
    private static readonly Brush FailureBackground = new SolidColorBrush(Color.FromArgb(0xCC, 0x7F, 0x1D, 0x1D));

    private DispatcherTimer? _flashTimer;

    public void SetState(State state)
    {
        CancelFlash();
        Shell.Background = IdleBackground;
        switch (state)
        {
            case State.Transcribing:
                StateLabel.Text = "Transcribing";
                LevelBars.Visibility = Visibility.Collapsed;
                break;
            default:
                StateLabel.Text = "Listening";
                LevelBars.Visibility = Visibility.Visible;
                ResetBars();
                break;
        }
    }

    /// <summary>
    /// Briefly reuse the recording pill to confirm an outcome that produced no
    /// visible paste — a task capture lands in a folder, so without this the
    /// dictation appears to vanish. Auto-hides; safe to call from any thread.
    /// </summary>
    public void Flash(string text, bool success, TimeSpan duration)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => Flash(text, success, duration)));
            return;
        }

        CancelFlash();
        StateLabel.Text = text;
        LevelBars.Visibility = Visibility.Collapsed;
        Shell.Background = success ? SuccessBackground : FailureBackground;
        Show();

        _flashTimer = new DispatcherTimer { Interval = duration };
        _flashTimer.Tick += (_, _) =>
        {
            CancelFlash();
            Shell.Background = IdleBackground;
            Hide();
        };
        _flashTimer.Start();
    }

    private void CancelFlash()
    {
        if (_flashTimer is null) return;
        _flashTimer.Stop();
        _flashTimer = null;
    }

    private const double BarMaxHeight = 18;
    private const double BarMinHeight = 2;

    private void ResetBars()
    {
        Bar0.Height = Bar1.Height = Bar2.Height = Bar3.Height = Bar4.Height = BarMinHeight;
    }

    /// <summary>
    /// Set live level bars from normalized [0,1] RMS values (one per bar).
    /// Safe to call from any thread — marshals onto the dispatcher.
    /// </summary>
    public void SetLevels(float[] levels)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => SetLevels(levels)));
            return;
        }
        if (levels is null || levels.Length == 0) return;

        var bars = new[] { Bar0, Bar1, Bar2, Bar3, Bar4 };
        for (int i = 0; i < bars.Length; i++)
        {
            var v = i < levels.Length ? Math.Clamp(levels[i], 0f, 1f) : 0f;
            bars[i].Height = BarMinHeight + v * (BarMaxHeight - BarMinHeight);
        }
    }

    // Upstream Handy uses the monitor under the cursor (not the foreground
    // window) and positions via the full monitor rect (not the work area),
    // with fixed pixel offsets from the top/bottom edges. See overlay.rs in
    // the Rust crate — these offsets are load-bearing for multi-monitor and
    // DPI-scaled setups where work_area returns surprising values.
    private const double OverlayTopOffsetPx    = 4;
    private const double OverlayBottomOffsetPx = 40;

    /// <summary>
    /// Position the overlay on the monitor the user is looking at (cursor
    /// position wins). "None" hides it.
    /// </summary>
    public void ApplyPosition(string position)
    {
        if (string.Equals(position, "None", StringComparison.OrdinalIgnoreCase))
        {
            Hide();
            return;
        }

        var mon = GetActiveMonitorRect();

        // Convert pixel rect to WPF DIPs for this window's DPI.
        var src = PresentationSource.FromVisual(this);
        var m = src?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var tl = m.Transform(new Point(mon.Left, mon.Top));
        var br = m.Transform(new Point(mon.Right, mon.Bottom));
        var monLeft   = tl.X;
        var monTop    = tl.Y;
        var monWidth  = br.X - tl.X;
        var monHeight = br.Y - tl.Y;

        Left = monLeft + (monWidth - ActualWidth) / 2;
        Top = string.Equals(position, "Bottom", StringComparison.OrdinalIgnoreCase)
            ? monTop + monHeight - ActualHeight - OverlayBottomOffsetPx
            : monTop + OverlayTopOffsetPx;

        // Upstream re-asserts HWND_TOPMOST explicitly — WPF's Topmost is not
        // reliable against full-screen apps and certain elevated windows.
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    // Monitor under the cursor — matches upstream's get_monitor_with_cursor.
    private static NativeMethods.RECT GetActiveMonitorRect()
    {
        IntPtr monitor = IntPtr.Zero;
        if (NativeMethods.GetCursorPos(out var pt))
            monitor = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTONEAREST);

        var info = new NativeMethods.MONITORINFO { cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (monitor != IntPtr.Zero && NativeMethods.GetMonitorInfo(monitor, ref info))
            return info.rcMonitor;

        return new NativeMethods.RECT
        {
            Left = 0,
            Top = 0,
            Right = (int)SystemParameters.PrimaryScreenWidth,
            Bottom = (int)SystemParameters.PrimaryScreenHeight,
        };
    }
}
