using System.Windows;
using GestionStock.App.ViewModels;

namespace GestionStock.App.Views;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
