using System.Collections.ObjectModel;
using System.IO.Ports;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GestionStock.Puces.Settings;

namespace GestionStock.Puces.ViewModels;

public partial class PortSetupViewModel : ObservableObject
{
    private readonly Action<OperatorPorts> _onContinue;

    public PortSetupViewModel(OperatorPorts saved, Action<OperatorPorts> onContinue)
    {
        _onContinue = onContinue;
        FillPorts();
        _mobilisPort = KeepSaved(saved.Mobilis);
        _djezzyPort = KeepSaved(saved.Djezzy);
        _ooredooPort = KeepSaved(saved.Ooredoo);
    }

    public ObservableCollection<string> Ports { get; } = new();

    [ObservableProperty] private string? _mobilisPort;
    [ObservableProperty] private string? _djezzyPort;
    [ObservableProperty] private string? _ooredooPort;
    [ObservableProperty] private string _message = "";

    [RelayCommand]
    private void RefreshPorts()
    {
        FillPorts();
        if (MobilisPort != null && !Ports.Contains(MobilisPort)) Ports.Add(MobilisPort);
        if (DjezzyPort != null && !Ports.Contains(DjezzyPort)) Ports.Add(DjezzyPort);
        if (OoredooPort != null && !Ports.Contains(OoredooPort)) Ports.Add(OoredooPort);
    }

    [RelayCommand]
    private void Continue()
    {
        var chosen = new[] { MobilisPort, DjezzyPort, OoredooPort }.Where(p => p != null).ToList();
        if (chosen.Distinct().Count() != chosen.Count)
        {
            Message = "Deux opérateurs ne peuvent pas utiliser le même port.";
            return;
        }

        var ports = new OperatorPorts(MobilisPort, DjezzyPort, OoredooPort);
        PortSettingsStore.Save(ports);
        _onContinue(ports);
    }

    private void FillPorts()
    {
        Ports.Clear();
        foreach (var name in SerialPort.GetPortNames().OrderBy(n => n)) Ports.Add(name);
    }

    /// <summary>Garde le port enregistré même si la clé n'est pas branchée au moment de l'ouverture.</summary>
    private string? KeepSaved(string? saved)
    {
        if (saved != null && !Ports.Contains(saved)) Ports.Add(saved);
        return saved;
    }
}
