using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GestionStock.App.Services;
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

    private bool _settingAutoPrice;

    /// <summary>Vrai si le coefficient a été proposé automatiquement (tarif du fournisseur) et pas saisi à la main.</summary>
    public bool IsPriceAutoFilled { get; private set; }

    /// <summary>Remplit le coefficient avec le tarif proposé ; il sera remplacé si le fournisseur ou le produit change.</summary>
    public void SetAutoPrice(string text)
    {
        _settingAutoPrice = true;
        UnitCostText = text;
        _settingAutoPrice = false;
        IsPriceAutoFilled = text.Length > 0;
    }

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
    partial void OnUnitCostTextChanged(string value)
    {
        if (!_settingAutoPrice) IsPriceAutoFilled = false; // saisi à la main : on n'y touche plus
        OnPropertyChanged(nameof(Total));
    }
}

public record PurchaseRow(PurchaseOrder Order)
{
    public string Number => Order.Number;
    public DateTime Date => Order.Date;
    public string SupplierName => Order.Supplier?.CompanyName ?? "";
    public decimal Total => Order.Total;
    public decimal Remaining => Order.Remaining;
}

/// <summary>Ligne du détail d'un bon : un produit acheté, ou le montant payé au fournisseur (<see cref="IsPayment"/>).</summary>
public record PurchaseLineRow(string ProductName, decimal? Quantity, decimal? UnitCost, decimal LineTotal, bool IsPayment = false)
{
    public static PurchaseLineRow From(PurchaseLine line) => new(line.Product?.Name ?? "", line.Quantity, line.UnitCost, line.LineTotal);

    public static PurchaseLineRow Payment(decimal amount) => new("Payé", null, null, amount, IsPayment: true);
}

public partial class PurchasesViewModel : ViewModelBase
{
    private readonly PurchaseService _purchases;
    private readonly SupplierService _suppliers;
    private readonly ProductService _products;
    private List<PurchaseOrder> _allOrders = new();
    private Dictionary<int, decimal> _lastPrices = new();

