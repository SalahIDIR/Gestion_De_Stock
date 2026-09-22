using System.Windows;
using GestionStock.App.ViewModels;
using GestionStock.Core.Domain;

namespace GestionStock.App.Views;

public partial class LoginWindow : Window
{
    private readonly LoginViewModel _vm;

    public User? LoggedInUser => _vm.LoggedInUser;

    public LoginWindow(LoginViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        vm.LoggedIn += () => DialogResult = true;
        Loaded += async (_, _) =>
        {
            await vm.InitializeAsync();
            UsernameBox.Focus();
        };
    }
}
