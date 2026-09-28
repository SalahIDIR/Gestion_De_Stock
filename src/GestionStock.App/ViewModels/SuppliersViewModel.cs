using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GestionStock.App.Services;
using GestionStock.Core.Domain;
using GestionStock.Core.Services;

namespace GestionStock.App.ViewModels;

public record SupplierRow(Supplier Supplier, decimal Debt)
{
    public string Reference => Supplier.Reference;
    public string CompanyName => Supplier.CompanyName;
    public string? Phone => Supplier.Phone1;
}

public partial class SuppliersViewModel : ViewModelBase
{
    private readonly SupplierService _service;
    private List<Supplier> _all = new();
    private Dictionary<int, decimal> _debts = new();

    public SuppliersViewModel(SupplierService service)
    {
        _service = service;
        _ = LoadAsync();
    }

    public ObservableCollection<SupplierRow> Items { get; } = new();

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _debtMin = "";
    [ObservableProperty] private string _debtMax = "";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditCommand), nameof(DeleteCommand))]
    private SupplierRow? _selected;

    /// <summary>Formulaire du fournisseur affiché par-dessus la liste. Le fermer sans enregistrer garde la saisie en cours.</summary>
    [ObservableProperty] private bool _isFormOpen;
    [ObservableProperty] private int _editId;
    [ObservableProperty] private string _reference = "";
    [ObservableProperty] private string _companyName = "";
    [ObservableProperty] private string _address = "";
    [ObservableProperty] private string _phone1 = "";
    [ObservableProperty] private string _phone2 = "";
    [ObservableProperty] private string _openingBalance = "";

    public string FormTitle => EditId == 0 ? "Nouveau fournisseur" : "Modifier le fournisseur";

    partial void OnEditIdChanged(int value) => OnPropertyChanged(nameof(FormTitle));
    partial void OnSearchChanged(string value) => ApplyFilter();
    partial void OnDebtMinChanged(string value) => ApplyFilter();
    partial void OnDebtMaxChanged(string value) => ApplyFilter();

    private bool HasSelection() => Selected != null;

    [RelayCommand]
    private void OpenForm()
    {
        if (EditId != 0) New(); // on quitte la modification d'un fournisseur pour en créer un nouveau
        IsFormOpen = true;
    }

    [RelayCommand] private void CloseForm() => IsFormOpen = false;

    /// <summary>Charge le fournisseur sélectionné dans le formulaire pour le modifier.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Edit()
    {
        if (Selected is not { } value) return;
        var s = value.Supplier;
        EditId = s.Id;
        Reference = s.Reference;
        CompanyName = s.CompanyName;
        Address = s.Address ?? "";
        Phone1 = s.Phone1 ?? "";
        Phone2 = s.Phone2 ?? "";
        OpeningBalance = s.OpeningBalance == 0 ? "" : s.OpeningBalance.ToString("0.##");
        IsFormOpen = true;
    }

    private async Task LoadAsync()
    {
        await TryAsync(async () =>
        {
            _all = await _service.ListAsync();
            _debts = await _service.GetDebtsAsync();
            ApplyFilter();
        });
    }

    private void ApplyFilter()
    {
        var term = Search.Trim();
        var min = ParseDecimal(DebtMin);
        var max = ParseDecimal(DebtMax);

        var rows = _all
            .Where(s => term.Length == 0
                || s.CompanyName.Contains(term, StringComparison.CurrentCultureIgnoreCase)
                || s.Reference.Contains(term, StringComparison.CurrentCultureIgnoreCase))
            .Select(s => new SupplierRow(s, _debts.GetValueOrDefault(s.Id)))
            .Where(r => min == null || r.Debt >= min)
            .Where(r => max == null || r.Debt <= max);

        Items.Clear();
        foreach (var r in rows) Items.Add(r);
    }

    /// <summary>Vide le formulaire pour saisir un nouveau fournisseur.</summary>
    [RelayCommand]
    private void New()
    {
        EditId = 0;
        Reference = CompanyName = Address = Phone1 = Phone2 = OpeningBalance = "";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var opening = string.IsNullOrWhiteSpace(OpeningBalance) ? 0m : ParseDecimal(OpeningBalance);
        if (opening == null)
        {
            Info("Le solde initial n'est pas un nombre valide.");
            return;
        }

        var ok = await TryAsync(async () => await _service.SaveAsync(new Supplier
        {
            Id = EditId, Reference = Reference, CompanyName = CompanyName,
            Address = Address, Phone1 = Phone1, Phone2 = Phone2, OpeningBalance = opening.Value,
        }));
        if (!ok) return;
        New();
        IsFormOpen = false;
        await LoadAsync();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteAsync()
    {
        if (Selected is not { } row || !Confirm($"Supprimer le fournisseur « {row.CompanyName} » ?\nCette action est définitive.")) return;
        if (!await TryAsync(() => _service.DeleteAsync(row.Supplier.Id))) return;
        if (EditId == row.Supplier.Id)
        {
            New();
            IsFormOpen = false;
        }
        await LoadAsync();
    }

    [RelayCommand]
    private void Print()
    {
        PrintHelper.PrintTable("Liste des fournisseurs",
            ["Référence", "Raison sociale", "Téléphone", "Dette (DA)"],
            Items.Select(r => new[] { r.Reference, r.CompanyName, r.Phone ?? "", r.Debt.ToString("N2") }).ToList());
    }
}
