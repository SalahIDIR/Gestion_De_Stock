using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Markup;
using GestionStock.App.Services;
using GestionStock.App.ViewModels;
using GestionStock.App.Views;
using GestionStock.Core.Data;
using GestionStock.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GestionStock.App;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    /// <summary>Base SQLite de l'application. La variable d'environnement GESTIONSTOCK_DB permet d'en utiliser une autre (tests).</summary>
    public static string DatabasePath { get; } =
        Environment.GetEnvironmentVariable("GESTIONSTOCK_DB") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GestionStock", "gestionstock.db");

    public App()
    {
        // La culture doit être réglée ici, de façon synchrone : dans .NET elle suit le contexte d'exécution,
        // donc un réglage fait dans OnStartup (async) ne serait pas vu par le reste de l'interface.
        var culture = new CultureInfo("fr-FR");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        // Les contrôles (FrameworkElement) et le texte en ligne (Run, Span... = TextElement) ont chacun leur langue par défaut.
        var language = XmlLanguage.GetLanguage(culture.IetfLanguageTag);
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(language));
        FrameworkContentElement.LanguageProperty.OverrideMetadata(typeof(System.Windows.Documents.TextElement), new FrameworkPropertyMetadata(language));
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
            Services = BuildServices();
            await DatabaseInitializer.InitializeAsync(Services.GetRequiredService<IDbContextFactory<AppDbContext>>());
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Impossible d'ouvrir la base de données :\n{ex.Message}", "Gestion Stock",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var login = new LoginWindow(new LoginViewModel(Services.GetRequiredService<AuthService>()));
        if (login.ShowDialog() != true || login.LoggedInUser == null)
        {
            Shutdown();
            return;
        }

        // Test des modems des 3 opérateurs : en cas de problème, un message est affiché mais l'application s'ouvre quand même.
        new ConnectionCheckWindow(Services.GetRequiredService<OperatorStatusViewModel>()).ShowDialog();

        var main = new MainWindow(new MainViewModel(Services, login.LoggedInUser));
        MainWindow = main;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        main.Show();
    }

    private static IServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<AppDbContext>(o => o.UseSqlite($"Data Source={DatabasePath}"));
        services.AddSingleton<AuthService>();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<SupplierService>();
        services.AddSingleton<ClientService>();
        services.AddSingleton<ProductService>();
        services.AddSingleton<PurchaseService>();
        services.AddSingleton<DeliveryService>();
        services.AddSingleton<ReportService>();
        services.AddSingleton<AccountClosingService>();
        services.AddSingleton<DemoDataService>();
        services.AddSingleton<IModemPort, SerialModemPort>();
        services.AddSingleton<CreditTransferService>();
        services.AddSingleton<VoiceCommandService>();
        services.AddSingleton<OperatorStatusViewModel>();
        return services.BuildServiceProvider();
    }
}
