using GestionStock.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace GestionStock.Tests;

public class DemoDataTests
{
    private static DemoDataService Create(TestDb db) => new(db, new ClientService(db), new SupplierService(db),
        new PurchaseService(db), new DeliveryService(db));

    [Fact]
    public async Task Demo_data_is_generated_consistently_and_only_once()
    {
        using var db = await TestDb.CreateAsync();
        var demo = Create(db);

        var result = await demo.GenerateAsync(clientCount: 60, days: 30);

        Assert.Equal(60, result.Clients);
        Assert.Equal(5, result.Suppliers);
        Assert.True(result.Purchases > 0);
        Assert.True(result.Deliveries > 0);

        await using var ctx = db.CreateDbContext();
        // Le stock de chaque produit reste égal à la somme de ses mouvements, et jamais négatif.
        foreach (var p in await ctx.Products.ToListAsync())
        {
            var sum = (await ctx.StockMovements.Where(m => m.ProductId == p.Id).Select(m => m.Quantity).ToListAsync()).Sum();
            Assert.Equal(sum, p.StockBalance);
            Assert.True(p.StockBalance >= 0);
        }
        // Aucune dette client négative.
        Assert.All((await new DeliveryService(db).GetDebtsAsync()).Values, d => Assert.True(d >= 0));

        await Assert.ThrowsAsync<BusinessException>(() => demo.GenerateAsync());
    }
}
