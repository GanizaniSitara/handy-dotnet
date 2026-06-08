using System;
using System.Drawing;
using System.Windows.Forms;
using WMedia = System.Windows.Media;

namespace Handy.Services;

/// <summary>
/// System tray icon via WinForms NotifyIcon, hosted inside a WPF app
/// (UseWindowsForms=true). Menu mirrors a subset of upstream: Settings,
/// Copy Last Transcript, Cancel, Exit. The icon is re-rendered with a
/// state-driven fill colour so the tray reflects Idle/Recording/Transcribing
/// at a glance.
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    private static readonly Guid TrayIconGuid = new("8F38F0A5-D738-40B6-9BE5-740ACD9A7C73");

    // Same palette as the recording overlay so tray + overlay match visually.
    private static readonly WMedia.Color IdleColor         = WMedia.Color.FromRgb(0x58, 0x93, 0xDA); // blue
    private static readonly WMedia.Color RecordingColor    = WMedia.Color.FromRgb(0xE0, 0x40, 0x3D); // red
    private static readonly WMedia.Color TranscribingColor = WMedia.Color.FromRgb(0x43, 0xA0, 0x47); // green

    private readonly GuidTrayIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _cancelItem;

    private System.Drawing.Icon? _currentIcon;
    private State _state = State.Idle;
    private bool _disposed;

    public TrayIconManager(
        Action onOpenSettings,
        Action onCopyLast,
        Action onOpenHistory,
        Action onCancel,
        Action onExit)
    {
        _statusItem = new ToolStripMenuItem("Idle") { Enabled = false };
        _cancelItem = new ToolStripMenuItem("Cancel recording", null, (_, _) => onCancel())
        {
            Enabled = false,
        };

        _menu = new ContextMenuStrip();
        _menu.Items.Add(_statusItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Settings",              null, (_, _) => onOpenSettings());
        _menu.Items.Add("Copy last transcript",  null, (_, _) => onCopyLast());
        _menu.Items.Add("History…",              null, (_, _) => onOpenHistory());
        _menu.Items.Add(_cancelItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Exit", null, (_, _) => onExit());

        _currentIcon = IconAssets.RenderHandTrayIcon(IdleColor);
        _icon = new GuidTrayIcon(
            TrayIconGuid,
            _currentIcon,
            "Handy.NET — press hotkey to dictate");

        // Convention: single left-click opens settings; right-click shows the
        // context menu.
        _icon.LeftClick += (_, _) => onOpenSettings();
        _icon.RightClick += (_, _) => _icon.ShowContextMenu(_menu);
    }

    public enum State { Idle, Recording, Transcribing }

    public void SetState(State state)
    {
        if (_disposed) return;
        if (state == _state) return;
        _state = state;

        WMedia.Color color;
        string status, tooltip;
        bool cancelEnabled;
        switch (state)
        {
            case State.Recording:
                color = RecordingColor;
                status = "Recording…";
                tooltip = "Handy.NET — recording";
                cancelEnabled = true;
                break;
            case State.Transcribing:
                color = TranscribingColor;
                status = "Transcribing…";
                tooltip = "Handy.NET — transcribing";
                cancelEnabled = false;
                break;
            default:
                color = IdleColor;
                status = "Idle";
                tooltip = "Handy.NET — press hotkey to dictate";
                cancelEnabled = false;
                break;
        }

        SwapIcon(IconAssets.RenderHandTrayIcon(color));
        _statusItem.Text = status;
        _icon.UpdateTooltip(tooltip);
        _cancelItem.Enabled = cancelEnabled;
    }

    public void SetRecording(bool recording) =>
        SetState(recording ? State.Recording : State.Idle);

    public void Notify(string title, string body)
    {
        if (_disposed) return;
        _icon.ShowBalloon(title, body);
    }

    private void SwapIcon(System.Drawing.Icon next)
    {
        var prev = _currentIcon;
        _currentIcon = next;
        _icon.UpdateIcon(next);
        // GuidTrayIcon does not own its icons; dispose the previous one ourselves
        // to release the underlying GDI handle. Without this, every state
        // transition leaks an HICON.
        prev?.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _icon.Dispose();
        _menu.Dispose();
        _currentIcon?.Dispose();
        _currentIcon = null;
    }
}
