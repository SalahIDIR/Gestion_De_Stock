using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GestionStock.Core.Domain;
using GestionStock.Core.Services;

namespace GestionStock.App.ViewModels;

public partial class LoginViewModel : ViewModelBase
{
    private readonly AuthService _auth;

    public LoginViewModel(AuthService auth) => _auth = auth;

    /// <summary>Vrai au premier lancement : aucun compte n'existe, on demande d'en créer un.</summary>
    [ObservableProperty] private bool _isFirstRun;
    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _confirmPassword = "";
    [ObservableProperty] private string _error = "";

    public User? LoggedInUser { get; private set; }
    public event Action? LoggedIn;

    public string Heading => IsFirstRun ? "Création du compte administrateur" : "Connexion";
    public string SubmitLabel => IsFirstRun ? "Créer le compte" : "Se connecter";

    partial void OnIsFirstRunChanged(bool value)
    {
        OnPropertyChanged(nameof(Heading));
        OnPropertyChanged(nameof(SubmitLabel));
    }

    public async Task InitializeAsync() => IsFirstRun = !await _auth.HasUsersAsync();

    [RelayCommand]
    private async Task SubmitAsync()
    {
        Error = "";
        try
        {
            if (IsFirstRun)
            {
                if (Password != ConfirmPassword) { Error = "Les deux mots de passe sont différents."; return; }
                LoggedInUser = await _auth.CreateAdminAsync(Username, Password);
            }
            else
            {
                LoggedInUser = await _auth.LoginAsync(Username, Password);
                if (LoggedInUser == null) { Error = "Nom d'utilisateur ou mot de passe incorrect."; Password = ""; return; }
            }
            LoggedIn?.Invoke();
        }
        catch (BusinessException ex)
        {
            Error = ex.Message;
        }
        catch (Exception ex)
        {
            Error = $"Erreur inattendue : {ex.Message}";
        }
    }
}
