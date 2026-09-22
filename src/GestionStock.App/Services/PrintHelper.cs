using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace GestionStock.App.Services;

/// <summary>Impression simple d'une liste sous forme de tableau, réutilisée par toutes les pages.</summary>
public static class PrintHelper
{
    public static void PrintTable(string title, IReadOnlyList<string> headers, IReadOnlyList<string[]> rows)
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

        IDocumentPaginatorSource source = doc;
        dialog.PrintDocument(source.DocumentPaginator, title);
    }

    private static TableCell Cell(string? text, bool bold = false) => new(new Paragraph(new Run(text ?? "")))
    {
        Padding = new Thickness(4, 3, 4, 3),
        BorderBrush = Brushes.LightGray,
        BorderThickness = new Thickness(0, 0, 0, 1),
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
    };
}
