using System.Collections.ObjectModel;
using System.IO.Ports;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GestionStock.Puces.Modem;

namespace GestionStock.Puces.ViewModels;

public partial class MainViewModel : ObservableObject
{
    public MainViewModel()
    {
        Ports.Clear();
        foreach (var name in SerialPort.GetPortNames().OrderBy(n => n)) Ports.Add(name);
        _selectedPort = Ports.FirstOrDefault();
    }

    public ObservableCollection<string> Ports { get; } = new();
    public ObservableCollection<SmsMessage> Messages { get; } = new();

    [ObservableProperty] private string? _selectedPort;
    [ObservableProperty] private string _balanceCode = "*632*01*00000#";
    [ObservableProperty] private string _balanceResult = "";
    [ObservableProperty] private string _status = "Choisissez le port du modem, puis testez la connexion.";
    [ObservableProperty] private bool _isBusy;

    partial void OnSelectedPortChanged(string? value) => NotifyCommandsChanged();
    partial void OnIsBusyChanged(bool value) => NotifyCommandsChanged();

    private void NotifyCommandsChanged()
    {
        TestConnectionCommand.NotifyCanExecuteChanged();
        CheckBalanceCommand.NotifyCanExecuteChanged();
        ReadMessagesCommand.NotifyCanExecuteChanged();
    }

    private bool CanUseModem() => !IsBusy && SelectedPort != null;

    [RelayCommand]
    private void RefreshPorts()
    {
        Ports.Clear();
        foreach (var name in SerialPort.GetPortNames().OrderBy(n => n)) Ports.Add(name);
        if (SelectedPort == null || !Ports.Contains(SelectedPort)) SelectedPort = Ports.FirstOrDefault();
    }

    [RelayCommand(CanExecute = nameof(CanUseModem))]
    private async Task TestConnectionAsync()
    {
        var message = await OnModemAsync("Test du modem", client =>
            client.Command("AT", TimeSpan.FromSeconds(5)).Contains("OK")
                ? "Le modem répond OK."
                : "Le modem ne répond pas.");
        if (message != null) Status = message;
    }

    [RelayCommand(CanExecute = nameof(CanUseModem))]
    private async Task CheckBalanceAsync()
    {
        var code = BalanceCode.Trim();
        var message = await OnModemAsync("Consultation du solde", client =>
        {
            var reply = client.Ussd(code, TimeSpan.FromSeconds(20));
            return reply is { } r ? r.Text : "Aucune réponse du modem.";
        });
        if (message != null) BalanceResult = message;
    }

    [RelayCommand(CanExecute = nameof(CanUseModem))]
    private async Task ReadMessagesAsync()
    {
        var list = await OnModemAsync("Lecture des SMS", client => client.ReadSms());
        if (list == null) return;

        Messages.Clear();
        foreach (var message in list.OrderByDescending(m => m.Date)) Messages.Add(message);
        Status = list.Count == 0 ? "Aucun SMS sur la carte SIM." : $"{list.Count} SMS lus sur la carte SIM.";
    }

    /// <summary>Ouvre le port, exécute l'action hors du thread d'interface, et affiche l'erreur éventuelle.</summary>
    private async Task<T?> OnModemAsync<T>(string label, Func<AtClient, T> work) where T : class
    {
        IsBusy = true;
        Status = $"{label} en cours…";
        try
        {
            var port = SelectedPort!;
            return await Task.Run(() =>
            {
                using var client = new AtClient(port);
                return work(client);
            });
        }
        catch (Exception ex)
        {
            Status = $"Erreur sur {SelectedPort} : {ex.Message}";
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
