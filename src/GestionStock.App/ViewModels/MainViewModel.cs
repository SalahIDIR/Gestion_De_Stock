using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GestionStock.Core.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace GestionStock.App.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly IServiceProvider _services;

    public MainViewModel(IServiceProvider services, User user)
    {
        _services = services;
        CurrentUser = user;
        ShowDeliveries();
    }

    public User CurrentUser { get; }

    [ObservableProperty] private ViewModelBase? _currentPage;

    [RelayCommand] private void ShowSuppliers() => CurrentPage = ActivatorUtilities.CreateInstance<SuppliersViewModel>(_services);
    [RelayCommand] private void ShowClients() => CurrentPage = ActivatorUtilities.CreateInstance<ClientsViewModel>(_services);
    [RelayCommand] private void ShowProducts() => CurrentPage = ActivatorUtilities.CreateInstance<ProductsViewModel>(_services);
    [RelayCommand] private void ShowDeliveries() => CurrentPage = ActivatorUtilities.CreateInstance<DeliveriesViewModel>(_services);
    [RelayCommand] private void ShowPurchases() => CurrentPage = ActivatorUtilities.CreateInstance<PurchasesViewModel>(_services);
    [RelayCommand] private void ShowSettings() => CurrentPage = ActivatorUtilities.CreateInstance<SettingsViewModel>(_services, CurrentUser);
}
