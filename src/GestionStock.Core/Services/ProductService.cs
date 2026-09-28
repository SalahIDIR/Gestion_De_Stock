using GestionStock.Core.Data;
using GestionStock.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace GestionStock.Core.Services;

public class ProductService
{
    /// <summary>Palette assignée automatiquement aux nouveaux produits qui n'ont pas de couleur choisie.</summary>
    private static readonly string[] DefaultPalette =
        ["#F59E0B", "#DC2626", "#16A34A", "#2563EB", "#7C3AED", "#0EA5E9", "#DB2777", "#65A30D"];

    private readonly IDbContextFactory<AppDbContext> _factory;

    public ProductService(IDbContextFactory<AppDbContext> factory) => _factory = factory;

    public async Task<List<Product>> ListAsync(bool includeInactive = false)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var query = db.Products.AsNoTracking().AsQueryable();
        if (!includeInactive) query = query.Where(p => p.IsActive);
        return await query.OrderBy(p => p.Kind).ThenBy(p => p.Name).ToListAsync();
    }

    public async Task<Product> SaveAsync(Product input)
    {
        var name = input.Name.Trim();
        if (name.Length == 0) throw new BusinessException("Le nom du produit est obligatoire.");
        var colorHex = string.IsNullOrWhiteSpace(input.ColorHex) ? null : input.ColorHex.Trim();
        if (colorHex != null && !System.Text.RegularExpressions.Regex.IsMatch(colorHex, "^#[0-9A-Fa-f]{6}$"))
            throw new BusinessException("La couleur doit être au format #RRGGBB.");

        await using var db = await _factory.CreateDbContextAsync();
        if (await db.Products.AnyAsync(p => p.Name == name && p.Id != input.Id))
            throw new BusinessException($"Le produit « {name} » existe déjà.");

        Product entity;
        if (input.Id == 0)
        {
            var used = await db.Products.CountAsync();
            entity = new Product { Kind = input.Kind, ColorHex = colorHex ?? DefaultPalette[used % DefaultPalette.Length] };
            db.Products.Add(entity);
        }
        else
        {
            entity = await db.Products.FindAsync(input.Id) ?? throw new BusinessException("Produit introuvable.");
            if (entity.Kind != input.Kind && await db.StockMovements.AnyAsync(m => m.ProductId == entity.Id))
                throw new BusinessException("Le type d'un produit ayant des mouvements de stock ne peut plus être changé.");
            entity.Kind = input.Kind;
            if (colorHex != null) entity.ColorHex = colorHex;
        }

        entity.Name = name;
        entity.IsActive = input.IsActive;
        await db.SaveChangesAsync();
        return entity;
    }

    /// <summary>Supprime un produit sans historique ; sinon il faut le désactiver.</summary>
    public async Task DeleteAsync(int id)
    {
        await using var db = await _factory.CreateDbContextAsync();
        if (await db.StockMovements.AnyAsync(m => m.ProductId == id))
            throw new BusinessException("Ce produit a des mouvements de stock : désactivez-le plutôt que de le supprimer.");
        var entity = await db.Products.FindAsync(id);
        if (entity == null) return;
        db.Products.Remove(entity);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Correction d'inventaire : enregistre un mouvement d'ajustement pour atteindre le solde compté, et peut aussi
    /// fixer le prix d'achat de référence du produit (laissé à null pour ne pas y toucher).
    /// </summary>
    public async Task AdjustStockAsync(int productId, decimal countedBalance, string? note, decimal? purchaseCost = null)
    {
        if (countedBalance < 0) throw new BusinessException("Le solde compté ne peut pas être négatif.");
        if (purchaseCost is <= 0) throw new BusinessException("Le prix d'achat doit être positif.");

        await using var db = await _factory.CreateDbContextAsync();
        var product = await db.Products.FindAsync(productId) ?? throw new BusinessException("Produit introuvable.");
        if (purchaseCost is { } cost && product.Kind == ProductKind.VirtualCredit && cost > PurchaseService.MaxVirtualCoefficient)
            throw new BusinessException($"Le coefficient ({cost}) est trop élevé : saisissez par exemple 0.9725.");

        var delta = countedBalance - product.StockBalance;
        if (delta == 0 && purchaseCost == null) return;

        if (delta != 0)
        {
            db.StockMovements.Add(new StockMovement
            {
                Date = DateTime.Now,
                ProductId = productId,
                Quantity = delta,
                Kind = StockMovementKind.Adjustment,
                Note = string.IsNullOrWhiteSpace(note) ? "Correction d'inventaire" : note.Trim(),
            });
            product.StockBalance = countedBalance;
        }
        if (purchaseCost != null) product.ReferencePurchaseCost = purchaseCost;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Prix d'achat de chaque produit sur son bon d'achat le plus récent (coefficient pour le crédit virtuel,
    /// prix unitaire pour un produit physique), quel que soit le fournisseur. Clé = ProductId ; absent si jamais acheté.
    /// </summary>
    public async Task<Dictionary<int, decimal>> GetLastPurchaseCostsAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        var lines = await db.PurchaseLines.AsNoTracking()
            .Join(db.PurchaseOrders, l => l.PurchaseOrderId, o => o.Id,
                (l, o) => new { l.ProductId, l.UnitCost, o.Date, OrderId = o.Id, LineId = l.Id })
            .ToListAsync();
        var costs = lines.GroupBy(l => l.ProductId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(l => l.Date).ThenByDescending(l => l.OrderId)
                .ThenByDescending(l => l.LineId).First().UnitCost);

        var references = await db.Products.AsNoTracking().Where(p => p.ReferencePurchaseCost != null)
            .Select(p => new { p.Id, p.ReferencePurchaseCost }).ToListAsync();
        foreach (var r in references)
            if (!costs.ContainsKey(r.Id)) costs[r.Id] = r.ReferencePurchaseCost!.Value;

        return costs;
    }

    /// <summary>Valeur totale du stock, tous produits confondus, au dernier prix d'achat connu de chacun.</summary>
    public async Task<decimal> GetTotalStockValueAsync()
    {
        var products = await ListAsync(includeInactive: true);
        var lastCosts = await GetLastPurchaseCostsAsync();
        return products.Sum(p => lastCosts.TryGetValue(p.Id, out var cost)
            ? Math.Round(p.StockBalance * cost, 2, MidpointRounding.AwayFromZero) : 0m);
    }

    public async Task<List<StockMovement>> GetMovementsAsync(int productId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var rows = await db.StockMovements.AsNoTracking().Where(m => m.ProductId == productId).ToListAsync();
        return rows.OrderByDescending(m => m.Date).ThenByDescending(m => m.Id).ToList();
    }
}
