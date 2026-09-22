using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GestionStock.Core.Domain;
using GestionStock.Core.Services;

namespace GestionStock.App.ViewModels;

public record KindOption(ProductKind Kind, string Label);

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

public record ProductRow(Product Product)
{
    public string Name => Product.Name;
    public string KindLabel => Labels.Kind(Product.Kind);
    public string OperatorName => Product.Operator?.Name ?? "";
    public decimal StockBalance => Product.StockBalance;
    public string Unit => Product.Kind == ProductKind.VirtualCredit ? "DA" : "unités";
    public string ActiveLabel => Product.IsActive ? "Oui" : "Non";
}

public record MovementRow(StockMovement Movement)
{
    public DateTime Date => Movement.Date;
    public string KindLabel => Labels.Movement(Movement.Kind);
    public string QuantityText => Movement.Quantity.ToString("+#,##0.00;-#,##0.00");
    public string? Note => Movement.Note;
}

public partial class ProductsViewModel : ViewModelBase
{
    private readonly ProductService _service;
    private readonly SettingsService _settings;

    public ProductsViewModel(ProductService service, SettingsService settings)
    {
        _service = service;
        _settings = settings;
        _ = InitializeAsync();
    }

    public ObservableCollection<ProductRow> Items { get; } = new();
    public ObservableCollection<MovementRow> Movements { get; } = new();
    public ObservableCollection<Operator> Operators { get; } = new();

    public IReadOnlyList<KindOption> Kinds { get; } =
    [
        new(ProductKind.VirtualCredit, Labels.Kind(ProductKind.VirtualCredit)),
        new(ProductKind.Physical, Labels.Kind(ProductKind.Physical)),
    ];

    [ObservableProperty] private ProductRow? _selected;
    [ObservableProperty] private int _editId;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private ProductKind _kind = ProductKind.VirtualCredit;
    [ObservableProperty] private Operator? _operator;
    [ObservableProperty] private bool _isActive = true;
    [ObservableProperty] private string _countedBalance = "";
    [ObservableProperty] private string _adjustNote = "";

    public bool IsVirtual => Kind == ProductKind.VirtualCredit;
    public string FormTitle => EditId == 0 ? "Nouveau produit" : "Modifier le produit";

    partial void OnKindChanged(ProductKind value) => OnPropertyChanged(nameof(IsVirtual));
    partial void OnEditIdChanged(int value) => OnPropertyChanged(nameof(FormTitle));

    partial void OnSelectedChanged(ProductRow? value)
    {
        Movements.Clear();
        if (value == null) return;
        var p = value.Product;
        EditId = p.Id;
        Name = p.Name;
        Kind = p.Kind;
        Operator = Operators.FirstOrDefault(o => o.Id == p.OperatorId);
        IsActive = p.IsActive;
        CountedBalance = "";
        AdjustNote = "";
        _ = LoadMovementsAsync(p.Id);
    }

    private async Task InitializeAsync()
    {
        await TryAsync(async () =>
        {
            foreach (var op in await _settings.GetOperatorsAsync()) Operators.Add(op);
            await LoadAsync();
        });
    }

    private async Task LoadAsync()
    {
        var products = await _service.ListAsync(includeInactive: true);
        Items.Clear();
        foreach (var p in products) Items.Add(new ProductRow(p));
    }

    private async Task LoadMovementsAsync(int productId)
    {
        await TryAsync(async () =>
        {
            var rows = await _service.GetMovementsAsync(productId);
            if (Selected?.Product.Id != productId) return;
            Movements.Clear();
            foreach (var m in rows) Movements.Add(new MovementRow(m));
        });
    }

    [RelayCommand]
    private void New()
    {
        Selected = null;
        Movements.Clear();
        EditId = 0;
        Name = "";
        Kind = ProductKind.VirtualCredit;
        Operator = null;
        IsActive = true;
        CountedBalance = AdjustNote = "";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var ok = await TryAsync(async () => await _service.SaveAsync(new Product
        {
            Id = EditId, Name = Name, Kind = Kind, OperatorId = Operator?.Id, IsActive = IsActive,
        }));
        if (!ok) return;
        New();
        await TryAsync(LoadAsync);
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (EditId == 0 || !Confirm($"Supprimer le produit « {Name} » ?")) return;
        if (await TryAsync(() => _service.DeleteAsync(EditId)))
        {
            New();
            await TryAsync(LoadAsync);
        }
    }

    [RelayCommand]
    private async Task AdjustAsync()
    {
        if (EditId == 0) { Info("Sélectionnez d'abord un produit."); return; }
        var counted = ParseDecimal(CountedBalance);
        if (counted == null) { Info("Saisissez le solde compté (un nombre)."); return; }
        if (!Confirm($"Fixer le solde de « {Name} » à {counted:N2} ?\nUn mouvement de correction sera enregistré.")) return;

        var id = EditId;
        if (!await TryAsync(() => _service.AdjustStockAsync(id, counted.Value, AdjustNote))) return;
        await TryAsync(LoadAsync);
        Selected = Items.FirstOrDefault(r => r.Product.Id == id);
    }
}
