using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using Handy.Services;

namespace Handy;

public partial class HistoryWindow : Window
{
    private HistoryService? _history;

    public HistoryWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _history = ((App)Application.Current).History;
            if (_history is not null)
            {
                _history.Changed += OnHistoryChanged;
                Reload();
            }
        };
    }

    private record Row(HistoryEntry Entry, string Timestamp, string Text);

    private void Reload()
    {
        if (_history is null) return;
        var rows = _history.Snapshot()
            .OrderByDescending(e => e.TimestampUtc)
            .Select(e => new Row(
                e,
                e.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                e.Text))
            .ToList();
        HistoryList.ItemsSource = rows;
    }

    private void OnHistoryChanged()
    {
        if (!Dispatcher.CheckAccess())
            Dispatcher.BeginInvoke(new Action(Reload));
        else
            Reload();
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is not Row row) return;
        try { Clipboard.SetText(row.Text); }
        catch (Exception ex) { Log.Warn($"Copy failed: {ex.Message}"); }
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is not Row row) return;
        _history?.Remove(row.Entry);
    }

    private void OnClearAll(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(this,
            "Delete every entry from the transcript history?",
            "Clear history", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result == MessageBoxResult.Yes) _history?.Clear();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_history is not null) _history.Changed -= OnHistoryChanged;
    }
}
