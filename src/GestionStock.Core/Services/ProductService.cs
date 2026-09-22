using GestionStock.Core.Data;
using GestionStock.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace GestionStock.Core.Services;

public class ProductService
{
    private readonly IDbContextFactory<AppDbContext> _factory;

    public ProductService(IDbContextFactory<AppDbContext> factory) => _factory = factory;

    public async Task<List<Product>> ListAsync(bool includeInactive = false)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var query = db.Products.AsNoTracking().Include(p => p.Operator).AsQueryable();
        if (!includeInactive) query = query.Where(p => p.IsActive);
        return await query.OrderBy(p => p.Kind).ThenBy(p => p.Name).ToListAsync();
    }

    public async Task<Product> SaveAsync(Product input)
    {
        var name = input.Name.Trim();
        if (name.Length == 0) throw new BusinessException("Le nom du produit est obligatoire.");
        if (input.Kind == ProductKind.VirtualCredit && input.OperatorId == null)
            throw new BusinessException("Un crédit virtuel doit être rattaché à un opérateur.");

        await using var db = await _factory.CreateDbContextAsync();
        if (await db.Products.AnyAsync(p => p.Name == name && p.Id != input.Id))
            throw new BusinessException($"Le produit « {name} » existe déjà.");

        Product entity;
        if (input.Id == 0)
        {
            entity = new Product { Kind = input.Kind };
            db.Products.Add(entity);
        }
        else
        {
            entity = await db.Products.FindAsync(input.Id) ?? throw new BusinessException("Produit introuvable.");
            if (entity.Kind != input.Kind && await db.StockMovements.AnyAsync(m => m.ProductId == entity.Id))
                throw new BusinessException("Le type d'un produit ayant des mouvements de stock ne peut plus être changé.");
            entity.Kind = input.Kind;
        }

        entity.Name = name;
        entity.OperatorId = input.Kind == ProductKind.VirtualCredit ? input.OperatorId : null;
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

    /// <summary>Correction d'inventaire : enregistre un mouvement d'ajustement pour atteindre le solde compté.</summary>
    public async Task AdjustStockAsync(int productId, decimal countedBalance, string? note)
    {
        if (countedBalance < 0) throw new BusinessException("Le solde compté ne peut pas être négatif.");

        await using var db = await _factory.CreateDbContextAsync();
        var product = await db.Products.FindAsync(productId) ?? throw new BusinessException("Produit introuvable.");
        var delta = countedBalance - product.StockBalance;
        if (delta == 0) return;

        db.StockMovements.Add(new StockMovement
        {
            Date = DateTime.Now,
            ProductId = productId,
            Quantity = delta,
            Kind = StockMovementKind.Adjustment,
            Note = string.IsNullOrWhiteSpace(note) ? "Correction d'inventaire" : note.Trim(),
        });
        product.StockBalance = countedBalance;
        await db.SaveChangesAsync();
    }

    public async Task<List<StockMovement>> GetMovementsAsync(int productId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var rows = await db.StockMovements.AsNoTracking().Where(m => m.ProductId == productId).ToListAsync();
        return rows.OrderByDescending(m => m.Date).ThenByDescending(m => m.Id).ToList();
    }
}
