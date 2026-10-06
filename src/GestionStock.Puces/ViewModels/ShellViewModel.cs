using CommunityToolkit.Mvvm.ComponentModel;
using GestionStock.Puces.Settings;

namespace GestionStock.Puces.ViewModels;

public partial class ShellViewModel : ObservableObject
{
    public ShellViewModel() => ShowPortSetup();

    [ObservableProperty] private object? _currentPage;

    private void ShowPortSetup() => CurrentPage = new PortSetupViewModel(PortSettingsStore.Load(), ShowModems);

    private void ShowModems(OperatorPorts ports)
    {
        var modems = new ModemsViewModel(
            new[]
            {
                new OperatorChat("Mobilis", ports.Mobilis, "*632*01*00000#"),
                new OperatorChat("Djezzy", ports.Djezzy, "*766#"),
                new OperatorChat("Ooredoo", ports.Ooredoo, ""),
            },
            ShowPortSetup);
        CurrentPage = modems;
        _ = modems.TestConnectionsAsync();
    }
}
