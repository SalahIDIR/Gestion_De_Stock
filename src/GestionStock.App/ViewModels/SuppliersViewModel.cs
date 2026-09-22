using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    public SuppliersViewModel(SupplierService service)
    {
        _service = service;
        _ = LoadAsync();
    }

    public ObservableCollection<SupplierRow> Items { get; } = new();

    [ObservableProperty] private SupplierRow? _selected;
    [ObservableProperty] private int _editId;
    [ObservableProperty] private string _reference = "";
    [ObservableProperty] private string _companyName = "";
    [ObservableProperty] private string _address = "";
    [ObservableProperty] private string _phone1 = "";
    [ObservableProperty] private string _phone2 = "";

    public string FormTitle => EditId == 0 ? "Nouveau fournisseur" : "Modifier le fournisseur";

    partial void OnEditIdChanged(int value) => OnPropertyChanged(nameof(FormTitle));

    partial void OnSelectedChanged(SupplierRow? value)
    {
        if (value == null) return;
        var s = value.Supplier;
        EditId = s.Id;
        Reference = s.Reference;
        CompanyName = s.CompanyName;
        Address = s.Address ?? "";
        Phone1 = s.Phone1 ?? "";
        Phone2 = s.Phone2 ?? "";
    }

    private async Task LoadAsync()
    {
        await TryAsync(async () =>
        {
            var suppliers = await _service.ListAsync();
            var debts = await _service.GetDebtsAsync();
            Items.Clear();
            foreach (var s in suppliers) Items.Add(new SupplierRow(s, debts.GetValueOrDefault(s.Id)));
        });
    }

    [RelayCommand]
    private void New()
    {
        Selected = null;
        EditId = 0;
        Reference = CompanyName = Address = Phone1 = Phone2 = "";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var ok = await TryAsync(async () => await _service.SaveAsync(new Supplier
        {
            Id = EditId, Reference = Reference, CompanyName = CompanyName,
            Address = Address, Phone1 = Phone1, Phone2 = Phone2,
        }));
        if (!ok) return;
        New();
        await LoadAsync();
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (EditId == 0 || !Confirm($"Supprimer le fournisseur « {CompanyName} » ?")) return;
        if (await TryAsync(() => _service.DeleteAsync(EditId)))
        {
            New();
            await LoadAsync();
        }
    }
}
