using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GestionStock.App.Services;
using GestionStock.Core.Domain;
using GestionStock.Core.Services;

namespace GestionStock.App.ViewModels;

public record KindOption(ProductKind Kind, string Label);

public record KindFilterOption(ProductKind? Kind, string Label);

/// <summary>Choix du filtre par type dans la fenêtre des mouvements (null = tous les types).</summary>
public record MovementKindOption(StockMovementKind? Kind, string Label);

public static class Labels
{
    public static string Kind(ProductKind kind) => kind == ProductKind.VirtualCredit ? "Crédit virtuel" : "Produit physique";

    public static string Movement(StockMovementKind kind) => kind switch
    {
        StockMovementKind.Purchase => "Achat",
        StockMovementKind.Sale => "Vente",
        _ => "Correction",
    };
}

/// <param name="PurchaseCost">Prix d'achat du dernier bon d'achat (coefficient ou prix unitaire), null si jamais acheté.</param>
public record ProductRow(Product Product, decimal? PurchaseCost)
{
    public string Name => Product.Name;
    public string KindLabel => Labels.Kind(Product.Kind);
    public decimal StockBalance => Product.StockBalance;
    /// <summary>Valeur du stock au dernier prix d'achat : quantité (ou montant de crédit) × prix d'achat.</summary>
    public decimal? StockValue => PurchaseCost is { } cost ? Math.Round(StockBalance * cost, 2, MidpointRounding.AwayFromZero) : null;
    public string PurchaseCostText => PurchaseCost?.ToString("0.####") ?? "—";
    public string Unit => Product.Kind == ProductKind.VirtualCredit ? "DA" : "unités";
    public string ActiveLabel => Product.IsActive ? "Oui" : "Non";
    public string ColorHex => Product.ColorHex;
}

public record MovementRow(StockMovement Movement)
{
    public DateTime Date => Movement.Date;
    public StockMovementKind Kind => Movement.Kind;
    public string KindLabel => Labels.Movement(Movement.Kind);
    public string QuantityText => Movement.Quantity.ToString("+#,##0.00;-#,##0.00");
    public string? Note => Movement.Note;
}

public partial class ProductsViewModel : ViewModelBase
{
    private readonly ProductService _service;

    public ProductsViewModel(ProductService service)
    {
        _service = service;
        MovementsKind = MovementKindFilters[0];
        _ = InitializeAsync();
    }

    public IReadOnlyList<MovementKindOption> MovementKindFilters { get; } =
    [
        new(null, "Tous les types"),
        new(StockMovementKind.Purchase, Labels.Movement(StockMovementKind.Purchase)),
        new(StockMovementKind.Sale, Labels.Movement(StockMovementKind.Sale)),
        new(StockMovementKind.Adjustment, Labels.Movement(StockMovementKind.Adjustment)),
    ];

    public ObservableCollection<ProductRow> Items { get; } = new();
    public ObservableCollection<MovementRow> Movements { get; } = new();
    /// <summary>Mouvements affichés dans la fenêtre agrandie, filtrés par date.</summary>
    public ObservableCollection<MovementRow> FilteredMovements { get; } = new();

    /// <summary>Nuances prêtes à l'emploi pour la couleur du produit dans le rapport.</summary>
    public IReadOnlyList<string> ColorPresets { get; } =
        ["#F59E0B", "#DC2626", "#16A34A", "#2563EB", "#7C3AED", "#0EA5E9", "#DB2777", "#65A30D", "#6B7280"];

    public IReadOnlyList<KindOption> Kinds { get; } =
    [
        new(ProductKind.VirtualCredit, Labels.Kind(ProductKind.VirtualCredit)),
        new(ProductKind.Physical, Labels.Kind(ProductKind.Physical)),
    ];

    public IReadOnlyList<KindFilterOption> KindFilters { get; } =
    [
        new(null, "Tous les types"),
        new(ProductKind.VirtualCredit, Labels.Kind(ProductKind.VirtualCredit)),
        new(ProductKind.Physical, Labels.Kind(ProductKind.Physical)),
    ];

    private List<Product> _all = new();
    private Dictionary<int, decimal> _lastCosts = new();

