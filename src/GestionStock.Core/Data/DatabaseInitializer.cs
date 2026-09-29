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

        // Gabarits USSD connus par défaut : ne complète que ce qui n'a jamais été configuré, sans écraser une
        // personnalisation déjà saisie dans Paramètres. Ces « codes » peuvent changer et restent modifiables là-bas.
        var defaultRouting = new Dictionary<string, (string Ussd, string Confirm, string Keyword, bool ViaSms)>
        {
            ["Djezzy"] = ("*760*{numero}*{montant}*2008#", "1", "TRANSFERE", false),
            ["Ooredoo"] = ("*599*{numero}*{montant}*2008#", "1", "STORMCREDIT", false),
            ["Mobilis"] = ("*631*{numero}*04*{montant}*00000#", "1", "transaction", true),
        };
        foreach (var op in await db.Operators.Where(o => o.UssdTemplate == null).ToListAsync())
            if (defaultRouting.TryGetValue(op.Name, out var d))
            {
                op.UssdTemplate = d.Ussd;
                op.ConfirmKeystroke = d.Confirm;
                op.SuccessKeyword = d.Keyword;
                op.ConfirmationViaSms = d.ViaSms;
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
