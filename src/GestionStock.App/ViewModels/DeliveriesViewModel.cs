using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GestionStock.App.Services;
using GestionStock.Core.Domain;
using GestionStock.Core.Services;

namespace GestionStock.App.ViewModels;

/// <summary>Ligne en cours de saisie dans le formulaire de bon de livraison.</summary>
public partial class DeliveryLineEditor : ObservableObject
{
    [ObservableProperty] private Product? _product;
    [ObservableProperty] private string _quantityText = "";
    [ObservableProperty] private string _unitPriceText = "";
    [ObservableProperty] private ClientChip? _selectedChip;

    public ObservableCollection<ClientChip> AvailableChips { get; } = new();

    public decimal? Quantity => ViewModelBase.ParseDecimal(QuantityText);
    public decimal? UnitPrice => ViewModelBase.ParseDecimal(UnitPriceText);

    /// <summary>Montant facturé, ou null tant que la saisie est incomplète.</summary>
    public decimal? Total => Quantity is > 0 && UnitPrice is > 0
        ? DeliveryService.ComputeLineTotal(Quantity.Value, UnitPrice.Value)
        : null;

    public string StockText => Product == null ? "" : Product.StockBalance.ToString("N2");

    public string Hint => Product?.Kind switch
    {
        ProductKind.VirtualCredit => "Montant de crédit (DA) × coefficient de vente",
        ProductKind.Physical => "Quantité × prix unitaire",
        _ => "",
    };

    partial void OnProductChanged(Product? value)
    {
        OnPropertyChanged(nameof(Hint));
        OnPropertyChanged(nameof(StockText));
    }

    partial void OnQuantityTextChanged(string value) => OnPropertyChanged(nameof(Total));
    partial void OnUnitPriceTextChanged(string value) => OnPropertyChanged(nameof(Total));
}

public record DeliveryRow(DeliveryNote Note)
{
    public string Number => Note.Number;
    public DateTime Date => Note.Date;
    public string ClientName => Note.Client?.Name ?? "";
    public bool IsPaymentOnly => Note.Lines.Count == 0;
    public string TypeLabel => IsPaymentOnly ? "Encaissement" : "Vente";
    public decimal Total => Note.Total;
    public decimal Remaining => Note.Remaining;
}

public record DeliveryLineRow(DeliveryLine Line)
{
    public string ProductName => Line.Product?.Name ?? "";
    public decimal Quantity => Line.Quantity;
    public decimal UnitPrice => Line.UnitPrice;
    public decimal LineTotal => Line.LineTotal;
    public string? RecipientPhone => Line.RecipientPhone;
}

public partial class DeliveriesViewModel : ViewModelBase
{
    /// <summary>Nombre maximal de clients proposés dans la liste déroulante (on affine avec la recherche).</summary>
    private const int MaxClientChoices = 100;

    private readonly DeliveryService _deliveries;
    private readonly ClientService _clients;
    private readonly ProductService _products;
    private List<Client> _allClients = new();
    private List<DeliveryNote> _allNotes = new();
    private Dictionary<int, decimal> _lastPrices = new();
    private bool _rebuildingChoices;

