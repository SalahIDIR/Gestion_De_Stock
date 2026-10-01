using GestionStock.Core.Data;
using GestionStock.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace GestionStock.Core.Services;

/// <summary>Aperçu, calculé mais non enregistré, de la prochaine clôture à un instant donné.</summary>
public record AccountClosingPreview(
    DateTime? PreviousDate, decimal PreviousCash, decimal PreviousTotal,
    decimal TotalRecettes, decimal TotalDepenses, decimal Cash,
    decimal StockValue, decimal ClientCredit, decimal SupplierCredit);

/// <summary>
/// « Solde des comptes » : clôture de caisse enchaînée. Chaque clôture part de l'espèce et du total de la précédente,
/// ajoute l'argent réellement encaissé et dépensé depuis, et produit un nouveau solde et un bénéfice figés dans
/// l'historique une fois validée.
/// </summary>
public class AccountClosingService
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly ReportService _reports;
    private readonly ProductService _products;
    private readonly DeliveryService _deliveries;
    private readonly SupplierService _suppliers;

    public AccountClosingService(IDbContextFactory<AppDbContext> factory, ReportService reports,
        ProductService products, DeliveryService deliveries, SupplierService suppliers)
    {
        _factory = factory;
        _reports = reports;
        _products = products;
        _deliveries = deliveries;
        _suppliers = suppliers;
    }

    public async Task<AccountClosing?> GetLastAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.AccountClosings.AsNoTracking().OrderByDescending(c => c.Date).ThenByDescending(c => c.Id).FirstOrDefaultAsync();
    }

    public async Task<List<AccountClosing>> ListAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        var rows = await db.AccountClosings.AsNoTracking().ToListAsync();
        return rows.OrderByDescending(c => c.Date).ThenByDescending(c => c.Id).ToList();
    }

    /// <summary>Calcule la situation à l'instant <paramref name="now"/>, sans rien enregistrer.</summary>
    public async Task<AccountClosingPreview> PreviewAsync(DateTime now)
    {
        var last = await GetLastAsync();
        var cash = await _reports.GetCashSummarySinceAsync(last?.Date, now);
        var stockValue = await _products.GetTotalStockValueAsync();
        var clientCredit = (await _deliveries.GetDebtsAsync()).Values.Sum();
        var supplierCredit = (await _suppliers.GetDebtsAsync()).Values.Sum();
        var previousCash = last?.Cash ?? 0m;

        return new AccountClosingPreview(last?.Date, previousCash, last?.Total ?? 0m,
            cash.TotalVersements, cash.TotalDepenses, previousCash + cash.TotalVersements - cash.TotalDepenses,
            stockValue, clientCredit, supplierCredit);
    }

    /// <summary>
    /// Enregistre définitivement une nouvelle clôture ; elle devient le point de départ de la suivante.
    /// <paramref name="cashOverride"/> permet de remplacer l'espèce calculée automatiquement (avant prélèvement) par
    /// une valeur saisie à la main. L'espèce enregistrée — et donc reprise comme « ancien espèce » par la clôture
    /// suivante — est toujours nette du prélèvement : l'argent sorti de la caisse n'y est plus.
    /// </summary>
    public async Task<AccountClosing> ValidateAsync(DateTime now, decimal prelevements, string? comments, decimal? cashOverride = null)
    {
        if (prelevements < 0) throw new BusinessException("Le prélèvement ne peut pas être négatif.");

        var preview = await PreviewAsync(now);
        var grossCash = cashOverride ?? preview.Cash;
        var cash = grossCash - prelevements;
        var total = preview.StockValue + preview.ClientCredit + cash - preview.SupplierCredit;
        var benefice = total - preview.PreviousTotal;
        var days = preview.PreviousDate.HasValue ? Math.Max(1m, (decimal)(now - preview.PreviousDate.Value).TotalDays) : 1m;
        var moyenne = Math.Round(benefice / days, 2, MidpointRounding.AwayFromZero);

        await using var db = await _factory.CreateDbContextAsync();
        // On revérifie qu'aucune autre clôture n'a été enregistrée entre-temps (ex. deux fenêtres ouvertes en même temps).
        var last = await db.AccountClosings.AsNoTracking().OrderByDescending(c => c.Date).ThenByDescending(c => c.Id).FirstOrDefaultAsync();
        if (last?.Date != preview.PreviousDate)
            throw new BusinessException("Une autre clôture a été enregistrée entre-temps. Rouvrez la page pour recalculer avant de valider.");

        var closing = new AccountClosing
        {
            Date = now,
            PreviousCash = preview.PreviousCash,
            TotalRecettes = preview.TotalRecettes,
            TotalDepenses = preview.TotalDepenses,
            Cash = cash,
            StockValue = preview.StockValue,
            ClientCredit = preview.ClientCredit,
            SupplierCredit = preview.SupplierCredit,
            Prelevements = prelevements,
            Total = total,
            Benefice = benefice,
            Moyenne = moyenne,
            Comments = string.IsNullOrWhiteSpace(comments) ? null : comments.Trim(),
        };
        db.AccountClosings.Add(closing);
        await db.SaveChangesAsync();
        return closing;
    }
}
