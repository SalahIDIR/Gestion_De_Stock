using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GestionStock.Puces.Modem;

namespace GestionStock.Puces.ViewModels;

public partial class ModemsViewModel : ObservableObject
{
    private static readonly TimeSpan UssdTimeout = TimeSpan.FromSeconds(20);

    private readonly Action _onChangePorts;

    public ModemsViewModel(IEnumerable<OperatorChat> operators, Action onChangePorts)
    {
        _onChangePorts = onChangePorts;
        foreach (var op in operators) Operators.Add(op);
        _selectedOperator = Operators.FirstOrDefault();
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
        var messages = await RunOnModemAsync(op, client => client.ReadSms());
        if (messages == null) return;

        var received = messages.Where(m => !m.IsOutgoing).ToList();
        op.ReplaceReceivedMessages(received);
        Status = received.Count == 0 ? $"Aucun SMS reçu sur la SIM {op.Name}." : $"{received.Count} SMS reçu(s) sur la SIM {op.Name}.";
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

    private async Task SendUssdCodeAsync(OperatorChat op, string code)
    {
        var reply = await RunOnModemAsync(op, client =>
            client.Ussd(code, UssdTimeout) is { } r ? r.Text : "Aucune réponse du modem.");
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
