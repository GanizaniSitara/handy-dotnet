using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Handy.Services;

/// <summary>
/// Renders the bundled help markdown into a WPF FlowDocument.
///
/// Deliberately minimal: it handles exactly the constructs the shipped docs use
/// — headings, paragraphs, bullet lists, fenced code blocks, pipe tables, and
/// inline bold / code. It is not a general Markdown implementation and is not
/// meant to become one; anything richer belongs in a real library, and one help
/// page does not justify the dependency.
///
/// This replaces shelling the .md file with UseShellExecute, which fails on any
/// machine with no .md file association — i.e. a stock Windows install.
/// </summary>
internal static class MarkdownFlowDocument
{
    public static FlowDocument Build(string markdown, FrameworkElement resources)
    {
        var doc = new FlowDocument
        {
            FontFamily  = new FontFamily("Segoe UI"),
            FontSize    = 13,
            PagePadding = new Thickness(24, 18, 24, 24),
            Foreground  = Brush(resources, "Brush.Text", Colors.Black),
            Background  = Brush(resources, "Brush.Background", Colors.White),
        };

        var subtle = Brush(resources, "Brush.TextSubtle", Colors.DimGray);
        var border = Brush(resources, "Brush.BorderStrong", Colors.Gray);
        var card   = Brush(resources, "Brush.BackgroundCard", Colors.WhiteSmoke);

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var i = 0;

        while (i < lines.Length)
        {
            var line = lines[i];

            if (string.IsNullOrWhiteSpace(line)) { i++; continue; }

            // Fenced code block — consumed verbatim, no inline parsing inside.
            if (line.TrimStart().StartsWith("```"))
            {
                var code = new List<string>();
                i++;
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```"))
                {
                    code.Add(lines[i]);
                    i++;
                }
                i++; // closing fence
                doc.Blocks.Add(CodeBlock(string.Join(Environment.NewLine, code), card, border));
                continue;
            }

            // Pipe table. The separator row (|---|---|) is what distinguishes a
            // real table from a paragraph that happens to contain a pipe.
            if (line.TrimStart().StartsWith("|")
                && i + 1 < lines.Length && IsTableSeparator(lines[i + 1]))
            {
                var rows = new List<string[]> { SplitRow(line) };
                i += 2; // header + separator
                while (i < lines.Length && lines[i].TrimStart().StartsWith("|"))
                {
                    rows.Add(SplitRow(lines[i]));
                    i++;
                }
                doc.Blocks.Add(BuildTable(rows, border, subtle));
                continue;
            }

            // Headings.
            if (line.StartsWith("#"))
            {
                var level = line.TakeWhile(c => c == '#').Count();
                var text = line[level..].Trim();
                doc.Blocks.Add(new Paragraph(new Run(text))
                {
                    FontSize   = level == 1 ? 22 : level == 2 ? 16 : 14,
                    FontWeight = FontWeights.SemiBold,
                    Margin     = new Thickness(0, level == 1 ? 0 : 18, 0, 6),
                });
                i++;
                continue;
            }

            // Bullet list — consecutive "- " / "* " lines become one List block.
            if (IsBullet(line))
            {
                var list = new List { MarkerStyle = TextMarkerStyle.Disc, Margin = new Thickness(0, 4, 0, 8) };
                while (i < lines.Length && IsBullet(lines[i]))
                {
                    var item = lines[i].TrimStart()[2..].Trim();
                    list.ListItems.Add(new ListItem(Inline(item)));
                    i++;
                }
                doc.Blocks.Add(list);
                continue;
            }

            // Paragraph — join wrapped lines until a blank or a new block starts.
            var para = new List<string>();
            while (i < lines.Length
                   && !string.IsNullOrWhiteSpace(lines[i])
                   && !lines[i].StartsWith("#")
                   && !IsBullet(lines[i])
                   && !lines[i].TrimStart().StartsWith("|")
                   && !lines[i].TrimStart().StartsWith("```"))
            {
                para.Add(lines[i].Trim());
                i++;
            }
            var p = Inline(string.Join(" ", para));
            p.Margin = new Thickness(0, 0, 0, 8);
            doc.Blocks.Add(p);
        }

        return doc;
    }

    private static bool IsBullet(string line)
    {
        var t = line.TrimStart();
        return t.StartsWith("- ") || t.StartsWith("* ");
    }

    private static bool IsTableSeparator(string line)
    {
        var t = line.Trim();
        return t.StartsWith("|") && t.Contains('-') && t.All(c => c is '|' or '-' or ':' or ' ');
    }

    private static string[] SplitRow(string line)
    {
        var t = line.Trim();
        if (t.StartsWith("|")) t = t[1..];
        if (t.EndsWith("|")) t = t[..^1];
        return t.Split('|').Select(c => c.Trim()).ToArray();
    }

    private static Table BuildTable(List<string[]> rows, Brush border, Brush subtle)
    {
        var columns = rows.Max(r => r.Length);
        var table = new Table
        {
            CellSpacing = 0,
            Margin = new Thickness(0, 4, 0, 12),
        };
        for (var c = 0; c < columns; c++)
            table.Columns.Add(new TableColumn());

        var group = new TableRowGroup();
        table.RowGroups.Add(group);

        for (var r = 0; r < rows.Count; r++)
        {
            var row = new TableRow();
            for (var c = 0; c < columns; c++)
            {
                var text = c < rows[r].Length ? rows[r][c] : string.Empty;
                var cellPara = Inline(text);
                cellPara.Margin = new Thickness(0);
                if (r == 0) cellPara.FontWeight = FontWeights.SemiBold;

                row.Cells.Add(new TableCell(cellPara)
                {
                    BorderBrush     = border,
                    BorderThickness = new Thickness(0, 0, 0, r == 0 ? 1 : 0.5),
                    Padding         = new Thickness(8, 5, 8, 5),
                    Foreground      = r == 0 ? null : subtle,
                });
            }
            group.Rows.Add(row);
        }
        return table;
    }

    private static Paragraph CodeBlock(string code, Brush background, Brush border)
    {
        return new Paragraph(new Run(code))
        {
            FontFamily      = new FontFamily("Consolas"),
            FontSize        = 12,
            Background      = background,
            BorderBrush     = border,
            BorderThickness = new Thickness(1),
            Padding         = new Thickness(10, 8, 10, 8),
            Margin          = new Thickness(0, 4, 0, 12),
        };
    }

    /// <summary>
    /// Handles **bold** and `code` spans. Anything else is emitted literally —
    /// better a stray asterisk than a mangled sentence.
    /// </summary>
    private static Paragraph Inline(string text)
    {
        var para = new Paragraph();
        var buffer = string.Empty;

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '*' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (end > 0)
                {
                    Flush(para, ref buffer);
                    para.Inlines.Add(new Run(text[(i + 2)..end]) { FontWeight = FontWeights.SemiBold });
                    i = end + 1;
                    continue;
                }
            }
            if (text[i] == '`')
            {
                var end = text.IndexOf('`', i + 1);
                if (end > 0)
                {
                    Flush(para, ref buffer);
                    para.Inlines.Add(new Run(text[(i + 1)..end])
                    {
                        FontFamily = new FontFamily("Consolas"),
                        FontSize   = 12,
                    });
                    i = end;
                    continue;
                }
            }
            buffer += text[i];
        }
        Flush(para, ref buffer);
        return para;
    }

    private static void Flush(Paragraph para, ref string buffer)
    {
        if (buffer.Length == 0) return;
        para.Inlines.Add(new Run(buffer));
        buffer = string.Empty;
    }

    private static Brush Brush(FrameworkElement source, string key, Color fallback)
    {
        try
        {
            if (source.TryFindResource(key) is Brush b) return b;
        }
        catch { }
        return new SolidColorBrush(fallback);
    }
}
