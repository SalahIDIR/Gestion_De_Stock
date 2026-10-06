using System.Windows;
using GestionStock.Puces.ViewModels;

namespace GestionStock.Puces;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new ShellViewModel();
    }
}
