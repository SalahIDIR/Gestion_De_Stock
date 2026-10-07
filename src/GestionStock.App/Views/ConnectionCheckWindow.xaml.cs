using System.Windows;
using GestionStock.App.ViewModels;

namespace GestionStock.App.Views;

/// <summary>
/// Affichée après la connexion : teste les modems des 3 opérateurs. Si tous sont en ligne, la fenêtre se ferme
/// toute seule ; sinon le problème est affiché et l'utilisateur continue vers l'application quand il le souhaite.
/// </summary>
public partial class ConnectionCheckWindow : Window
{
    private static readonly TimeSpan AutoCloseDelay = TimeSpan.FromSeconds(1);

    public ConnectionCheckWindow(OperatorStatusViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Loaded += async (_, _) =>
        {
            await vm.RefreshAsync();
            if (!vm.AllOnline) return;
            await Task.Delay(AutoCloseDelay); // le temps de voir les pastilles vertes
            if (IsVisible) Close();
        };
    }

    private void Continue_Click(object sender, RoutedEventArgs e) => Close();
}