    /// <summary>Valeur totale des produits affichés, au dernier prix d'achat.</summary>
    [ObservableProperty] private decimal _totalStockValue;

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private KindFilterOption? _kindFilter;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditCommand), nameof(DeleteCommand), nameof(OpenAdjustCommand), nameof(OpenMovementsCommand))]
    private ProductRow? _selected;

    /// <summary>Fenêtre agrandie des mouvements du produit sélectionné (avec filtre par date et impression).</summary>
    [ObservableProperty] private bool _isMovementsOpen;
    [ObservableProperty] private DateTime? _movementsFrom;
    [ObservableProperty] private DateTime? _movementsTo;
    [ObservableProperty] private MovementKindOption? _movementsKind;

    /// <summary>Fiche produit affichée par-dessus la liste. La fermer sans enregistrer garde la saisie en cours.</summary>
    [ObservableProperty] private bool _isFormOpen;
    /// <summary>Fenêtre de correction d'inventaire du produit sélectionné.</summary>
    [ObservableProperty] private bool _isAdjustOpen;
    [ObservableProperty] private int _editId;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private ProductKind _kind = ProductKind.VirtualCredit;
    [ObservableProperty] private bool _isActive = true;
    [ObservableProperty] private string _colorHex = "#6B7280";
    [ObservableProperty] private string _countedBalance = "";
    [ObservableProperty] private string _adjustNote = "";
    [ObservableProperty] private string _adjustPurchaseCost = "";

    public string FormTitle => EditId == 0 ? "Nouveau produit" : "Modifier le produit";

    partial void OnEditIdChanged(int value) => OnPropertyChanged(nameof(FormTitle));
    partial void OnSearchChanged(string value) => ApplyFilter();
    partial void OnKindFilterChanged(KindFilterOption? value) => ApplyFilter();

    /// <summary>Titre et stock actuel affichés dans la fenêtre de correction d'inventaire.</summary>
    public string AdjustTitle => Selected == null ? "" : $"Corriger le stock de « {Selected.Name} »";
    public string AdjustCurrent => Selected == null ? "" : $"Stock actuel : {Selected.StockBalance:N2} {Selected.Unit}";

    public string MovementsTitle => Selected == null ? "" : $"Mouvements de « {Selected.Name} »";

    partial void OnMovementsFromChanged(DateTime? value) => ApplyMovementsFilter();
    partial void OnMovementsToChanged(DateTime? value) => ApplyMovementsFilter();
    partial void OnMovementsKindChanged(MovementKindOption? value) => ApplyMovementsFilter();

    partial void OnSelectedChanged(ProductRow? value)
    {
        Movements.Clear();
        ApplyMovementsFilter();
        OnPropertyChanged(nameof(AdjustTitle));
        OnPropertyChanged(nameof(AdjustCurrent));
        OnPropertyChanged(nameof(MovementsTitle));
        if (value != null) _ = LoadMovementsAsync(value.Product.Id);
        else IsMovementsOpen = false;
    }

    /// <summary>Recalcule la liste de la fenêtre agrandie : mouvements du type choisi, compris entre les deux dates (bornes incluses).</summary>
    private void ApplyMovementsFilter()
    {
        FilteredMovements.Clear();
        foreach (var m in Movements
                     .Where(m => MovementsFrom == null || m.Date.Date >= MovementsFrom.Value.Date)
                     .Where(m => MovementsTo == null || m.Date.Date <= MovementsTo.Value.Date)
                     .Where(m => MovementsKind?.Kind == null || m.Kind == MovementsKind.Kind))
            FilteredMovements.Add(m);
    }

    private bool HasSelection() => Selected != null;

    [RelayCommand]
    private void OpenForm()
    {
        if (EditId != 0) New(); // on quitte la modification d'un produit pour en créer un nouveau
        IsFormOpen = true;
    }

    [RelayCommand] private void CloseForm() => IsFormOpen = false;

    /// <summary>Charge le produit sélectionné dans la fiche pour le modifier.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Edit()
    {
        if (Selected is not { } value) return;
        var p = value.Product;
        EditId = p.Id;
        Name = p.Name;
        Kind = p.Kind;
        IsActive = p.IsActive;
        ColorHex = p.ColorHex;
        IsFormOpen = true;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void OpenAdjust()
    {
        CountedBalance = AdjustNote = AdjustPurchaseCost = "";
        IsAdjustOpen = true;
    }

    [RelayCommand] private void CloseAdjust() => IsAdjustOpen = false;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void OpenMovements() => IsMovementsOpen = true;

    [RelayCommand] private void CloseMovements() => IsMovementsOpen = false;

    [RelayCommand]
    private void ResetMovementsFilter()
    {
        MovementsFrom = null;
        MovementsTo = null;
        MovementsKind = MovementKindFilters[0];
    }

    [RelayCommand]
    private void PrintMovements()
    {
        var period = (MovementsFrom, MovementsTo) switch
        {
            ({ } from, { } to) => $" — du {from:dd/MM/yyyy} au {to:dd/MM/yyyy}",
            ({ } from, null) => $" — depuis le {from:dd/MM/yyyy}",
            (null, { } to) => $" — jusqu'au {to:dd/MM/yyyy}",
            _ => "",
        };
        if (MovementsKind?.Kind != null) period += $" — {MovementsKind.Label}";
        PrintHelper.PrintTable(MovementsTitle + period,
            ["Date", "Type", "Quantité", "Note"],
            FilteredMovements.Select(m => new[] { m.Date.ToString("dd/MM/yyyy HH:mm"), m.KindLabel, m.QuantityText, m.Note ?? "" }).ToList());
    }

    private async Task InitializeAsync()
    {
        await TryAsync(async () =>
        {
            KindFilter = KindFilters[0];
            await LoadAsync();
        });
    }

    private async Task LoadAsync()
    {
        _all = await _service.ListAsync(includeInactive: true);
        _lastCosts = await _service.GetLastPurchaseCostsAsync();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var term = Search.Trim();
        var rows = _all
            .Where(p => term.Length == 0 || p.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase))
            .Where(p => KindFilter?.Kind == null || p.Kind == KindFilter.Kind)
            .Select(p => new ProductRow(p, _lastCosts.TryGetValue(p.Id, out var cost) ? cost : null))
            .ToList();

        Items.Clear();
        foreach (var r in rows) Items.Add(r);
        TotalStockValue = rows.Sum(r => r.StockValue ?? 0m);
    }

    private async Task LoadMovementsAsync(int productId)
    {
        await TryAsync(async () =>
        {
            var rows = await _service.GetMovementsAsync(productId);
            if (Selected?.Product.Id != productId) return;
            Movements.Clear();
            foreach (var m in rows) Movements.Add(new MovementRow(m));
            ApplyMovementsFilter();
        });
    }

    /// <summary>Vide la fiche pour saisir un nouveau produit.</summary>
    [RelayCommand]
    private void New()
    {
        EditId = 0;
        Name = "";
        Kind = ProductKind.VirtualCredit;
        IsActive = true;
        ColorHex = "#6B7280";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var id = EditId;
        Product? saved = null;
        var ok = await TryAsync(async () => saved = await _service.SaveAsync(new Product
        {
            Id = id, Name = Name, Kind = Kind, IsActive = IsActive, ColorHex = ColorHex,
        }));
        if (!ok || saved == null) return;
        New();
        IsFormOpen = false;
        await ReloadAndSelectAsync(saved.Id);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteAsync()
    {
        if (Selected is not { } row || !Confirm($"Supprimer le produit « {row.Name} » ?\nCette action est définitive.")) return;
        if (!await TryAsync(() => _service.DeleteAsync(row.Product.Id))) return;
        if (EditId == row.Product.Id)
        {
            New();
            IsFormOpen = false;
        }
        await TryAsync(LoadAsync);
    }

    [RelayCommand]
    private async Task AdjustAsync()
    {
        if (Selected is not { } row) return;
        var counted = ParseDecimal(CountedBalance);
        if (counted == null) { Info("Saisissez le solde compté (un nombre)."); return; }

        decimal? purchaseCost = null;
        if (!string.IsNullOrWhiteSpace(AdjustPurchaseCost))
        {
            purchaseCost = ParseDecimal(AdjustPurchaseCost);
            if (purchaseCost == null) { Info("Le prix d'achat n'est pas un nombre valide."); return; }
        }

        if (!Confirm($"Fixer le solde de « {row.Name} » à {counted:N2} ?\nUn mouvement de correction sera enregistré.")) return;

        var id = row.Product.Id;
        if (!await TryAsync(() => _service.AdjustStockAsync(id, counted.Value, AdjustNote, purchaseCost))) return;
        IsAdjustOpen = false;
        await ReloadAndSelectAsync(id);
    }

    /// <summary>Recharge la liste et resélectionne le produit, pour garder ses mouvements affichés.</summary>
    private async Task ReloadAndSelectAsync(int productId)
    {
        await TryAsync(LoadAsync);
        Selected = Items.FirstOrDefault(r => r.Product.Id == productId);
    }

    [RelayCommand]
    private void PickColor(string? hex)
    {
        if (!string.IsNullOrWhiteSpace(hex)) ColorHex = hex;
    }

    [RelayCommand]
    private void Print()
    {
        PrintHelper.PrintTable("Produits & stock",
            ["Produit", "Type", "Stock", "Prix d'achat", "Total (DA)", "Actif"],
            Items.Select(r => new[]
            {
                r.Name, r.KindLabel, r.StockBalance.ToString("N2"), r.PurchaseCostText, r.StockValue?.ToString("N2") ?? "—", r.ActiveLabel,
            }).ToList());
    }
}
