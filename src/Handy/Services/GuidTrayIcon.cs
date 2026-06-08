using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Handy.Services;

internal sealed class GuidTrayIcon : NativeWindow, IDisposable
{
    private static readonly int TaskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

    private const int CallbackMessage = 0x8000 + 0x048; // WM_APP + 72

    private const uint NIM_ADD        = 0x00000000;
    private const uint NIM_MODIFY     = 0x00000001;
    private const uint NIM_DELETE     = 0x00000002;
    private const uint NIM_SETVERSION = 0x00000004;

    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON    = 0x00000002;
    private const uint NIF_TIP     = 0x00000004;
    private const uint NIF_INFO    = 0x00000010;
    private const uint NIF_GUID    = 0x00000020;
    private const uint NIF_SHOWTIP = 0x00000080;

    private const uint NOTIFYICON_VERSION_4 = 4;
    private const uint NIIF_INFO = 0x00000001;

    private const int WM_LBUTTONUP  = 0x0202;
    private const int WM_RBUTTONUP  = 0x0205;
    private const int WM_CONTEXTMENU = 0x007B;
    private const int NIN_SELECT    = 0x0400;
    private const int NIN_KEYSELECT = 0x0401;

    private readonly Guid _id;
    private Icon _icon;
    private string _tooltip;
    private bool _visible;
    private bool _disposed;

    public GuidTrayIcon(Guid id, Icon icon, string tooltip)
    {
        _id = id;
        _icon = icon;
        _tooltip = Trim(tooltip, 127);

        CreateHandle(new CreateParams { Caption = "Handy.NET tray icon" });
        Add();
    }

    public event EventHandler? LeftClick;
    public event EventHandler? RightClick;

    public void UpdateIcon(Icon icon)
    {
        if (_disposed) return;
        _icon = icon;
        if (!Modify(NIF_ICON))
            Log.Warn("Tray icon update failed.");
    }

    public void UpdateTooltip(string tooltip)
    {
        if (_disposed) return;
        _tooltip = Trim(tooltip, 127);
        if (!Modify(NIF_TIP))
            Log.Warn("Tray tooltip update failed.");
    }

    public void ShowBalloon(string title, string body)
    {
        if (_disposed || !_visible) return;

        var data = CreateData(NIF_INFO);
        data.szInfoTitle = Trim(title, 63);
        data.szInfo = Trim(body, 255);
        data.dwInfoFlags = NIIF_INFO;
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    public void ShowContextMenu(ContextMenuStrip menu)
    {
        if (_disposed) return;

        SetForegroundWindow(Handle);
        menu.Show(Cursor.Position);
    }

    protected override void WndProc(ref Message m)
    {
        if (TaskbarCreatedMessage != 0 && m.Msg == TaskbarCreatedMessage)
        {
            Add();
            return;
        }

        if (m.Msg == CallbackMessage)
        {
            var eventCode = unchecked((int)((long)m.LParam & 0xffff));
            if (eventCode is WM_LBUTTONUP or NIN_SELECT or NIN_KEYSELECT)
            {
                LeftClick?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (eventCode is WM_RBUTTONUP or WM_CONTEXTMENU)
            {
                RightClick?.Invoke(this, EventArgs.Empty);
                return;
            }
        }

        base.WndProc(ref m);
    }

    private void Add()
    {
        if (_disposed) return;

        Delete();

        var data = CreateData(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        if (!Shell_NotifyIcon(NIM_ADD, ref data))
        {
            var addError = Marshal.GetLastWin32Error();
            if (!Shell_NotifyIcon(NIM_MODIFY, ref data))
            {
                Log.Warn($"Tray icon add failed (error={addError}, modifyError={Marshal.GetLastWin32Error()}).");
                _visible = false;
                return;
            }
        }

        _visible = true;

        data = CreateData(0);
        data.uTimeoutOrVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIcon(NIM_SETVERSION, ref data);
    }

    private bool Modify(uint flags)
    {
        if (!_visible) return false;
        var data = CreateData(flags);
        return Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private void Delete()
    {
        var data = CreateData(0);
        Shell_NotifyIcon(NIM_DELETE, ref data);
        _visible = false;
    }

    private NotifyIconData CreateData(uint flags)
    {
        return new NotifyIconData
        {
            cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
            hWnd = Handle,
            uID = 1,
            uFlags = flags | NIF_GUID,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon.Handle,
            szTip = _tooltip,
            szInfo = string.Empty,
            szInfoTitle = string.Empty,
            guidItem = _id,
        };
    }

    private static string Trim(string value, int maxChars)
    {
        value ??= string.Empty;
        return value.Length <= maxChars ? value : value[..maxChars];
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Delete();
        DestroyHandle();
        GC.SuppressFinalize(this);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NotifyIconData lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
