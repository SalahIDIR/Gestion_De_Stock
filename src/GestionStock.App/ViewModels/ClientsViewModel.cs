using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GestionStock.Core.Domain;
using GestionStock.Core.Services;

namespace GestionStock.App.ViewModels;

/// <summary>Une ligne du formulaire : le numéro de puce du client pour un opérateur.</summary>
public partial class ChipEditor : ObservableObject
{
    public ChipEditor(Operator op) => Operator = op;

    public Operator Operator { get; }
    public string OperatorName => Operator.Name;

    [ObservableProperty] private string _number = "";
}

public record ClientRow(Client Client, decimal Debt)
{
    public string Name => Client.Name;
    public string? City => Client.City;
    public string? Phone => Client.Phone;
    public decimal CreditLimit => Client.CreditLimit;
}

public partial class ClientsViewModel : ViewModelBase
{
    private readonly ClientService _service;
    private readonly DeliveryService _deliveries;
    private List<Client> _all = new();
    private Dictionary<int, decimal> _debts = new();

    public ClientsViewModel(ClientService service, SettingsService settings, DeliveryService deliveries)
    {
        _service = service;
        _deliveries = deliveries;
        _ = InitializeAsync(settings);
    }

    public ObservableCollection<ClientRow> Items { get; } = new();
    public ObservableCollection<ChipEditor> Chips { get; } = new();

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _onlyDebtors;
    [ObservableProperty] private ClientRow? _selected;
    [ObservableProperty] private int _editId;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _address = "";
    [ObservableProperty] private string _city = "";
    [ObservableProperty] private string _phone = "";
    [ObservableProperty] private string _creditLimit = "";
    [ObservableProperty] private decimal _selectedDebt;
    [ObservableProperty] private string _paymentAmount = "";
    [ObservableProperty] private string _paymentNote = "";
    [ObservableProperty] private decimal _totalDisplayedDebt;

    public string FormTitle => EditId == 0 ? "Nouveau client" : "Modifier le client";
    public bool HasSelection => EditId != 0;

    partial void OnEditIdChanged(int value)
    {
        OnPropertyChanged(nameof(FormTitle));
        OnPropertyChanged(nameof(HasSelection));
    }

    partial void OnSearchChanged(string value) => ApplyFilter();
    partial void OnOnlyDebtorsChanged(bool value) => ApplyFilter();

    partial void OnSelectedChanged(ClientRow? value)
    {
        if (value == null) return;
        var client = value.Client;
        EditId = client.Id;
        Name = client.Name;
        Address = client.Address ?? "";
        City = client.City ?? "";
        Phone = client.Phone ?? "";
        CreditLimit = client.CreditLimit == 0 ? "" : client.CreditLimit.ToString("0.##");
        SelectedDebt = value.Debt;
        PaymentAmount = PaymentNote = "";
        foreach (var chip in Chips)
            chip.Number = client.Chips.FirstOrDefault(c => c.OperatorId == chip.Operator.Id)?.PhoneNumber ?? "";
    }

    private async Task InitializeAsync(SettingsService settings)
    {
        await TryAsync(async () =>
        {
            foreach (var op in await settings.GetOperatorsAsync()) Chips.Add(new ChipEditor(op));
            await LoadAsync();
        });
    }

    private async Task LoadAsync()
    {
        _all = await _service.ListAsync();
        _debts = await _deliveries.GetDebtsAsync();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var term = Search.Trim();
        var rows = _all
            .Where(c => term.Length == 0
                || c.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase)
                || (c.City?.Contains(term, StringComparison.CurrentCultureIgnoreCase) ?? false)
                || (c.Address?.Contains(term, StringComparison.CurrentCultureIgnoreCase) ?? false))
            .Select(c => new ClientRow(c, _debts.GetValueOrDefault(c.Id)))
            .Where(r => !OnlyDebtors || r.Debt > 0)
            .ToList();

        Items.Clear();
        foreach (var r in rows) Items.Add(r);
        TotalDisplayedDebt = rows.Sum(r => r.Debt);
    }

    [RelayCommand]
    private void New()
    {
        Selected = null;
        EditId = 0;
        Name = Address = City = Phone = CreditLimit = "";
        SelectedDebt = 0;
        PaymentAmount = PaymentNote = "";
        foreach (var chip in Chips) chip.Number = "";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var limit = string.IsNullOrWhiteSpace(CreditLimit) ? 0m : ParseDecimal(CreditLimit);
        if (limit == null)
        {
            Info("Le plafond de crédit n'est pas un nombre valide.");
            return;
        }

        var client = new Client
        {
            Id = EditId, Name = Name, Address = Address, City = City, Phone = Phone, CreditLimit = limit.Value,
        };
        foreach (var chip in Chips)
            client.Chips.Add(new ClientChip { OperatorId = chip.Operator.Id, PhoneNumber = chip.Number });

        if (!await TryAsync(async () => await _service.SaveAsync(client))) return;
        New();
        await TryAsync(LoadAsync);
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (EditId == 0 || !Confirm($"Supprimer le client « {Name} » et ses puces ?")) return;
        if (await TryAsync(() => _service.DeleteAsync(EditId)))
        {
            New();
            await TryAsync(LoadAsync);
        }
    }

    [RelayCommand]
    private async Task AddPaymentAsync()
    {
        if (EditId == 0) { Info("Sélectionnez d'abord un client."); return; }
        var amount = ParseDecimal(PaymentAmount);
        if (amount is not > 0) { Info("Saisissez le montant encaissé (un nombre positif)."); return; }
        if (!Confirm($"Encaisser {amount:N2} DA de « {Name} » ?")) return;

        var id = EditId;
        if (!await TryAsync(() => _deliveries.AddPaymentAsync(id, DateTime.Now, amount.Value, PaymentNote))) return;
        await TryAsync(LoadAsync);
        Selected = Items.FirstOrDefault(r => r.Client.Id == id);
        if (Selected == null) SelectedDebt = _debts.GetValueOrDefault(id);
    }
}