    public PurchasesViewModel(PurchaseService purchases, SupplierService suppliers, ProductService products)
    {
        _purchases = purchases;
        _suppliers = suppliers;
        _products = products;
        Lines.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
                foreach (PurchaseLineEditor l in e.NewItems)
                {
                    l.PropertyChanged += (_, args) =>
                    {
                        if (args.PropertyName == nameof(PurchaseLineEditor.Product)) PrefillPrice(l);
                        RefreshTotals();
                    };
                }
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

    [ObservableProperty] private string _historySearch = "";
    [ObservableProperty] private DateTime? _historyFrom;
    [ObservableProperty] private DateTime? _historyTo;
    [ObservableProperty] private string _historyAmountMin = "";
    [ObservableProperty] private string _historyAmountMax = "";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditCommand), nameof(DeleteCommand))]
    private PurchaseRow? _selectedPurchase;

    /// <summary>Bon en cours de modification dans le formulaire, ou null pour un nouveau bon.</summary>
    private PurchaseOrder? _editingOrder;

    public string FormTitle => _editingOrder == null ? "Nouveau bon d'achat" : $"Modifier le bon {_editingOrder.Number}";
    public string SaveLabel => _editingOrder == null ? "Enregistrer le bon" : "Enregistrer les modifications";
    public bool IsEditing => _editingOrder != null;

    [ObservableProperty] private Supplier? _supplier;
    [ObservableProperty] private PurchaseLineEditor? _selectedLine;
    [ObservableProperty] private string _paidText = "";

    public decimal Total => Lines.Sum(l => l.Total ?? 0m);
    public decimal Remaining => Total - (ParseDecimal(PaidText) ?? 0m);

    partial void OnPaidTextChanged(string value) => RefreshTotals();
    partial void OnHistorySearchChanged(string value) => ApplyHistoryFilter();
    partial void OnHistoryFromChanged(DateTime? value) => ApplyHistoryFilter();
    partial void OnHistoryToChanged(DateTime? value) => ApplyHistoryFilter();
    partial void OnHistoryAmountMinChanged(string value) => ApplyHistoryFilter();
    partial void OnHistoryAmountMaxChanged(string value) => ApplyHistoryFilter();

    partial void OnSupplierChanged(Supplier? value) => _ = LoadSupplierRatesAsync(value);

    partial void OnSelectedPurchaseChanged(PurchaseRow? value)
    {
        SelectedDetails.Clear();
        if (value == null) return;
        foreach (var l in value.Order.Lines) SelectedDetails.Add(PurchaseLineRow.From(l));
        if (value.Order.AmountPaid > 0) SelectedDetails.Add(PurchaseLineRow.Payment(value.Order.AmountPaid));
    }

    private void RefreshTotals()
    {
        OnPropertyChanged(nameof(Total));
        OnPropertyChanged(nameof(Remaining));
    }

    private async Task LoadSupplierRatesAsync(Supplier? supplier)
    {
        if (supplier == null)
        {
            _lastPrices = new();
            foreach (var line in Lines) PrefillPrice(line);
            return;
        }
        await TryAsync(async () =>
        {
            var prices = await _purchases.GetLastPricesAsync(supplier.Id);
            if (Supplier?.Id != supplier.Id) return;
            _lastPrices = prices;
            foreach (var line in Lines) PrefillPrice(line);
        });
    }

    /// <summary>
    /// Propose le dernier coefficient de ce fournisseur pour ce produit. Un coefficient saisi à la main n'est jamais remplacé ;
    /// un coefficient proposé automatiquement est remplacé si le fournisseur ou le produit change.
    /// </summary>
    private void PrefillPrice(PurchaseLineEditor line)
    {
        if (!string.IsNullOrWhiteSpace(line.UnitCostText) && !line.IsPriceAutoFilled) return;
        line.SetAutoPrice(line.Product != null && _lastPrices.TryGetValue(line.Product.Id, out var price)
            ? price.ToString("0.####")
            : "");
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
        _allOrders = await _purchases.ListAsync();
        ApplyHistoryFilter();
    }

    private void ApplyHistoryFilter()
    {
        var term = HistorySearch.Trim();
        var min = ParseDecimal(HistoryAmountMin);
        var max = ParseDecimal(HistoryAmountMax);

        var rows = _allOrders.Where(o => term.Length == 0
                || o.Number.Contains(term, StringComparison.CurrentCultureIgnoreCase)
                || (o.Supplier?.CompanyName.Contains(term, StringComparison.CurrentCultureIgnoreCase) ?? false))
            .Where(o => HistoryFrom == null || o.Date.Date >= HistoryFrom.Value.Date)
            .Where(o => HistoryTo == null || o.Date.Date <= HistoryTo.Value.Date)
            .Where(o => min == null || o.Total >= min)
            .Where(o => max == null || o.Total <= max);

        History.Clear();
        foreach (var o in rows) History.Add(new PurchaseRow(o));
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
        SetEditingOrder(null);
        Supplier = null;
        PaidText = "";
        Lines.Clear();
        AddLine();
    }

    private void SetEditingOrder(PurchaseOrder? order)
    {
        _editingOrder = order;
        OnPropertyChanged(nameof(FormTitle));
        OnPropertyChanged(nameof(SaveLabel));
        OnPropertyChanged(nameof(IsEditing));
    }

    private bool HasDraft => Supplier != null || !string.IsNullOrWhiteSpace(PaidText) || Lines.Any(l => l.Product != null
        || !string.IsNullOrWhiteSpace(l.QuantityText) || !string.IsNullOrWhiteSpace(l.UnitCostText));

    [RelayCommand]
    private void Reset() => ResetForm();

    /// <summary>Formulaire du bon affiché par-dessus la liste. Le fermer sans enregistrer garde la saisie en cours.</summary>
    [ObservableProperty] private bool _isFormOpen;

    [RelayCommand]
    private void OpenForm()
    {
        if (_editingOrder != null) ResetForm(); // on quitte la modification d'un bon pour en créer un nouveau
        IsFormOpen = true;
    }

    [RelayCommand] private void CloseForm() => IsFormOpen = false;

    private bool HasSelection() => SelectedPurchase != null;

    /// <summary>Charge le bon sélectionné dans le formulaire pour le modifier (il garde son numéro et sa date).</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Edit()
    {
        if (SelectedPurchase == null) return;
        var order = SelectedPurchase.Order;
        if (_editingOrder == null && HasDraft
            && !Confirm("Un nouveau bon d'achat est en cours de saisie. L'abandonner pour modifier le bon sélectionné ?")) return;

        var inactive = order.Lines.FirstOrDefault(l => Products.All(p => p.Id != l.ProductId));
        if (inactive != null)
        {
            Info($"Le produit « {inactive.Product?.Name} » est désactivé : réactivez-le pour pouvoir modifier ce bon.");
            return;
        }

        ResetForm();
        SetEditingOrder(order);
        Supplier = Suppliers.FirstOrDefault(s => s.Id == order.SupplierId);
        Lines.Clear();
        foreach (var l in order.Lines)
        {
            var editor = new PurchaseLineEditor();
            Lines.Add(editor);
            editor.Product = Products.First(p => p.Id == l.ProductId);
            editor.QuantityText = l.Quantity.ToString("0.##");
            editor.UnitCostText = l.UnitCost.ToString("0.####");
        }
        if (Lines.Count == 0) AddLine();
        PaidText = order.AmountPaid > 0 ? order.AmountPaid.ToString("0.##") : "";
        IsFormOpen = true;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteAsync()
    {
        if (SelectedPurchase == null) return;
        var order = SelectedPurchase.Order;
        if (!Confirm($"Supprimer le bon d'achat {order.Number} ({order.Total:N2} DA) de « {order.Supplier?.CompanyName} » ?\n\n" +
                     "Ses produits seront retirés du stock et la dette envers le fournisseur corrigée.\nCette action est définitive.")) return;

        if (!await TryAsync(() => _purchases.DeleteAsync(order.Id))) return;
        if (_editingOrder?.Id == order.Id)
        {
            ResetForm();
            IsFormOpen = false;
        }
        await ReloadAfterChangeAsync();
        Info($"Bon {order.Number} supprimé.");
    }

    private Task ReloadAfterChangeAsync() => TryAsync(async () =>
    {
        await LoadHistoryAsync();
        Products.Clear();
        foreach (var p in await _products.ListAsync()) Products.Add(p);
    });

    [RelayCommand]
    private Task SaveAsync() => SaveCoreAsync(print: false);

    [RelayCommand]
    private Task SaveAndPrintAsync() => SaveCoreAsync(print: true);

    /// <summary>Imprime le bon en cours de modification tel qu'il est enregistré (sans les changements non enregistrés).</summary>
    [RelayCommand]
    private async Task PrintEditingAsync()
    {
        if (_editingOrder != null) await PrintOrderAsync(_editingOrder.Id);
    }

    private async Task PrintOrderAsync(int orderId)
    {
        PurchaseOrder? order = null;
        if (!await TryAsync(async () => order = await _purchases.GetAsync(orderId)) || order == null) return;

        var supplier = order.Supplier;
        PrintHelper.PrintBon("Bon d'achat", order.Number, order.Date,
            [("Fournisseur", supplier?.CompanyName ?? ""), ("Référence", supplier?.Reference ?? ""),
             ("Adresse", supplier?.Address ?? ""), ("Téléphone", supplier?.Phone1 ?? "")],
            ["Produit", "Quantité / montant", "Coef. / prix", "Coût (DA)"],
            order.Lines.Select(l => new[]
            {
                l.Product?.Name ?? "", l.Quantity.ToString("N2"), l.UnitCost.ToString("0.####"), l.LineTotal.ToString("N2"),
            }).ToList(),
            [("Total du bon", $"{order.Total:N2} DA"), ("Montant payé", $"{order.AmountPaid:N2} DA"),
             ("Reste dû au fournisseur", $"{order.Remaining:N2} DA")]);
    }

    private async Task SaveCoreAsync(bool print)
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

        var editing = _editingOrder;
        PurchaseOrder? saved = null;
        var ok = await TryAsync(async () => saved = editing == null
            ? await _purchases.CreateAsync(new PurchaseInput(Supplier.Id, DateTime.Now, lines, paid.Value))
            : await _purchases.UpdateAsync(editing.Id, new PurchaseInput(Supplier.Id, editing.Date, lines, paid.Value)));
        if (!ok || saved == null) return;

        ResetForm();
        IsFormOpen = false;
        await ReloadAfterChangeAsync();
        Info(editing == null
            ? $"Bon d'achat {saved.Number} enregistré ({saved.Total:N2} DA). Le stock a été mis à jour."
            : $"Bon d'achat {saved.Number} modifié ({saved.Total:N2} DA). Le stock, la dette fournisseur et le rapport ont été mis à jour.");
        if (print) await PrintOrderAsync(saved.Id);
    }

    [RelayCommand]
    private void Print()
    {
        PrintHelper.PrintTable("Bons d'achat",
            ["N°", "Date", "Fournisseur", "Total (DA)", "Reste dû (DA)"],
            History.Select(r => new[] { r.Number, r.Date.ToString("dd/MM/yyyy"), r.SupplierName, r.Total.ToString("N2"), r.Remaining.ToString("N2") }).ToList());
    }
}
