using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace GestionStock.App.Views;

/// <summary>Convertit une couleur "#RRGGBB" en SolidColorBrush pour l'affichage (pastilles produit, etc.).</summary>
public class HexToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string hex && !string.IsNullOrWhiteSpace(hex))
        {
            try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
            catch (FormatException) { /* couleur invalide : on retombe sur le gris neutre */ }
        }
        return new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Fond de ligne légèrement teinté avec la couleur "#RRGGBB" (produit), sans gêner la lecture. Transparent sans couleur.</summary>
public class HexToRowBackgroundConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string hex && !string.IsNullOrWhiteSpace(hex))
        {
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(hex);
                return new SolidColorBrush(Color.FromArgb(0x22, c.R, c.G, c.B));
            }
            catch (FormatException) { /* couleur invalide : pas de teinte */ }
        }
        return Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
