using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GestionStock.App.Services;
using GestionStock.Core.Services;

namespace GestionStock.App.ViewModels;

public record ProductBalanceRowView(ProductBalanceRow Row)
{
    public string ProductName => Row.ProductName;
    public string ColorHex => Row.ColorHex;
    public string QuantityText => Row.CurrentStock.ToString("N2");
    public string AchatsText => Row.Achats.ToString("N2");
    public string VentesText => Row.Ventes.ToString("N2");
    public string MargeText => Row.Marge?.ToString("N2") ?? "—";
}

/// <summary>
/// Bilan par produit : stock actuel et activité (achats, ventes, marge) sur une période, plus l'argent réellement
/// encaissé et dépensé sur la même période.
/// </summary>
public partial class ProductBalanceViewModel : ViewModelBase
{
    private readonly ReportService _reports;

    public ProductBalanceViewModel(ReportService reports)
    {
        _reports = reports;
        _ = LoadAsync();
    }

    public ObservableCollection<ProductBalanceRowView> Items { get; } = new();

    // Par défaut : du 1er du mois jusqu'à aujourd'hui. Pour remonter plus loin, il suffit de reculer la date « Du ».
    [ObservableProperty] private DateTime? _dateFrom = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
    [ObservableProperty] private DateTime? _dateTo = DateTime.Today;

    [ObservableProperty] private decimal _totalAchats;
    [ObservableProperty] private decimal _totalVentes;
    [ObservableProperty] private decimal _totalMarge;
    [ObservableProperty] private decimal _totalVersements;
    [ObservableProperty] private decimal _totalDepenses;
    [ObservableProperty] private decimal _totalNet;

    partial void OnDateFromChanged(DateTime? value) => _ = LoadAsync();
    partial void OnDateToChanged(DateTime? value) => _ = LoadAsync();

    private async Task LoadAsync()
    {
        await TryAsync(async () =>
        {
            var (rows, cash) = await _reports.GetProductBalanceAsync(DateFrom, DateTo);
            Items.Clear();
            foreach (var r in rows) Items.Add(new ProductBalanceRowView(r));

            TotalAchats = rows.Sum(r => r.Achats);
            TotalVentes = rows.Sum(r => r.Ventes);
            TotalMarge = rows.Sum(r => r.Marge ?? 0m);
            TotalVersements = cash.TotalVersements;
            TotalDepenses = cash.TotalDepenses;
            TotalNet = cash.Total;
        });
    }

    private string Period() => (DateFrom, DateTo) switch
    {
        ({ } f, { } t) => $" — du {f:dd/MM/yyyy} au {t:dd/MM/yyyy}",
        ({ } f, null) => $" — depuis le {f:dd/MM/yyyy}",
        (null, { } t) => $" — jusqu'au {t:dd/MM/yyyy}",
        _ => "",
    };

    [RelayCommand]
    private void Print()
    {
        PrintHelper.PrintTable("Bilan produits" + Period(),
            ["Produit", "Quantité", "Achats (DA)", "Ventes (DA)", "Marge (DA)"],
            Items.Select(r => new[] { r.ProductName, r.QuantityText, r.AchatsText, r.VentesText, r.MargeText }).ToList(),
            [
                ("Total achats", $"{TotalAchats:N2} DA"),
                ("Total ventes", $"{TotalVentes:N2} DA"),
                ("Total marge", $"{TotalMarge:N2} DA"),
                ("Total versements", $"{TotalVersements:N2} DA"),
                ("Total dépenses", $"{TotalDepenses:N2} DA"),
                ("Total (versements − dépenses)", $"{TotalNet:N2} DA"),
            ]);
    }
}
