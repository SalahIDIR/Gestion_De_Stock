using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GestionStock.Puces.Archive;
using GestionStock.Puces.Modem;

namespace GestionStock.Puces.ViewModels;

/// <summary>Résultat d'une lecture des SMS : les messages, et l'occupation du stockage pour avertir avant qu'il soit plein.</summary>
public record SmsReadResult(List<SmsMessage> Messages, (int Used, int Total)? Storage);

public partial class ModemsViewModel : ObservableObject
{
    private static readonly TimeSpan UssdTimeout = TimeSpan.FromSeconds(20);

    /// <summary>À partir de ce taux d'occupation, on avertit que la mémoire SMS risque de bloquer l'arrivée de nouveaux messages.</summary>
    private const double StorageWarningThreshold = 0.8;

    private readonly Action _onChangePorts;

    public ModemsViewModel(IEnumerable<OperatorChat> operators, Action onChangePorts)
    {
        _onChangePorts = onChangePorts;
        foreach (var op in operators) Operators.Add(op);
        _selectedOperator = Operators.FirstOrDefault();

        // Les SMS déjà archivés s'affichent tout de suite, même avant toute lecture de la SIM dans cette session.
        var archive = SmsArchiveStore.Load();
        foreach (var op in Operators) LoadArchiveInto(op, archive);
    }

    private static void LoadArchiveInto(OperatorChat op, List<ArchivedSms> archive)
    {
        var received = archive.Where(m => m.Operator == op.Name && !m.IsOutgoing).Select(m => (m.Phone, m.Date, m.Body));
        op.ReplaceReceivedMessages(received);
    }

    public ObservableCollection<OperatorChat> Operators { get; } = new();

    [ObservableProperty] private OperatorChat? _selectedOperator;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "";

    partial void OnSelectedOperatorChanged(OperatorChat? value) => NotifyCommandsChanged();
    partial void OnIsBusyChanged(bool value) => NotifyCommandsChanged();

    private void NotifyCommandsChanged()
    {
        ReadReceivedCommand.NotifyCanExecuteChanged();
        SendUssdCommand.NotifyCanExecuteChanged();
        CheckBalanceCommand.NotifyCanExecuteChanged();
        CloseUssdSessionCommand.NotifyCanExecuteChanged();
    }

    private bool CanUseModem() => !IsBusy && SelectedOperator?.Port != null;

    /// <summary>Teste silencieusement chaque opérateur ; le résultat est seulement visible par la pastille de couleur.</summary>
    public async Task TestConnectionsAsync()
    {
        IsBusy = true;
        try
        {
            foreach (var op in Operators)
            {
                if (op.Port == null)
                {
                    op.IsReachable = null;
                    continue;
                }
                var port = op.Port;
                op.IsReachable = await Task.Run(() => PingModem(port));
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseModem))]
    private async Task ReadReceivedAsync()
    {
        var op = SelectedOperator!;
        var result = await RunOnModemAsync(op, client => new SmsReadResult(client.ReadSms(), client.GetSimStorageUsage()));
        if (result == null) return;

        // Archivé avant affichage : une fois copiés ici, les messages restent visibles même si la SIM est vidée plus tard.
        var fresh = result.Messages.Select(m => new ArchivedSms(op.Name, m.Phone, m.Date, m.Body, m.IsOutgoing));
        var archive = SmsArchiveStore.Append(fresh);
        LoadArchiveInto(op, archive);

        var onSimCount = result.Messages.Count(m => !m.IsOutgoing);
        var baseStatus = onSimCount == 0 ? $"Aucun SMS reçu sur la SIM {op.Name}." : $"{onSimCount} SMS reçu(s) sur la SIM {op.Name}.";
        Status = AppendStorageWarning(baseStatus, result.Storage);
    }

    /// <summary>
    /// Ajoute un avertissement si la mémoire SMS de la SIM est presque pleine : au-delà, le réseau ne peut plus
    /// livrer de nouveau SMS (ex. le solde Mobilis), sans message d'erreur visible ailleurs que sur le modem lui-même.
    /// </summary>
    private static string AppendStorageWarning(string status, (int Used, int Total)? storage)
    {
        if (storage is not { } s || s.Total == 0 || (double)s.Used / s.Total < StorageWarningThreshold) return status;
        return $"{status} Attention : mémoire SMS de la SIM presque pleine ({s.Used}/{s.Total}) — les nouveaux SMS risquent de ne pas arriver tant qu'elle n'est pas libérée (AT+CMGD=1,4 pour tout effacer).";
    }

    [RelayCommand(CanExecute = nameof(CanUseModem))]
    private async Task SendUssdAsync()
    {
        var op = SelectedOperator!;
        var code = op.UssdCode.Trim();
        if (code.Length == 0) return;
        await SendUssdCodeAsync(op, code);
        op.UssdCode = "";
    }

    [RelayCommand(CanExecute = nameof(CanUseModem))]
    private async Task CheckBalanceAsync()
    {
        var op = SelectedOperator!;
        if (string.IsNullOrWhiteSpace(op.BalanceCode))
        {
            Status = $"Aucun code de solde configuré pour {op.Name}.";
            return;
        }
        await SendUssdCodeAsync(op, op.BalanceCode);
    }

    [RelayCommand]
    private void ChangePorts() => _onChangePorts();

    /// <summary>
    /// Ferme explicitement la session USSD en cours (ex. une réponse qui se termine par un menu, comme Djezzy avec
    /// « 1:Plus de detail Bonus »), pour pouvoir ensuite en ouvrir une nouvelle sans que le réseau ne la refuse.
    /// Rien n'est fermé automatiquement entre deux requêtes : certains menus se continuent justement en répondant
    /// par une nouvelle requête USSD (ex. "1"), donc fermer trop tôt casserait ce suivi.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUseModem))]
    private async Task CloseUssdSessionAsync()
    {
        var op = SelectedOperator!;
        var done = await RunOnModemAsync(op, client =>
        {
            client.CancelUssd();
            return "ok";
        });
        if (done != null) Status = $"Session USSD de {op.Name} fermée.";
    }

    private async Task SendUssdCodeAsync(OperatorChat op, string code)
    {
        var reply = await RunOnModemAsync(op, client => client.Ussd(code, UssdTimeout) is { } r ? r.Text : "Aucune réponse du modem.");
        if (reply != null) op.AddUssdExchange(code, reply);
    }

    private async Task<T?> RunOnModemAsync<T>(OperatorChat op, Func<AtClient, T> work) where T : class
    {
        IsBusy = true;
        Status = $"{op.Name} : en cours…";
        try
        {
            var port = op.Port!;
            return await Task.Run(() =>
            {
                using var client = new AtClient(port);
                return work(client);
            });
        }
        catch (Exception ex)
        {
            Status = $"{op.Name} : {ex.Message}";
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static bool PingModem(string port)
    {
        try
        {
            using var client = new AtClient(port);
            return client.Command("AT", TimeSpan.FromSeconds(5)).Any(line => line == "OK");
        }
        catch (Exception)
        {
            return false;
        }
    }
}
