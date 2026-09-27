using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace GestionStock.App.Services;

/// <summary>Impression simple d'une liste sous forme de tableau, réutilisée par toutes les pages.</summary>
public static class PrintHelper
{
    public static void PrintTable(string title, IReadOnlyList<string> headers, IReadOnlyList<string[]> rows,
        IReadOnlyList<(string Label, string Value)>? totals = null)
    {
        var dialog = new PrintDialog();
        if (dialog.ShowDialog() != true) return;

        var doc = new FlowDocument
        {
            PagePadding = new Thickness(24),
            ColumnWidth = dialog.PrintableAreaWidth,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 11,
        };

        doc.Blocks.Add(new Paragraph(new Run(title)) { FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 2) });
        doc.Blocks.Add(new Paragraph(new Run($"Imprimé le {DateTime.Now:dd/MM/yyyy à HH:mm} — {rows.Count} ligne(s)"))
        {
            FontSize = 10, Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 14),
        });

        var table = new Table();
        foreach (var _ in headers) table.Columns.Add(new TableColumn());
        var group = new TableRowGroup();
        table.RowGroups.Add(group);

        var headerRow = new TableRow { Background = Brushes.WhiteSmoke };
        foreach (var h in headers) headerRow.Cells.Add(Cell(h, bold: true));
        group.Rows.Add(headerRow);

        foreach (var row in rows)
        {
            var tr = new TableRow();
            foreach (var value in row) tr.Cells.Add(Cell(value));
            group.Rows.Add(tr);
        }
        doc.Blocks.Add(table);

        if (totals is { Count: > 0 })
        {
            var totalsParagraph = new Paragraph { TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            foreach (var (label, value) in totals)
            {
                if (totalsParagraph.Inlines.Count > 0) totalsParagraph.Inlines.Add(new LineBreak());
                totalsParagraph.Inlines.Add(new Run($"{label} : "));
                totalsParagraph.Inlines.Add(new Run(value) { FontWeight = FontWeights.SemiBold });
            }
            doc.Blocks.Add(totalsParagraph);
        }

        IDocumentPaginatorSource source = doc;
        dialog.PrintDocument(source.DocumentPaginator, title);
    }

    /// <summary>
    /// Imprime un bon (livraison, encaissement ou achat) : titre et numéro, informations du tiers, lignes, puis totaux.
    /// </summary>
    public static void PrintBon(string title, string number, DateTime date, IReadOnlyList<(string Label, string Value)> info,
        IReadOnlyList<string> headers, IReadOnlyList<string[]> rows, IReadOnlyList<(string Label, string Value)> totals)
    {
        var dialog = new PrintDialog();
        if (dialog.ShowDialog() != true) return;

        var doc = new FlowDocument
        {
            PagePadding = new Thickness(40),
            ColumnWidth = dialog.PrintableAreaWidth,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
        };

        doc.Blocks.Add(new Paragraph(new Run($"{title} N° {number}")) { FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 2) });
        doc.Blocks.Add(new Paragraph(new Run($"Date : {date:dd/MM/yyyy à HH:mm}")) { Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 14) });

        var infoParagraph = new Paragraph { Margin = new Thickness(0, 0, 0, 16) };
        foreach (var (label, value) in info.Where(i => !string.IsNullOrWhiteSpace(i.Value)))
        {
            if (infoParagraph.Inlines.Count > 0) infoParagraph.Inlines.Add(new LineBreak());
            infoParagraph.Inlines.Add(new Run($"{label} : ") { Foreground = Brushes.Gray });
            infoParagraph.Inlines.Add(new Run(value) { FontWeight = FontWeights.SemiBold });
        }
        doc.Blocks.Add(infoParagraph);

        if (rows.Count > 0)
        {
            var table = new Table { CellSpacing = 0 };
            foreach (var _ in headers) table.Columns.Add(new TableColumn());
            var group = new TableRowGroup();
            table.RowGroups.Add(group);
            var headerRow = new TableRow { Background = Brushes.WhiteSmoke };
            foreach (var h in headers) headerRow.Cells.Add(Cell(h, bold: true));
            group.Rows.Add(headerRow);
            foreach (var row in rows)
            {
                var tr = new TableRow();
                foreach (var value in row) tr.Cells.Add(Cell(value));
                group.Rows.Add(tr);
            }
            doc.Blocks.Add(table);
        }

        var totalsParagraph = new Paragraph { TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        foreach (var (label, value) in totals)
        {
            if (totalsParagraph.Inlines.Count > 0) totalsParagraph.Inlines.Add(new LineBreak());
            totalsParagraph.Inlines.Add(new Run($"{label} : "));
            totalsParagraph.Inlines.Add(new Run(value) { FontWeight = FontWeights.SemiBold });
        }
        doc.Blocks.Add(totalsParagraph);

        IDocumentPaginatorSource source = doc;
        dialog.PrintDocument(source.DocumentPaginator, $"{title} {number}");
    }

    private static TableCell Cell(string? text, bool bold = false) => new(new Paragraph(new Run(text ?? "")))
    {
        Padding = new Thickness(4, 3, 4, 3),
        BorderBrush = Brushes.LightGray,
        BorderThickness = new Thickness(0, 0, 0, 1),
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
    };
}
