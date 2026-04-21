using System;
using System.Drawing;
using System.Windows.Forms;

namespace Handy.Services;

/// <summary>
/// System tray icon via WinForms NotifyIcon, hosted inside a WPF app
/// (UseWindowsForms=true). Menu mirrors a subset of upstream: Settings,
/// Copy Last Transcript, Cancel, Quit.
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _cancelItem;

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

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Handy — press hotkey to dictate",
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

    public void SetRecording(bool recording)
    {
        _statusItem.Text = recording ? "Recording…" : "Idle";
        _cancelItem.Enabled = recording;
        _icon.Text = recording ? "Handy — recording" : "Handy — press hotkey to dictate";
    }

    public void Notify(string title, string body)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = body;
        _icon.ShowBalloonTip(3000);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
