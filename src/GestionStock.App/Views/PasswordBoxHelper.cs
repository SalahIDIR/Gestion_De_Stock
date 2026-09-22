using System.Windows;
using System.Windows.Controls;

namespace GestionStock.App.Views;

/// <summary>Permet de lier un PasswordBox à une propriété string d'un ViewModel.</summary>
public static class PasswordBoxHelper
{
    public static readonly DependencyProperty BoundPasswordProperty = DependencyProperty.RegisterAttached(
        "BoundPassword", typeof(string), typeof(PasswordBoxHelper),
        // Défaut null (et non "") : le premier binding vers "" doit déclencher le callback qui branche PasswordChanged.
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnBoundPasswordChanged));

    private static readonly DependencyProperty UpdatingProperty =
        DependencyProperty.RegisterAttached("Updating", typeof(bool), typeof(PasswordBoxHelper));

    public static string? GetBoundPassword(DependencyObject d) => (string?)d.GetValue(BoundPasswordProperty);
    public static void SetBoundPassword(DependencyObject d, string value) => d.SetValue(BoundPasswordProperty, value);

    private static void OnBoundPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox box) return;
        box.PasswordChanged -= OnPasswordChanged;
        if (!(bool)box.GetValue(UpdatingProperty) && box.Password != (string?)e.NewValue)
            box.Password = (string?)e.NewValue ?? string.Empty;
        box.PasswordChanged += OnPasswordChanged;
    }

    private static void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        var box = (PasswordBox)sender;
        box.SetValue(UpdatingProperty, true);
        SetBoundPassword(box, box.Password);
        box.SetValue(UpdatingProperty, false);
    }
}
