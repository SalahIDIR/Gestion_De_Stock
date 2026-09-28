using GestionStock.Core.Data;
using GestionStock.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace GestionStock.Core.Services;

public class SupplierService
{
    private readonly IDbContextFactory<AppDbContext> _factory;

    public SupplierService(IDbContextFactory<AppDbContext> factory) => _factory = factory;

    public async Task<List<Supplier>> ListAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.Suppliers.AsNoTracking().OrderBy(s => s.CompanyName).ToListAsync();
    }

    /// <summary>Dette envers chaque fournisseur (solde initial + achats non payés), clé = SupplierId.</summary>
    public async Task<Dictionary<int, decimal>> GetDebtsAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        var openingBalances = await db.Suppliers.AsNoTracking().Select(s => new { s.Id, s.OpeningBalance }).ToListAsync();
        var rows = await db.PurchaseOrders.AsNoTracking()
            .Select(p => new { p.SupplierId, p.Total, p.AmountPaid }).ToListAsync();

        var debts = openingBalances.ToDictionary(s => s.Id, s => s.OpeningBalance);
        foreach (var group in rows.GroupBy(r => r.SupplierId))
            debts[group.Key] = debts.GetValueOrDefault(group.Key) + group.Sum(r => r.Total - r.AmountPaid);
        return debts;
    }

    public async Task<Supplier> SaveAsync(Supplier input)
    {
        var reference = input.Reference.Trim();
        var name = input.CompanyName.Trim();
        if (reference.Length == 0) throw new BusinessException("La référence est obligatoire.");
        if (name.Length == 0) throw new BusinessException("La raison sociale est obligatoire.");

        await using var db = await _factory.CreateDbContextAsync();
        if (await db.Suppliers.AnyAsync(s => s.Reference == reference && s.Id != input.Id))
            throw new BusinessException($"La référence « {reference} » existe déjà.");

        Supplier entity;
        if (input.Id == 0)
        {
            entity = new Supplier();
            db.Suppliers.Add(entity);
        }
        else
        {
            entity = await db.Suppliers.FindAsync(input.Id) ?? throw new BusinessException("Fournisseur introuvable.");
        }

        entity.Reference = reference;
        entity.CompanyName = name;
        entity.Address = Clean(input.Address);
        entity.Phone1 = Clean(input.Phone1);
        entity.Phone2 = Clean(input.Phone2);
        entity.OpeningBalance = input.OpeningBalance;
        await db.SaveChangesAsync();
        return entity;
    }

    public async Task DeleteAsync(int id)
    {
        await using var db = await _factory.CreateDbContextAsync();
        if (await db.PurchaseOrders.AnyAsync(p => p.SupplierId == id))
            throw new BusinessException("Ce fournisseur a des bons d'achat : il ne peut pas être supprimé.");
        var entity = await db.Suppliers.FindAsync(id);
        if (entity == null) return;
        db.Suppliers.Remove(entity);
        await db.SaveChangesAsync();
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
