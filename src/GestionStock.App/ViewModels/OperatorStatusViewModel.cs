using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GestionStock.Core.Services;

namespace GestionStock.App.ViewModels;

/// <summary>État de la liaison avec le modem d'un opérateur.</summary>
public enum LinkState
{
    Testing,
    Online,
    Offline,
}

/// <summary>Un opérateur et l'état de son modem, affiché par une pastille de couleur.</summary>
public partial class OperatorLinkItem : ObservableObject
{
    public OperatorLinkItem(string name) => Name = name;

    public string Name { get; }

    [ObservableProperty] private LinkState _state = LinkState.Testing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private string _message = "Test en cours…";

    /// <summary>Texte de l'infobulle : utile quand le menu est replié et que seule la pastille reste visible.</summary>
    public string Summary => $"{Name} : {Message}";
}

/// <summary>
/// État partagé (singleton) de la connexion aux 3 opérateurs : testé au démarrage par la fenêtre de vérification,
/// puis affiché en permanence dans le menu de la fenêtre principale, d'où il peut être retesté.
/// </summary>
public partial class OperatorStatusViewModel : ObservableObject
{
    private readonly CreditTransferService _creditTransfer;

    public OperatorStatusViewModel(CreditTransferService creditTransfer)
    {
        _creditTransfer = creditTransfer;
        // Pas de test pendant un envoi de crédit : il attendrait de toute façon la fin de l'envoi sur ce modem.
        creditTransfer.SendingChanged += (_, _) =>
            Application.Current.Dispatcher.BeginInvoke(RefreshCommand.NotifyCanExecuteChanged);
    }

    public ObservableCollection<OperatorLinkItem> Operators { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyPropertyChangedFor(nameof(AllOnline), nameof(HasProblem))]
    private bool _isTesting;

    public bool AllOnline => !IsTesting && Operators.Count > 0 && Operators.All(o => o.State == LinkState.Online);

    public bool HasProblem => !IsTesting && !AllOnline;

    private bool CanRefresh() => !IsTesting && !_creditTransfer.IsSending;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    public async Task RefreshAsync()
    {
        if (IsTesting) return;
        IsTesting = true;
        foreach (var op in Operators)
        {
            op.State = LinkState.Testing;
            op.Message = "Test en cours…";
        }

        try
        {
            var results = await _creditTransfer.CheckConnectionsAsync();
            if (!results.Select(r => r.OperatorName).SequenceEqual(Operators.Select(o => o.Name)))
            {
                Operators.Clear();
                foreach (var r in results) Operators.Add(new OperatorLinkItem(r.OperatorName));
            }
            for (var i = 0; i < results.Count; i++)
            {
                Operators[i].State = results[i].Online ? LinkState.Online : LinkState.Offline;
                Operators[i].Message = results[i].Message;
            }
        }
        catch (Exception ex)
        {
            foreach (var op in Operators)
            {
                op.State = LinkState.Offline;
                op.Message = $"Test impossible : {ex.Message}";
            }
        }
        finally
        {
            IsTesting = false;
        }
    }
}