    public DeliveriesViewModel(DeliveryService deliveries, ClientService clients, ProductService products)
    {
        _deliveries = deliveries;
        _clients = clients;
        _products = products;
        Lines.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
                foreach (DeliveryLineEditor l in e.NewItems)
                {
                    l.PropertyChanged += (_, args) =>
                    {
                        if (args.PropertyName == nameof(DeliveryLineEditor.Product))
                        {
                            PrefillPrice(l);
                            PopulateChips(l);
                        }
                        RefreshTotals();
                    };
                }
            RefreshTotals();
        };
        AddLine();
        _ = InitializeAsync();
    }

    public ObservableCollection<DeliveryRow> History { get; } = new();
    public ObservableCollection<DeliveryLineRow> SelectedDetails { get; } = new();
    public ObservableCollection<Client> ClientChoices { get; } = new();
    public ObservableCollection<Product> Products { get; } = new();
    public ObservableCollection<DeliveryLineEditor> Lines { get; } = new();

    [ObservableProperty] private string _historySearch = "";
    [ObservableProperty] private DateTime? _historyFrom;
    [ObservableProperty] private DateTime? _historyTo;
    [ObservableProperty] private string _historyAmountMin = "";
    [ObservableProperty] private string _historyAmountMax = "";
    [ObservableProperty] private DeliveryRow? _selectedDelivery;

    [ObservableProperty] private string _clientSearch = "";
    [ObservableProperty] private Client? _client;
    [ObservableProperty] private DateTime _deliveryDate = DateTime.Today;
    [ObservableProperty] private DeliveryLineEditor? _selectedLine;
    [ObservableProperty] private string _paidText = "";
    [ObservableProperty] private decimal _clientDebt;
    [ObservableProperty] private bool _isPaymentOnly;

    public decimal Total => IsPaymentOnly ? 0m : Lines.Sum(l => l.Total ?? 0m);
    public decimal Remaining => Total - (ParseDecimal(PaidText) ?? 0m);
    public decimal NewDebt => ClientDebt + Remaining;
    public bool OverLimit => !IsPaymentOnly && Client is { CreditLimit: > 0 } c && NewDebt > c.CreditLimit;
    public string PaidLabel => IsPaymentOnly ? "Montant encaissé (DA) *" : "Montant encaissé maintenant (DA)";
    public bool IsProductMode => !IsPaymentOnly;

    public string ClientChoicesHint => _allClients.Count > MaxClientChoices && ClientSearch.Trim().Length == 0
        ? $"{_allClients.Count} clients : tapez un nom ou une ville pour filtrer."
        : "";

    public string ClientInfo
    {
        get
        {
            if (Client == null) return "";
            var limit = Client.CreditLimit > 0 ? $"{Client.CreditLimit:N2} DA" : "aucun";
            var chips = string.Join(" · ", Client.Chips
                .OrderBy(c => c.OperatorId).ThenBy(c => c.Slot)
                .GroupBy(c => c.OperatorId)
                .Select(g => $"{g.First().Operator?.Name ?? "Op." + g.Key} " + string.Join(" / ", g.Select(c => c.PhoneNumber))));
            return $"Dette actuelle : {ClientDebt:N2} DA · Plafond : {limit}" + (chips.Length > 0 ? $"\nPuces : {chips}" : "\nAucune puce enregistrée");
        }
    }

    partial void OnPaidTextChanged(string value) => RefreshTotals();
    partial void OnClientSearchChanged(string value) => RebuildClientChoices();
    partial void OnHistorySearchChanged(string value) => ApplyHistoryFilter();
    partial void OnHistoryFromChanged(DateTime? value) => ApplyHistoryFilter();
    partial void OnHistoryToChanged(DateTime? value) => ApplyHistoryFilter();
    partial void OnHistoryAmountMinChanged(string value) => ApplyHistoryFilter();
    partial void OnHistoryAmountMaxChanged(string value) => ApplyHistoryFilter();

    partial void OnIsPaymentOnlyChanged(bool value)
    {
        OnPropertyChanged(nameof(PaidLabel));
        OnPropertyChanged(nameof(IsProductMode));
        RefreshTotals();
    }

    partial void OnClientChanged(Client? value)
    {
        if (_rebuildingChoices) return; // la ComboBox vide sa sélection le temps de la reconstruction
        OnPropertyChanged(nameof(ClientInfo));
        foreach (var line in Lines) PopulateChips(line);
        _ = LoadClientContextAsync(value);
    }

    partial void OnSelectedDeliveryChanged(DeliveryRow? value)
    {
        SelectedDetails.Clear();
        if (value == null) return;
        foreach (var l in value.Note.Lines) SelectedDetails.Add(new DeliveryLineRow(l));
    }

    private void RefreshTotals()
    {
        OnPropertyChanged(nameof(Total));
        OnPropertyChanged(nameof(Remaining));
        OnPropertyChanged(nameof(NewDebt));
        OnPropertyChanged(nameof(OverLimit));
    }

    private async Task LoadClientContextAsync(Client? client)
    {
        if (client == null)
        {
            ClientDebt = 0;
            _lastPrices = new();
            OnPropertyChanged(nameof(ClientInfo));
            RefreshTotals();
            return;
        }

        await TryAsync(async () =>
        {
            var debt = await _deliveries.GetClientDebtAsync(client.Id);
            var prices = await _deliveries.GetLastPricesAsync(client.Id);
            if (Client?.Id != client.Id) return; // l'utilisateur a changé de client entre-temps
            ClientDebt = debt;
            _lastPrices = prices;
            foreach (var line in Lines) PrefillPrice(line);
            OnPropertyChanged(nameof(ClientInfo));
            RefreshTotals();
        });
    }

    /// <summary>Propose le tarif courant de ce client pour ce produit, si la case est vide.</summary>
    private void PrefillPrice(DeliveryLineEditor line)
    {
        if (line.Product == null || !string.IsNullOrWhiteSpace(line.UnitPriceText)) return;
        if (_lastPrices.TryGetValue(line.Product.Id, out var price))
            line.UnitPriceText = price.ToString("0.####");
    }

    /// <summary>Remplit le choix de puce destinataire selon l'opérateur du produit et les puces du client sélectionné.</summary>
    private void PopulateChips(DeliveryLineEditor line)
    {
        line.AvailableChips.Clear();
        if (Client != null && line.Product?.OperatorId is int operatorId)
            foreach (var chip in Client.Chips.Where(c => c.OperatorId == operatorId).OrderBy(c => c.Slot))
                line.AvailableChips.Add(chip);
        line.SelectedChip = line.AvailableChips.FirstOrDefault();
    }

    private async Task InitializeAsync()
    {
        await TryAsync(async () =>
        {
            _allClients = await _clients.ListAsync();
            RebuildClientChoices();
            await ReloadProductsAndHistoryAsync();
        });
    }

    private async Task ReloadProductsAndHistoryAsync()
    {
        Products.Clear();
        foreach (var p in await _products.ListAsync()) Products.Add(p);
        _allNotes = await _deliveries.ListAsync();
        ApplyHistoryFilter();
    }

    private void ApplyHistoryFilter()
    {
        var term = HistorySearch.Trim();
        var min = ParseDecimal(HistoryAmountMin);
        var max = ParseDecimal(HistoryAmountMax);

        var rows = _allNotes.Where(n => term.Length == 0
                || n.Number.Contains(term, StringComparison.CurrentCultureIgnoreCase)
                || (n.Client?.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase) ?? false))
            .Where(n => HistoryFrom == null || n.Date.Date >= HistoryFrom.Value.Date)
            .Where(n => HistoryTo == null || n.Date.Date <= HistoryTo.Value.Date)
            .Where(n => min == null || n.Total >= min)
            .Where(n => max == null || n.Total <= max);

        History.Clear();
        foreach (var n in rows) History.Add(new DeliveryRow(n));
    }

    private void RebuildClientChoices()
    {
        var term = ClientSearch.Trim();
        var matches = _allClients
            .Where(c => term.Length == 0
                || c.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase)
                || (c.City?.Contains(term, StringComparison.CurrentCultureIgnoreCase) ?? false))
            .Take(MaxClientChoices)
            .ToList();

        // Le client déjà choisi doit rester dans la liste, sinon WPF efface la sélection.
        var selected = Client;
        if (selected != null && matches.All(c => c.Id != selected.Id)) matches.Insert(0, selected);

        _rebuildingChoices = true;
        try
        {
            ClientChoices.Clear();
            foreach (var c in matches) ClientChoices.Add(c);
            if (selected != null) Client = ClientChoices.FirstOrDefault(c => c.Id == selected.Id);
        }
        finally
        {
            _rebuildingChoices = false;
        }
        OnPropertyChanged(nameof(ClientChoicesHint));
    }

    [RelayCommand]
    private void AddLine() => Lines.Add(new DeliveryLineEditor());

    [RelayCommand]
    private void RemoveLine()
    {
        if (SelectedLine != null) Lines.Remove(SelectedLine);
        if (Lines.Count == 0) AddLine();
    }

    private void ResetForm()
    {
        Client = null;
        ClientSearch = "";
        DeliveryDate = DateTime.Today;
        PaidText = "";
        IsPaymentOnly = false;
        Lines.Clear();
        AddLine();
    }

    [RelayCommand]
    private void Reset() => ResetForm();

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (Client == null) { Info("Sélectionnez un client."); return; }

        var paid = string.IsNullOrWhiteSpace(PaidText) ? 0m : ParseDecimal(PaidText);
        if (paid == null) { Info("Le montant encaissé n'est pas un nombre valide."); return; }

        List<DeliveryLineInput> lines = [];
        if (IsPaymentOnly)
        {
            if (paid is not > 0) { Info("Saisissez le montant encaissé (un nombre positif)."); return; }
        }
        else
        {
            foreach (var (line, index) in Lines.Select((l, i) => (l, i + 1)))
            {
                if (line.Product == null) { Info($"Ligne {index} : choisissez un produit."); return; }
                if (line.Quantity is not > 0) { Info($"Ligne {index} : le montant ou la quantité n'est pas valide."); return; }
                if (line.UnitPrice is not > 0) { Info($"Ligne {index} : le coefficient ou prix n'est pas valide."); return; }
                lines.Add(new DeliveryLineInput(line.Product.Id, line.Quantity.Value, line.UnitPrice.Value, line.SelectedChip?.PhoneNumber));
            }
        }

        DeliveryNote? saved = null;
        var ok = await TryAsync(async () =>
            saved = await _deliveries.CreateAsync(new DeliveryInput(Client.Id, DeliveryDate, lines, paid.Value)));
        if (!ok || saved == null) return;

        var clientName = Client.Name;
        var wasPaymentOnly = IsPaymentOnly;
        ResetForm();
        await TryAsync(ReloadProductsAndHistoryAsync);
        Info(wasPaymentOnly
            ? $"Bon d'encaissement {saved.Number} enregistré pour « {clientName} » ({saved.AmountPaid:N2} DA)."
            : $"Bon de livraison {saved.Number} enregistré pour « {clientName} » ({saved.Total:N2} DA, reste à payer {saved.Remaining:N2} DA).\nLe stock a été mis à jour.");
    }

    [RelayCommand]
    private void Print()
    {
        PrintHelper.PrintTable("Bons de livraison et encaissements",
            ["N°", "Date", "Type", "Client", "Total (DA)", "Reste (DA)"],
            History.Select(r => new[] { r.Number, r.Date.ToString("dd/MM/yyyy"), r.TypeLabel, r.ClientName,
                r.Total.ToString("N2"), r.Remaining.ToString("N2") }).ToList());
    }
}
