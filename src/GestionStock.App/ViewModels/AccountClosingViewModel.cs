using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GestionStock.App.Services;
using GestionStock.Core.Domain;
using GestionStock.Core.Services;

namespace GestionStock.App.ViewModels;

/// <summary>
/// « Solde des comptes » : clôture de caisse. Les statistiques (stock, crédits) et les mouvements d'espèce depuis
/// la dernière clôture sont calculés en direct ; seul le prélèvement est saisi à la main. Rien n'est enregistré
/// tant que « Valider ce solde » n'a pas été cliqué.
/// </summary>
public partial class AccountClosingViewModel : ViewModelBase
{
    private readonly AccountClosingService _closings;

    public AccountClosingViewModel(AccountClosingService closings)
    {
        _closings = closings;
        _ = LoadAsync();
    }

    [ObservableProperty] private DateTime _now;
    [ObservableProperty] private DateTime? _previousDate;
    [ObservableProperty] private decimal _previousTotal;
    [ObservableProperty] private decimal _previousCash;

    [ObservableProperty] private decimal _stockValue;
    [ObservableProperty] private decimal _clientCredit;
    [ObservableProperty] private decimal _supplierCredit;
    [ObservableProperty] private decimal _totalRecettes;
    [ObservableProperty] private decimal _totalDepenses;

    [ObservableProperty] private string _prelevementsText = "0";
    [ObservableProperty] private string _comments = "";

    /// <summary>Espèce calculée automatiquement (ancien espèce + recettes − dépenses) au dernier chargement.</summary>
    private decimal _autoCash;
    [ObservableProperty] private string _cashText = "";

    public string PreviousDateText => PreviousDate?.ToString("dd/MM/yyyy HH:mm") ?? "Aucune (première clôture)";

    public decimal? Prelevements => ParseDecimal(PrelevementsText);
    /// <summary>Espèce brute (ancien espèce + recettes − dépenses), avant prélèvement : calculée automatiquement,
    /// mais modifiable à la main ; la valeur saisie remplace alors le calcul.</summary>
    private decimal GrossCash => ParseDecimal(CashText) ?? _autoCash;
    /// <summary>Espèce réellement en caisse, prélèvement déduit : c'est elle qui est affichée, enregistrée, et reprise
    /// comme « ancien espèce » par la clôture suivante — l'argent prélevé n'y est plus.</summary>
    public decimal Cash => GrossCash - (Prelevements ?? 0m);
    public decimal Total => StockValue + ClientCredit + Cash - SupplierCredit;
    public decimal Benefice => Total - PreviousTotal;

    public decimal Moyenne
    {
        get
        {
            var days = PreviousDate.HasValue ? Math.Max(1m, (decimal)(Now - PreviousDate.Value).TotalDays) : 1m;
            return Math.Round(Benefice / days, 2, MidpointRounding.AwayFromZero);
        }
    }

    partial void OnPrelevementsTextChanged(string value) => RefreshComputed();
    partial void OnCashTextChanged(string value) => RefreshComputed();

    private void RefreshComputed()
    {
        OnPropertyChanged(nameof(Prelevements));
        OnPropertyChanged(nameof(Cash));
        OnPropertyChanged(nameof(Total));
        OnPropertyChanged(nameof(Benefice));
        OnPropertyChanged(nameof(Moyenne));
    }

    private async Task LoadAsync()
    {
        await TryAsync(async () =>
        {
            Now = DateTime.Now;
            var preview = await _closings.PreviewAsync(Now);
            PreviousDate = preview.PreviousDate;
            PreviousTotal = preview.PreviousTotal;
            PreviousCash = preview.PreviousCash;
            TotalRecettes = preview.TotalRecettes;
            TotalDepenses = preview.TotalDepenses;
            _autoCash = preview.Cash;
            CashText = _autoCash.ToString("0.##");
            StockValue = preview.StockValue;
            ClientCredit = preview.ClientCredit;
            SupplierCredit = preview.SupplierCredit;
            OnPropertyChanged(nameof(PreviousDateText));
            RefreshComputed();
        });
    }

    [RelayCommand]
    private async Task RefreshAsync() => await LoadAsync();

    /// <summary>Remet le prélèvement, le commentaire et l'espèce à leurs valeurs de départ sans rien enregistrer (« Différer »).</summary>
    [RelayCommand]
    private void Differ()
    {
        PrelevementsText = "0";
        Comments = "";
        CashText = _autoCash.ToString("0.##");
    }

    [RelayCommand]
    private async Task ValidateAsync()
    {
        var prelev = Prelevements;
        if (prelev is null || prelev < 0) { Info("Le prélèvement doit être un nombre positif ou nul."); return; }
        if (!Confirm($"Valider cette clôture ?\n\nNouveau solde : {Total:N2} DA\nBénéfice : {Benefice:N2} DA\n\nCette action est définitive."))
            return;

        AccountClosing? saved = null;
        var ok = await TryAsync(async () => saved = await _closings.ValidateAsync(Now, prelev.Value, Comments, GrossCash));
        if (!ok || saved == null) return;

        Differ();
        await LoadAsync();
        Info($"Clôture enregistrée.\nNouveau solde : {saved.Total:N2} DA\nBénéfice : {saved.Benefice:N2} DA");
    }

    [RelayCommand]
    private void Print()
    {
        PrintHelper.PrintBon("Solde des comptes", Now.ToString("dd/MM/yyyy HH:mm"), Now,
            [
                ("Date du dernier solde", PreviousDateText),
                ("Montant du dernier solde", $"{PreviousTotal:N2} DA"),
            ],
            ["Statistique", "Valeur"],
            [
                new[] { "Stock en valeur", $"{StockValue:N2} DA" },
                new[] { "Crédit clients", $"{ClientCredit:N2} DA" },
                new[] { "Crédit fournisseurs", $"{SupplierCredit:N2} DA" },
                new[] { "Ancien espèce", $"{PreviousCash:N2} DA" },
                new[] { "Total des recettes", $"{TotalRecettes:N2} DA" },
                new[] { "Total des dépenses", $"{TotalDepenses:N2} DA" },
                new[] { "Espèce", $"{Cash:N2} DA" },
                new[] { "Prélèvements", $"{Prelevements ?? 0m:N2} DA" },
            ],
            [
                ("Nouveau solde", $"{Total:N2} DA"),
                ("Bénéfice", $"{Benefice:N2} DA"),
                ("Moyenne par jour", $"{Moyenne:N2} DA"),
            ]);
    }
}
