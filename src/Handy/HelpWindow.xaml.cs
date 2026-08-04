using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Documents;
using Handy.Services;

namespace Handy;

/// <summary>
/// Displays a bundled help document in-app.
///
/// Previously the help button shelled the raw .md file with UseShellExecute,
/// which throws on any machine without a .md file association — the default on
/// a stock Windows install — so the user got an error dialog instead of help.
/// </summary>
public partial class HelpWindow : Window
{
    public HelpWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Opens <paramref name="docFileName"/> from the shipped docs folder. Returns
    /// false if the document could not be found, so the caller can report it
    /// rather than showing an empty window.
    /// </summary>
    public static bool TryShow(Window? owner, string docFileName, string title)
    {
        var path = ResolveDocPath(docFileName);
        if (path is null) return false;

        string markdown;
        try { markdown = File.ReadAllText(path); }
        catch (Exception ex)
        {
            Log.Warn($"Help: could not read {path}: {ex.Message}");
            return false;
        }

        var window = new HelpWindow { Title = $"Handy.NET — {title}" };
        if (owner is not null && !ReferenceEquals(owner, window)) window.Owner = owner;
        window.Viewer.Document = MarkdownFlowDocument.Build(markdown, window);
        window.Show();
        return true;
    }

    /// <summary>
    /// Docs sit next to the executable in a published build, and four levels up
    /// in a dev checkout. Same candidate order the old shell-out used.
    /// </summary>
    public static string? ResolveDocPath(string docFileName)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "docs", docFileName),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "docs", docFileName)),
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}
