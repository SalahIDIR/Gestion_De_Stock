using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GestionStock.App.Services;
using GestionStock.Core.Domain;
using GestionStock.Core.Services;

namespace GestionStock.App.ViewModels;

/// <summary>
/// Rapport des transactions de crédit virtuel (Flexy, Storm, Erselli…) : achats et ventes seulement,
/// sans encaissements ni produits physiques.
/// </summary>
public partial class TransactionsReportViewModel : ViewModelBase
{
    private readonly ReportService _reports;
    private List<OperationRow> _all = new();

    public TransactionsReportViewModel(ReportService reports, ProductService products)
    {
        _reports = reports;
        _ = InitializeAsync(products);
    }

    public ObservableCollection<OperationRowView> Items { get; } = new();
    public ObservableCollection<ProductFilterOption> ProductFilters { get; } = new();

    [ObservableProperty] private bool _showAchats = true;
    [ObservableProperty] private bool _showVentes = true;
    [ObservableProperty] private string _tiersSearch = "";
    [ObservableProperty] private ProductFilterOption? _productFilter;
    // Par défaut : du 1er du mois jusqu'à aujourd'hui. Pour remonter plus loin, il suffit de reculer la date « Du ».
    [ObservableProperty] private DateTime? _dateFrom = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
    [ObservableProperty] private DateTime? _dateTo = DateTime.Today;

    /// <summary>Crédit reçu des fournisseurs (valeur faciale) et ce qu'il a coûté.</summary>
    [ObservableProperty] private decimal _creditAchete;
    [ObservableProperty] private decimal _coutAchats;
    /// <summary>Crédit envoyé aux clients (valeur faciale) et ce qui leur a été facturé.</summary>
    [ObservableProperty] private decimal _creditVendu;
    [ObservableProperty] private decimal _totalVentes;

    partial void OnShowAchatsChanged(bool value) => ApplyFilter();
    partial void OnShowVentesChanged(bool value) => ApplyFilter();
    partial void OnTiersSearchChanged(string value) => ApplyFilter();
    partial void OnProductFilterChanged(ProductFilterOption? value) => ApplyFilter();
    partial void OnDateFromChanged(DateTime? value) => ApplyFilter();
    partial void OnDateToChanged(DateTime? value) => ApplyFilter();

    private async Task InitializeAsync(ProductService products)
    {
        await TryAsync(async () =>
        {
            ProductFilters.Add(new ProductFilterOption(null));
            foreach (var p in (await products.ListAsync(includeInactive: true)).Where(p => p.Kind == ProductKind.VirtualCredit))
                ProductFilters.Add(new ProductFilterOption(p));
            ProductFilter = ProductFilters[0];
            _all = await _reports.GetVirtualCreditTransactionsAsync();
            ApplyFilter();
        });
    }

    private void ApplyFilter()
    {
        var term = TiersSearch.Trim();
        var rows = _all
            .Where(r => r.Type switch { "Achat" => ShowAchats, "Vente" => ShowVentes, _ => false })
            .Where(r => ProductFilter?.Product == null || r.ProductName == ProductFilter.Product.Name)
            .Where(r => term.Length == 0 || r.Tiers.Contains(term, StringComparison.CurrentCultureIgnoreCase))
            .Where(r => DateFrom == null || r.Date.Date >= DateFrom.Value.Date)
            .Where(r => DateTo == null || r.Date.Date <= DateTo.Value.Date)
            .ToList();

        Items.Clear();
        foreach (var r in rows) Items.Add(new OperationRowView(r));

        var achats = rows.Where(r => r.Type == "Achat").ToList();
        var ventes = rows.Where(r => r.Type == "Vente").ToList();
        CreditAchete = achats.Sum(r => r.Quantity ?? 0m);
        CoutAchats = achats.Sum(r => r.Total);
        CreditVendu = ventes.Sum(r => r.Quantity ?? 0m);
        TotalVentes = ventes.Sum(r => r.Total);
    }

    [RelayCommand]
    private void Print()
    {
        PrintHelper.PrintTable("Rapport des transactions de crédit virtuel",
            ["Date", "Type", "N°", "Tiers", "Produit", "Puce destinataire", "Montant crédit (DA)", "Crédit restant (DA)"],
            Items.Select(r => new[]
            {
                r.Date.ToString("dd/MM/yyyy HH:mm"), r.Type, r.Number, r.Tiers, r.ProductName, r.RecipientPhoneText,
                r.QuantityText, r.StockAfterText,
            }).ToList());
    }
}
