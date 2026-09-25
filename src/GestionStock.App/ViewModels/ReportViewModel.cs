using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GestionStock.App.Services;
using GestionStock.Core.Domain;
using GestionStock.Core.Services;

namespace GestionStock.App.ViewModels;

public record OperationRowView(OperationRow Row)
{
    public DateTime Date => Row.Date;
    public string Type => Row.Type;
    public string Number => Row.Number;
    public string Tiers => Row.Tiers;
    public string ProductName => Row.ProductName ?? "";    public string ProductColorHex => Row.ProductColorHex ?? "#6B7280";
    public bool HasProduct => Row.ProductName != null;
    /// <summary>Couleur du fond de ligne : celle du produit, aucune pour les lignes sans produit (encaissements).</summary>
    public string? RowColorHex => HasProduct ? ProductColorHex : null;
    public string QuantityText => Row.Quantity?.ToString("N2") ?? "";
    public string RateText => Row.Rate?.ToString("0.####") ?? "";
    public decimal Total => Row.Total;
    /// <summary>Stock du produit après l'opération ; « — » quand il n'y en a pas (encaissement).</summary>
    public string StockAfterText => Row.StockAfter?.ToString("N2") ?? "—";
}

/// <summary>Filtre "tous les tiers" ou un produit particulier, utilisé dans la liste déroulante du rapport.</summary>
public record ProductFilterOption(Product? Product)
{
    public string Label => Product?.Name ?? "Tous les produits";
}

public partial class ReportViewModel : ViewModelBase
{
    private readonly ReportService _reports;
    private List<OperationRow> _all = new();

    public ReportViewModel(ReportService reports, ProductService products)
    {
        _reports = reports;
        _ = InitializeAsync(products);
    }

    public ObservableCollection<OperationRowView> Items { get; } = new();
    public ObservableCollection<ProductFilterOption> ProductFilters { get; } = new();

    [ObservableProperty] private bool _showAchats = true;
    [ObservableProperty] private bool _showVentes = true;
    [ObservableProperty] private bool _showEncaissements = true;
    [ObservableProperty] private string _tiersSearch = "";
    [ObservableProperty] private ProductFilterOption? _productFilter;
    [ObservableProperty] private DateTime? _dateFrom;
    [ObservableProperty] private DateTime? _dateTo;
    [ObservableProperty] private string _amountMin = "";
    [ObservableProperty] private string _amountMax = "";

    [ObservableProperty] private decimal _totalAchats;
    [ObservableProperty] private decimal _totalVentes;
    [ObservableProperty] private decimal _totalEncaissements;

    partial void OnShowAchatsChanged(bool value) => ApplyFilter();
    partial void OnShowVentesChanged(bool value) => ApplyFilter();
    partial void OnShowEncaissementsChanged(bool value) => ApplyFilter();
    partial void OnTiersSearchChanged(string value) => ApplyFilter();
    partial void OnProductFilterChanged(ProductFilterOption? value) => ApplyFilter();
    partial void OnDateFromChanged(DateTime? value) => ApplyFilter();
    partial void OnDateToChanged(DateTime? value) => ApplyFilter();
    partial void OnAmountMinChanged(string value) => ApplyFilter();
    partial void OnAmountMaxChanged(string value) => ApplyFilter();

    private async Task InitializeAsync(ProductService products)
    {
        await TryAsync(async () =>
        {
            ProductFilters.Add(new ProductFilterOption(null));
            foreach (var p in await products.ListAsync(includeInactive: true)) ProductFilters.Add(new ProductFilterOption(p));
            ProductFilter = ProductFilters[0];
            await LoadAsync();
        });
    }

    private async Task LoadAsync()
    {
        _all = await _reports.GetOperationsAsync();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var term = TiersSearch.Trim();
        var min = ParseDecimal(AmountMin);
        var max = ParseDecimal(AmountMax);

        var rows = _all
            .Where(r => r.Type switch { "Achat" => ShowAchats, "Vente" => ShowVentes, _ => ShowEncaissements })
            .Where(r => term.Length == 0 || r.Tiers.Contains(term, StringComparison.CurrentCultureIgnoreCase))
            .Where(r => ProductFilter?.Product == null || r.ProductName == ProductFilter.Product.Name)
            .Where(r => DateFrom == null || r.Date.Date >= DateFrom.Value.Date)
            .Where(r => DateTo == null || r.Date.Date <= DateTo.Value.Date)
            .Where(r => min == null || r.Total >= min)
            .Where(r => max == null || r.Total <= max)
            .ToList();

        Items.Clear();
        foreach (var r in rows) Items.Add(new OperationRowView(r));

        TotalAchats = rows.Where(r => r.Type == "Achat").Sum(r => r.Total);
        TotalVentes = rows.Where(r => r.Type == "Vente").Sum(r => r.Total);
        // Inclut l'argent encaissé au moment d'une vente.
        TotalEncaissements = rows.Where(r => r.Type == "Encaissement").Sum(r => r.Total);
    }

    [RelayCommand]
    private void Print()
    {
        PrintHelper.PrintTable("Historique des opérations",
            ["Date", "Type", "N°", "Tiers", "Produit", "Qté / montant", "Coef. / prix", "Total (DA)"],
            Items.Select(r => new[]
            {
                r.Date.ToString("dd/MM/yyyy"), r.Type, r.Number, r.Tiers, r.ProductName, r.QuantityText, r.RateText, r.Total.ToString("N2"),
            }).ToList());
    }
}
