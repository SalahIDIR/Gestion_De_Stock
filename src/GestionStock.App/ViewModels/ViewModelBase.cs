using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using GestionStock.Core.Services;

namespace GestionStock.App.ViewModels;

public abstract class ViewModelBase : ObservableObject
{
    /// <summary>Exécute une action asynchrone et affiche les erreurs à l'utilisateur au lieu de faire planter l'application.</summary>
    protected static async Task<bool> TryAsync(Func<Task> action)
    {
        try
        {
            await action();
            return true;
        }
        catch (BusinessException ex)
        {
            MessageBox.Show(ex.Message, "Gestion Stock", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Une erreur inattendue est survenue :\n{ex.Message}", "Gestion Stock",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        return false;
    }

    protected static bool Confirm(string message)
        => MessageBox.Show(message, "Gestion Stock", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    protected static void Info(string message)
        => MessageBox.Show(message, "Gestion Stock", MessageBoxButton.OK, MessageBoxImage.Information);

    /// <summary>Lit un nombre saisi avec une virgule ou un point (0,9725 ou 0.9725), espaces de milliers acceptés.</summary>
    public static decimal? ParseDecimal(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var normalized = text.Replace(" ", "").Replace(" ", "").Replace(" ", "").Replace(',', '.');
        return decimal.TryParse(normalized, System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}
