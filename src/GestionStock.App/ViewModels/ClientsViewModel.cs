using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GestionStock.App.Services;
using GestionStock.Core.Domain;
using GestionStock.Core.Services;

namespace GestionStock.App.ViewModels;

public enum ClientRiskLevel { Normal, Warning, Critical }

/// <summary>Une ligne du formulaire : les 1 ou 2 numéros de puce du client pour un opérateur.</summary>
public partial class ChipEditor : ObservableObject
{
    public ChipEditor(Operator op) => Operator = op;

    public Operator Operator { get; }
    public string OperatorName => Operator.Name;

    [ObservableProperty] private string _number1 = "";
    [ObservableProperty] private string _number2 = "";
    [ObservableProperty] private bool _showSecond;

    public bool ShowAddButton => !ShowSecond;
    partial void OnShowSecondChanged(bool value) => OnPropertyChanged(nameof(ShowAddButton));

    public void Reset()
    {
        Number1 = "";
        Number2 = "";
        ShowSecond = false;
    }

    public void LoadFrom(IEnumerable<ClientChip> chips)
    {
        Number1 = chips.FirstOrDefault(c => c.Slot == 1)?.PhoneNumber ?? "";
        var slot2 = chips.FirstOrDefault(c => c.Slot == 2);
        Number2 = slot2?.PhoneNumber ?? "";
        ShowSecond = slot2 != null;
    }
}

public record ClientRow(Client Client, decimal Debt, ClientRiskLevel Risk, int? DaysSincePayment)
{
    public string Name => Client.Name;
    public string? City => Client.City;
    public string? Phone => Client.Phone;
    public decimal CreditLimit => Client.CreditLimit;
}

public partial class ClientsViewModel : ViewModelBase
{
    /// <summary>Une dette non réglée depuis ce nombre de jours passe en orange.</summary>
    public const int WarningDays = 7;
    /// <summary>Une dette non réglée depuis ce nombre de jours passe en rouge.</summary>
    public const int CriticalDays = 10;

    private readonly ClientService _service;
    private readonly DeliveryService _deliveries;
    private List<Client> _all = new();
    private Dictionary<int, decimal> _debts = new();
    private Dictionary<int, DateTime> _activity = new();

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
    [ObservableProperty] private string _debtMin = "";
    [ObservableProperty] private string _debtMax = "";
    [ObservableProperty] private ClientRow? _selected;
    [ObservableProperty] private int _editId;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _address = "";
    [ObservableProperty] private string _city = "";
    [ObservableProperty] private string _phone = "";
    [ObservableProperty] private string _creditLimit = "";
    [ObservableProperty] private decimal _selectedDebt;
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
    partial void OnDebtMinChanged(string value) => ApplyFilter();
    partial void OnDebtMaxChanged(string value) => ApplyFilter();

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
        foreach (var chip in Chips) chip.LoadFrom(client.Chips.Where(c => c.OperatorId == chip.Operator.Id));
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
        _activity = await _deliveries.GetLastActivityDatesAsync();
        ApplyFilter();
    }

    private (ClientRiskLevel Risk, int? Days) ComputeRisk(int clientId, decimal debt)
    {
        if (debt <= 0 || !_activity.TryGetValue(clientId, out var last)) return (ClientRiskLevel.Normal, null);
        var days = (DateTime.Today - last.Date).Days;
        var risk = days >= CriticalDays ? ClientRiskLevel.Critical : days >= WarningDays ? ClientRiskLevel.Warning : ClientRiskLevel.Normal;
        return (risk, days);
    }

    private void ApplyFilter()
    {
        var term = Search.Trim();
        var min = ParseDecimal(DebtMin);
        var max = ParseDecimal(DebtMax);

        var rows = _all
            .Where(c => term.Length == 0
                || c.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase)
                || (c.City?.Contains(term, StringComparison.CurrentCultureIgnoreCase) ?? false)
                || (c.Address?.Contains(term, StringComparison.CurrentCultureIgnoreCase) ?? false))
            .Select(c =>
            {
                var debt = _debts.GetValueOrDefault(c.Id);
                var (risk, days) = ComputeRisk(c.Id, debt);
                return new ClientRow(c, debt, risk, days);
            })
            .Where(r => !OnlyDebtors || r.Debt > 0)
            .Where(r => min == null || r.Debt >= min)
            .Where(r => max == null || r.Debt <= max)
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
        foreach (var chip in Chips) chip.Reset();
    }

    [RelayCommand]
    private void AddSecondNumber(ChipEditor? chip)
    {
        if (chip != null) chip.ShowSecond = true;
    }

    [RelayCommand]
    private void RemoveSecondNumber(ChipEditor? chip)
    {
        if (chip == null) return;
        chip.Number2 = "";
        chip.ShowSecond = false;
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
        {
            if (!string.IsNullOrWhiteSpace(chip.Number1))
                client.Chips.Add(new ClientChip { OperatorId = chip.Operator.Id, PhoneNumber = chip.Number1, Slot = 1 });
            if (!string.IsNullOrWhiteSpace(chip.Number2))
                client.Chips.Add(new ClientChip { OperatorId = chip.Operator.Id, PhoneNumber = chip.Number2, Slot = 2 });
        }

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
    private void Print()
    {
        PrintHelper.PrintTable("Liste des clients",
            ["Nom", "Ville", "Téléphone", "Plafond (DA)", "Dette (DA)"],
            Items.Select(r => new[] { r.Name, r.City ?? "", r.Phone ?? "", r.CreditLimit.ToString("N0"), r.Debt.ToString("N2") }).ToList());
    }
}
