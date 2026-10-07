using System.ComponentModel;
using System.Windows;
using GestionStock.App.ViewModels;
using GestionStock.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace GestionStock.App.Views;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    /// <summary>Fermer pendant un envoi de crédit couperait la session USSD en plein milieu : on demande d'abord.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (App.Services.GetRequiredService<CreditTransferService>().IsSending
            && MessageBox.Show("Un envoi de crédit est en cours. Si vous fermez maintenant, son résultat restera incertain.\n\nFermer quand même ?",
                "Gestion Stock", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            e.Cancel = true;
        base.OnClosing(e);
    }
}
