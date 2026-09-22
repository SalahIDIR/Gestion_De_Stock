using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GestionStock.Core.Domain;
using GestionStock.Core.Services;

namespace GestionStock.App.ViewModels;

/// <summary>Ligne en cours de saisie dans le formulaire de bon d'achat.</summary>
public partial class PurchaseLineEditor : ObservableObject
{
    [ObservableProperty] private Product? _product;
    [ObservableProperty] private string _quantityText = "";
    [ObservableProperty] private string _unitCostText = "";

    public decimal? Quantity => ViewModelBase.ParseDecimal(QuantityText);
    public decimal? UnitCost => ViewModelBase.ParseDecimal(UnitCostText);

    /// <summary>Coût de la ligne, ou null tant que la saisie est incomplète.</summary>
    public decimal? Total => Quantity is > 0 && UnitCost is > 0
        ? PurchaseService.ComputeLineTotal(Quantity.Value, UnitCost.Value)
        : null;

    public string Hint => Product?.Kind switch
    {
        ProductKind.VirtualCredit => "Montant (DA) × coefficient",
        ProductKind.Physical => "Quantité × prix unitaire",
        _ => "",
    };

    partial void OnProductChanged(Product? value) => OnPropertyChanged(nameof(Hint));
    partial void OnQuantityTextChanged(string value) => OnPropertyChanged(nameof(Total));
    partial void OnUnitCostTextChanged(string value) => OnPropertyChanged(nameof(Total));
}

public record PurchaseRow(PurchaseOrder Order)
{
    public string Number => Order.Number;
    public DateTime Date => Order.Date;
    public string SupplierName => Order.Supplier?.CompanyName ?? "";
    public decimal Total => Order.Total;
    public decimal Remaining => Order.Remaining;
}

public record PurchaseLineRow(PurchaseLine Line)
{
    public string ProductName => Line.Product?.Name ?? "";
    public decimal Quantity => Line.Quantity;
    public decimal UnitCost => Line.UnitCost;
    public decimal LineTotal => Line.LineTotal;
}

public partial class PurchasesViewModel : ViewModelBase
{
    private readonly PurchaseService _purchases;
    private readonly SupplierService _suppliers;
    private readonly ProductService _products;

    public PurchasesViewModel(PurchaseService purchases, SupplierService suppliers, ProductService products)
    {
        _purchases = purchases;
        _suppliers = suppliers;
        _products = products;
        Lines.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null) foreach (PurchaseLineEditor l in e.NewItems) l.PropertyChanged += (_, _) => RefreshTotals();
            RefreshTotals();
        };
        AddLine();
        _ = InitializeAsync();
    }

    public ObservableCollection<PurchaseRow> History { get; } = new();
    public ObservableCollection<PurchaseLineRow> SelectedDetails { get; } = new();
    public ObservableCollection<Supplier> Suppliers { get; } = new();
    public ObservableCollection<Product> Products { get; } = new();
    public ObservableCollection<PurchaseLineEditor> Lines { get; } = new();

    [ObservableProperty] private PurchaseRow? _selectedPurchase;
    [ObservableProperty] private Supplier? _supplier;
    [ObservableProperty] private DateTime _purchaseDate = DateTime.Today;
    [ObservableProperty] private PurchaseLineEditor? _selectedLine;
    [ObservableProperty] private string _paidText = "";

    public decimal Total => Lines.Sum(l => l.Total ?? 0m);
    public decimal Remaining => Total - (ParseDecimal(PaidText) ?? 0m);

    partial void OnPaidTextChanged(string value) => RefreshTotals();

    partial void OnSelectedPurchaseChanged(PurchaseRow? value)
    {
        SelectedDetails.Clear();
        if (value == null) return;
        foreach (var l in value.Order.Lines) SelectedDetails.Add(new PurchaseLineRow(l));
    }

    private void RefreshTotals()
    {
        OnPropertyChanged(nameof(Total));
        OnPropertyChanged(nameof(Remaining));
    }

    private async Task InitializeAsync()
    {
        await TryAsync(async () =>
        {
            foreach (var s in await _suppliers.ListAsync()) Suppliers.Add(s);
            foreach (var p in await _products.ListAsync()) Products.Add(p);
            await LoadHistoryAsync();
        });
    }

    private async Task LoadHistoryAsync()
    {
        var orders = await _purchases.ListAsync();
        History.Clear();
        foreach (var o in orders) History.Add(new PurchaseRow(o));
    }

    [RelayCommand]
    private void AddLine() => Lines.Add(new PurchaseLineEditor());

    [RelayCommand]
    private void RemoveLine()
    {
        if (SelectedLine != null) Lines.Remove(SelectedLine);
        if (Lines.Count == 0) AddLine();
    }

    private void ResetForm()
    {
        Supplier = null;
        PurchaseDate = DateTime.Today;
        PaidText = "";
        Lines.Clear();
        AddLine();
    }

    [RelayCommand]
    private void Reset() => ResetForm();

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (Supplier == null) { Info("Sélectionnez un fournisseur."); return; }

        var lines = new List<PurchaseLineInput>();
        foreach (var (line, index) in Lines.Select((l, i) => (l, i + 1)))
        {
            if (line.Product == null) { Info($"Ligne {index} : choisissez un produit."); return; }
            if (line.Quantity is not > 0) { Info($"Ligne {index} : la quantité ou le montant n'est pas valide."); return; }
            if (line.UnitCost is not > 0) { Info($"Ligne {index} : le prix ou coefficient n'est pas valide."); return; }
            lines.Add(new PurchaseLineInput(line.Product.Id, line.Quantity.Value, line.UnitCost.Value));
        }

        var paid = string.IsNullOrWhiteSpace(PaidText) ? 0m : ParseDecimal(PaidText);
        if (paid == null) { Info("Le montant payé n'est pas un nombre valide."); return; }

        PurchaseOrder? saved = null;
        var ok = await TryAsync(async () =>
            saved = await _purchases.CreateAsync(new PurchaseInput(Supplier.Id, PurchaseDate, lines, paid.Value)));
        if (!ok || saved == null) return;

        ResetForm();
        await TryAsync(async () =>
        {
            await LoadHistoryAsync();
            Products.Clear();
            foreach (var p in await _products.ListAsync()) Products.Add(p);
        });
        Info($"Bon d'achat {saved.Number} enregistré ({saved.Total:N2} DA). Le stock a été mis à jour.");
    }
}
