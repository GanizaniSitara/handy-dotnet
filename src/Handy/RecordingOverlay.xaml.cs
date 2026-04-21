using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Handy;

public partial class RecordingOverlay : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW  = 0x00000080;
    private const int WS_EX_NOACTIVATE  = 0x08000000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

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

    /// <summary>
    /// Position the overlay at the top or bottom of the primary screen.
    /// "None" hides it. Anything else defaults to top.
    /// </summary>
    public void ApplyPosition(string position)
    {
        if (string.Equals(position, "None", StringComparison.OrdinalIgnoreCase))
        {
            Hide();
            return;
        }

        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - Width) / 2;
        Top = string.Equals(position, "Bottom", StringComparison.OrdinalIgnoreCase)
            ? area.Bottom - Height - 24
            : area.Top + 24;
    }
}
