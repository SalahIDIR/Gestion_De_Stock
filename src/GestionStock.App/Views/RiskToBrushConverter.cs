using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using GestionStock.App.ViewModels;

namespace GestionStock.App.Views;

/// <summary>Couleur associée au niveau de retard de paiement d'un client (voir <see cref="ClientRiskLevel"/>).</summary>
public class RiskToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        ClientRiskLevel.Critical => new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26)), // rouge
        ClientRiskLevel.Warning => new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)),  // orange
        _ => new SolidColorBrush(Color.FromRgb(0x1D, 0x29, 0x39)),                        // noir (normal)
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Fond de ligne très légèrement teinté pour les clients en retard ou critiques, sans gêner la lecture.</summary>
public class RiskToRowBackgroundConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        ClientRiskLevel.Critical => new SolidColorBrush(Color.FromArgb(0x18, 0xDC, 0x26, 0x26)),
        ClientRiskLevel.Warning => new SolidColorBrush(Color.FromArgb(0x18, 0xF5, 0x9E, 0x0B)),
        _ => Brushes.Transparent,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
