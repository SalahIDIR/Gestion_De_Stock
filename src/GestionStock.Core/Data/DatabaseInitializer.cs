using GestionStock.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace GestionStock.Core.Data;

public static class DatabaseInitializer
{
    /// <summary>Applique les migrations puis insère les données de départ si la base est vide.</summary>
    public static async Task InitializeAsync(IDbContextFactory<AppDbContext> factory)
    {
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.MigrateAsync();

        if (!await db.Settings.AnyAsync())
            db.Settings.Add(new AppSettings());

        if (!await db.Operators.AnyAsync())
        {
            db.Operators.AddRange(
                new Operator { Name = "Djezzy", ColorHex = "#F59E0B" },
                new Operator { Name = "Ooredoo", ColorHex = "#DC2626" },
                new Operator { Name = "Mobilis", ColorHex = "#16A34A" });
            await db.SaveChangesAsync();
        }

        if (!await db.Products.AnyAsync())
        {
            var ops = await db.Operators.ToDictionaryAsync(o => o.Name, o => o.Id);
            db.Products.AddRange(
                new Product { Name = "Flexy", Kind = ProductKind.VirtualCredit, OperatorId = ops["Djezzy"] },
                new Product { Name = "Storm", Kind = ProductKind.VirtualCredit, OperatorId = ops["Ooredoo"] },
                new Product { Name = "Erselli", Kind = ProductKind.VirtualCredit, OperatorId = ops["Mobilis"] },
                new Product { Name = "Cartes Idoom", Kind = ProductKind.Physical });
        }

        await db.SaveChangesAsync();
    }
}
