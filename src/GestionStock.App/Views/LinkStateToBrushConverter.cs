using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using GestionStock.App.ViewModels;

namespace GestionStock.App.Views;

/// <summary>Couleur de la pastille d'un opérateur selon l'état de son modem (voir <see cref="LinkState"/>).</summary>
public class LinkStateToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        LinkState.Online => new SolidColorBrush(Color.FromRgb(0x12, 0xB7, 0x6A)),  // vert
        LinkState.Offline => new SolidColorBrush(Color.FromRgb(0xF0, 0x44, 0x38)), // rouge
        _ => new SolidColorBrush(Color.FromRgb(0x98, 0xA2, 0xB3)),                 // gris (test en cours)
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
