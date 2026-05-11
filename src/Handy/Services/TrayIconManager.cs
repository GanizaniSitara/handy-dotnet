using System;
using System.Drawing;
using System.Windows.Forms;
using WMedia = System.Windows.Media;

namespace Handy.Services;

/// <summary>
/// System tray icon via WinForms NotifyIcon, hosted inside a WPF app
/// (UseWindowsForms=true). Menu mirrors a subset of upstream: Settings,
/// Copy Last Transcript, Cancel, Quit. The icon is re-rendered with a
/// state-driven fill colour so the tray reflects Idle/Recording/Transcribing
/// at a glance.
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    // Same palette as the recording overlay so tray + overlay match visually.
    private static readonly WMedia.Color IdleColor         = WMedia.Color.FromRgb(0x58, 0x93, 0xDA); // blue
    private static readonly WMedia.Color RecordingColor    = WMedia.Color.FromRgb(0xE0, 0x40, 0x3D); // red
    private static readonly WMedia.Color TranscribingColor = WMedia.Color.FromRgb(0x43, 0xA0, 0x47); // green

    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _cancelItem;

    private System.Drawing.Icon? _currentIcon;
    private State _state = State.Idle;

    public TrayIconManager(
        Action onOpenSettings,
        Action onCopyLast,
        Action onOpenHistory,
        Action onCancel,
        Action onQuit)
    {
        _statusItem = new ToolStripMenuItem("Idle") { Enabled = false };
        _cancelItem = new ToolStripMenuItem("Cancel recording", null, (_, _) => onCancel())
        {
            Enabled = false,
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Settings",              null, (_, _) => onOpenSettings());
        menu.Items.Add("Copy last transcript",  null, (_, _) => onCopyLast());
        menu.Items.Add("History…",              null, (_, _) => onOpenHistory());
        menu.Items.Add(_cancelItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => onQuit());

        _currentIcon = IconAssets.RenderHandTrayIcon(IdleColor);
        _icon = new NotifyIcon
        {
            Icon = _currentIcon,
            Text = "Handy.NET — press hotkey to dictate",
            Visible = true,
            ContextMenuStrip = menu,
        };
        // Convention: single left-click opens settings; right-click shows the
        // context menu (handled automatically by ContextMenuStrip).
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) onOpenSettings();
        };
    }

    public enum State { Idle, Recording, Transcribing }

    public void SetState(State state)
    {
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
        _icon.Text = tooltip;
        _cancelItem.Enabled = cancelEnabled;
    }

    public void SetRecording(bool recording) =>
        SetState(recording ? State.Recording : State.Idle);

    public void Notify(string title, string body)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = body;
        _icon.ShowBalloonTip(3000);
    }

    private void SwapIcon(System.Drawing.Icon next)
    {
        var prev = _currentIcon;
        _currentIcon = next;
        _icon.Icon = next;
        // NotifyIcon does not own its icons; dispose the previous one ourselves
        // to release the underlying GDI handle. Without this, every state
        // transition leaks an HICON.
        prev?.Dispose();
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _currentIcon?.Dispose();
        _currentIcon = null;
    }
}
