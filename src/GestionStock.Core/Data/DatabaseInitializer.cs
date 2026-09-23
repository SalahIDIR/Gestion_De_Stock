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
            db.Products.AddRange(
                new Product { Name = "Flexy", Kind = ProductKind.VirtualCredit, ColorHex = "#F59E0B" },
                new Product { Name = "Storm", Kind = ProductKind.VirtualCredit, ColorHex = "#DC2626" },
                new Product { Name = "Erselli", Kind = ProductKind.VirtualCredit, ColorHex = "#16A34A" },
                new Product { Name = "Cartes Idoom", Kind = ProductKind.Physical, ColorHex = "#2563EB" });
        }
        else
        {
            // Bases créées avant l'ajout des couleurs : on assigne une couleur reconnaissable aux produits de départ
            // qui ont encore la couleur neutre par défaut, sans toucher aux couleurs déjà choisies par l'utilisateur.
            var knownColors = new Dictionary<string, string>
            {
                ["Flexy"] = "#F59E0B", ["Storm"] = "#DC2626", ["Erselli"] = "#16A34A", ["Cartes Idoom"] = "#2563EB",
            };
            var toFix = await db.Products.Where(p => p.ColorHex == "#6B7280").ToListAsync();
            foreach (var p in toFix)
                if (knownColors.TryGetValue(p.Name, out var hex)) p.ColorHex = hex;
        }

        await db.SaveChangesAsync();
    }
}
