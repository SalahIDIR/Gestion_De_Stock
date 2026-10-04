using System.Collections.ObjectModel;
using System.IO.Ports;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GestionStock.Core.Domain;
using GestionStock.Core.Services;

namespace GestionStock.App.ViewModels;

/// <summary>Ligne du tableau de routage : un opérateur, son port COM et sa requête USSD.</summary>
public partial class OperatorRouting : ObservableObject
{
    public OperatorRouting(Operator op)
    {
        Operator = op;
        _comPort = op.ComPort ?? "";
        _ussdTemplate = op.UssdTemplate ?? "";
        _confirmKeystroke = op.ConfirmKeystroke ?? "";
        _successKeyword = op.SuccessKeyword ?? "";
        _confirmationViaSms = op.ConfirmationViaSms;
        _balanceUssdCode = op.BalanceUssdCode ?? "";
        _hiLinkHost = op.HiLinkHost ?? "";
    }

    public Operator Operator { get; }
    public string Name => Operator.Name;

    [ObservableProperty] private string _comPort;
    [ObservableProperty] private string _ussdTemplate;
    /// <summary>Chiffre renvoyé pour confirmer le transfert quand le réseau demande confirmation (ex. « 1 »).</summary>
    [ObservableProperty] private string _confirmKeystroke;
    /// <summary>Mot trouvé dans le message final qui indique que le transfert a réussi.</summary>
    [ObservableProperty] private string _successKeyword;
    /// <summary>La confirmation réelle arrive par SMS séparé (ex. Mobilis) plutôt que dans la session USSD elle-même.</summary>
    [ObservableProperty] private bool _confirmationViaSms;
    /// <summary>Code USSD sans confirmation qui renvoie le solde de crédit disponible sur la puce (ex. *766#).</summary>
    [ObservableProperty] private string _balanceUssdCode;
    /// <summary>Adresse IP du modem en mode HiLink (ex. 192.168.8.1), si ce modem n'a pas de port COM.</summary>
    [ObservableProperty] private string _hiLinkHost;
}

public partial class SettingsViewModel : ViewModelBase
{
    private readonly SettingsService _settings;
    private readonly AuthService _auth;
    private readonly DemoDataService _demo;
    private readonly CreditTransferService _creditTransfer;
    private readonly User _user;

    public SettingsViewModel(SettingsService settings, AuthService auth, DemoDataService demo, CreditTransferService creditTransfer, User user)
    {
        _settings = settings;
        _auth = auth;
        _demo = demo;
        _creditTransfer = creditTransfer;
        _user = user;

        try { foreach (var port in SerialPort.GetPortNames().Order()) AvailablePorts.Add(port); }
        catch { /* La liste des ports détectés est une aide : la saisie manuelle reste possible. */ }

        _ = LoadAsync();
    }

    public ObservableCollection<OperatorRouting> Routing { get; } = new();
    public ObservableCollection<string> AvailablePorts { get; } = new();

    [ObservableProperty] private string _companyEmail = "";
    [ObservableProperty] private DateTime? _inventoryStart;
    [ObservableProperty] private DateTime? _inventoryEnd;

    [ObservableProperty] private string _currentPassword = "";
    [ObservableProperty] private string _newPassword = "";
    [ObservableProperty] private string _confirmNewPassword = "";

    public string PortsHint => AvailablePorts.Count == 0
        ? "Aucun port COM détecté. Branchez les modems puis rouvrez cette page."
        : $"Ports détectés : {string.Join(", ", AvailablePorts)}";

    private async Task LoadAsync()
    {
        await TryAsync(async () =>
        {
            var s = await _settings.GetAsync();
            CompanyEmail = s.CompanyEmail;
            InventoryStart = s.InventoryStart;
            InventoryEnd = s.InventoryEnd;

            Routing.Clear();
            foreach (var op in await _settings.GetOperatorsAsync()) Routing.Add(new OperatorRouting(op));
        });
    }

    [RelayCommand]
    private async Task SaveGeneralAsync()
    {
        var ok = await TryAsync(() => _settings.SaveAsync(new AppSettings
        {
            CompanyEmail = CompanyEmail, InventoryStart = InventoryStart, InventoryEnd = InventoryEnd,
        }));
        if (ok) Info("Paramètres enregistrés.");
    }

    [RelayCommand]
    private async Task SaveRoutingAsync(OperatorRouting? row)
    {
        if (row == null) return;
        var ok = await TryAsync(() => _settings.SaveOperatorRoutingAsync(row.Operator.Id, row.ComPort, row.UssdTemplate,
            row.ConfirmKeystroke, row.SuccessKeyword, row.ConfirmationViaSms, row.BalanceUssdCode, row.HiLinkHost));
        if (ok) Info($"Routage de {row.Name} enregistré.");
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestConnectionCommand))]
    private bool _isTestingConnection;

    private bool CanTestConnection() => !IsTestingConnection;

    /// <summary>Interroge le solde de chaque puce configurée pour vérifier que le modem et le routage répondent.</summary>
    [RelayCommand(CanExecute = nameof(CanTestConnection))]
    private async Task TestConnectionAsync()
    {
        IsTestingConnection = true;
        List<CreditTransferService.BalanceResult>? results = null;
        try { await TryAsync(async () => results = await _creditTransfer.CheckBalancesAsync()); }
        finally { IsTestingConnection = false; }

        if (results != null)
            Info("Test de connexion :\n\n" + string.Join("\n\n",
                results.Select(r => $"{(r.Success ? "✓" : "✗")} {r.OperatorName} : {r.Message}")));
    }

    [RelayCommand]
    private async Task ChangePasswordAsync()
    {
        if (NewPassword != ConfirmNewPassword) { Info("Les deux nouveaux mots de passe sont différents."); return; }
        var ok = await TryAsync(() => _auth.ChangePasswordAsync(_user.Id, CurrentPassword, NewPassword));
        if (!ok) return;
        CurrentPassword = NewPassword = ConfirmNewPassword = "";
        Info("Mot de passe modifié.");
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateDemoDataCommand))]
    private bool _isGeneratingDemo;

    private bool CanGenerateDemoData() => !IsGeneratingDemo;

    [RelayCommand(CanExecute = nameof(CanGenerateDemoData))]
    private async Task GenerateDemoDataAsync()
    {
        if (!Confirm("Ajouter 300 clients, 5 fournisseurs et environ 3 mois d'opérations (achats, livraisons, encaissements) " +
                     "à la base actuelle ?\n\nCes données ne pourront pas être supprimées automatiquement.")) return;

        IsGeneratingDemo = true;
        DemoDataResult? result = null;
        try { await TryAsync(async () => result = await _demo.GenerateAsync()); }
        finally { IsGeneratingDemo = false; }

        if (result != null)
            Info($"Données de démonstration ajoutées :\n{result.Clients} clients, {result.Suppliers} fournisseurs,\n" +
                 $"{result.Purchases} bons d'achat, {result.Deliveries} bons de livraison, {result.Encashments} encaissements.");
    }
}
