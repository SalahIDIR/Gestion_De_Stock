using GestionStock.Core.Domain;
using GestionStock.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace GestionStock.Tests;

public class EditDeleteTests
{
    private sealed record Ctx(TestDb Db, Supplier Supplier, Client Client, Product Flexy, Product Cards,
        PurchaseService Purchases, DeliveryService Deliveries, ReportService Reports);

    /// <summary>Base avec un bon d'achat de 1 000 000 DA de Flexy et 100 cartes, et un client sans plafond.</summary>
    private static async Task<(Ctx Ctx, PurchaseOrder Order)> SetupAsync()
    {
        var db = await TestDb.CreateAsync();
        var supplier = await new SupplierService(db).SaveAsync(new Supplier { Reference = "F1", CompanyName = "Grossiste" });
        var all = await new ProductService(db).ListAsync();
        var flexy = all.Single(p => p.Name == "Flexy");
        var cards = all.Single(p => p.Kind == ProductKind.Physical);
        var purchases = new PurchaseService(db);
        var order = await purchases.CreateAsync(new PurchaseInput(supplier.Id, DateTime.Today,
            [new PurchaseLineInput(flexy.Id, 1_000_000m, 0.97m), new PurchaseLineInput(cards.Id, 100m, 480m)], 0m));
        var client = await new ClientService(db).SaveAsync(new Client { Name = "Boutique" });
        return (new Ctx(db, supplier, client, flexy, cards, purchases, new DeliveryService(db), new ReportService(db)), order);
    }

    private static async Task<decimal> StockOf(Ctx c, Product p)
    {
        await using var db = c.Db.CreateDbContext();
        var product = await db.Products.SingleAsync(x => x.Id == p.Id);
        var journal = (await db.StockMovements.Where(m => m.ProductId == p.Id).Select(m => m.Quantity).ToListAsync()).Sum();
        Assert.Equal(journal, product.StockBalance); // le journal reste toujours cohérent avec le solde
        return product.StockBalance;
    }

    private static DeliveryInput Sale(Ctx c, decimal amount, decimal paid)
        => new(c.Client.Id, DateTime.Today, [new DeliveryLineInput(c.Flexy.Id, amount, 1m)], paid);

    [Fact]
    public async Task Editing_a_delivery_moves_stock_debt_and_report_but_keeps_number_and_date()
    {
        var (c, _) = await SetupAsync();
        using var _ = c.Db;
        var note = await c.Deliveries.CreateAsync(Sale(c, 10_000m, 0m));

        var edited = await c.Deliveries.UpdateAsync(note.Id, Sale(c, 4_000m, 1_000m));

        Assert.Equal(note.Number, edited.Number);
        Assert.Equal(note.Date, edited.Date);
        Assert.Equal(996_000m, await StockOf(c, c.Flexy));
        Assert.Equal(3_000m, await c.Deliveries.GetClientDebtAsync(c.Client.Id));

        var rows = await c.Reports.GetOperationsAsync();
        Assert.Equal(4_000m, Assert.Single(rows, r => r.Type == "Vente").Total);
        Assert.Equal(1_000m, Assert.Single(rows, r => r.Type == "Encaissement").Total);
    }

    [Fact]
    public async Task Editing_a_delivery_can_use_the_stock_it_already_held()
    {
        var (c, _) = await SetupAsync();
        using var _ = c.Db;
        var note = await c.Deliveries.CreateAsync(Sale(c, 1_000_000m, 0m)); // tout le stock

        await c.Deliveries.UpdateAsync(note.Id, Sale(c, 900_000m, 0m));

        Assert.Equal(100_000m, await StockOf(c, c.Flexy));
    }

    [Fact]
    public async Task Deleting_a_delivery_returns_stock_and_clears_debt_and_report()
    {
        var (c, _) = await SetupAsync();
        using var _ = c.Db;
        var note = await c.Deliveries.CreateAsync(Sale(c, 10_000m, 2_000m));

        await c.Deliveries.DeleteAsync(note.Id);

        Assert.Equal(1_000_000m, await StockOf(c, c.Flexy));
        Assert.Equal(0m, await c.Deliveries.GetClientDebtAsync(c.Client.Id));
        Assert.DoesNotContain(await c.Reports.GetOperationsAsync(), r => r.Type != "Achat");
    }

    [Fact]
    public async Task Reducing_or_deleting_a_delivery_already_settled_leaves_the_client_in_credit()
    {
        var (c, _) = await SetupAsync();
        using var _ = c.Db;
        var note = await c.Deliveries.CreateAsync(Sale(c, 10_000m, 0m));
        await c.Deliveries.CreateAsync(new DeliveryInput(c.Client.Id, DateTime.Today, [], 10_000m));

        await c.Deliveries.UpdateAsync(note.Id, Sale(c, 5_000m, 0m));
        Assert.Equal(-5_000m, await c.Deliveries.GetClientDebtAsync(c.Client.Id));

        await c.Deliveries.DeleteAsync(note.Id);
        Assert.Equal(-10_000m, await c.Deliveries.GetClientDebtAsync(c.Client.Id));
    }

    [Fact]
    public async Task An_encaissement_can_be_edited_and_deleted()
    {
        var (c, _) = await SetupAsync();
        using var _ = c.Db;
        await c.Deliveries.CreateAsync(Sale(c, 10_000m, 0m));
        var enc = await c.Deliveries.CreateAsync(new DeliveryInput(c.Client.Id, DateTime.Today, [], 3_000m));

        await c.Deliveries.UpdateAsync(enc.Id, new DeliveryInput(c.Client.Id, DateTime.Today, [], 10_000m));
        Assert.Equal(0m, await c.Deliveries.GetClientDebtAsync(c.Client.Id));
        await c.Deliveries.UpdateAsync(enc.Id, new DeliveryInput(c.Client.Id, DateTime.Today, [], 12_000m));
        Assert.Equal(-2_000m, await c.Deliveries.GetClientDebtAsync(c.Client.Id));

        await c.Deliveries.DeleteAsync(enc.Id);
        Assert.Equal(10_000m, await c.Deliveries.GetClientDebtAsync(c.Client.Id));
    }

    [Fact]
    public async Task Editing_a_purchase_updates_stock_total_and_supplier_debt()
    {
        var (c, order) = await SetupAsync();
        using var _ = c.Db;

        var edited = await c.Purchases.UpdateAsync(order.Id, new PurchaseInput(c.Supplier.Id, DateTime.Today,
            [new PurchaseLineInput(c.Flexy.Id, 500_000m, 0.97m)], 100_000m));

        Assert.Equal(order.Number, edited.Number);
        Assert.Equal(485_000m, edited.Total);
        Assert.Equal(500_000m, await StockOf(c, c.Flexy));
        Assert.Equal(0m, await StockOf(c, c.Cards));
        Assert.Equal(385_000m, (await new SupplierService(c.Db).GetDebtsAsync())[c.Supplier.Id]);
    }

    [Fact]
    public async Task A_purchase_whose_goods_were_already_sold_cannot_be_deleted_or_reduced_below_sales()
    {
        var (c, order) = await SetupAsync();
        using var _ = c.Db;
        await c.Deliveries.CreateAsync(Sale(c, 600_000m, 0m));

        await Assert.ThrowsAsync<BusinessException>(() => c.Purchases.DeleteAsync(order.Id));
        await Assert.ThrowsAsync<BusinessException>(() => c.Purchases.UpdateAsync(order.Id, new PurchaseInput(
            c.Supplier.Id, DateTime.Today, [new PurchaseLineInput(c.Flexy.Id, 500_000m, 0.97m)], 0m)));
        Assert.Equal(400_000m, await StockOf(c, c.Flexy));
    }

    [Fact]
    public async Task Editing_the_latest_delivery_updates_the_rate_proposed_for_the_next_one()
    {
        var (c, _) = await SetupAsync();
        using var _ = c.Db;
        var older = await c.Deliveries.CreateAsync(new DeliveryInput(c.Client.Id, DateTime.Today.AddDays(-1),
            [new DeliveryLineInput(c.Flexy.Id, 1_000m, 0.97m)], 0m));
        var latest = await c.Deliveries.CreateAsync(new DeliveryInput(c.Client.Id, DateTime.Today,
            [new DeliveryLineInput(c.Flexy.Id, 1_000m, 0.98m)], 0m));

        // Modifier un bon plus ancien ne change pas le tarif courant…
        await c.Deliveries.UpdateAsync(older.Id, new DeliveryInput(c.Client.Id, older.Date,
            [new DeliveryLineInput(c.Flexy.Id, 1_000m, 0.95m)], 0m));
        Assert.Equal(0.98m, (await c.Deliveries.GetLastPricesAsync(c.Client.Id))[c.Flexy.Id]);

        // … mais modifier le plus récent, oui.
        await c.Deliveries.UpdateAsync(latest.Id, new DeliveryInput(c.Client.Id, latest.Date,
            [new DeliveryLineInput(c.Flexy.Id, 1_000m, 0.985m)], 0m));
        Assert.Equal(0.985m, (await c.Deliveries.GetLastPricesAsync(c.Client.Id))[c.Flexy.Id]);
    }

    [Fact]
    public async Task Each_client_keeps_its_own_rate_and_last_chip_per_product()
    {
        var (c, _) = await SetupAsync();
        using var _ = c.Db;
        var other = await new ClientService(c.Db).SaveAsync(new Client { Name = "Autre" });
        await c.Deliveries.CreateAsync(new DeliveryInput(c.Client.Id, DateTime.Today,
            [new DeliveryLineInput(c.Flexy.Id, 1_000m, 0.98m, "0770000001")], 0m));
        await c.Deliveries.CreateAsync(new DeliveryInput(other.Id, DateTime.Today,
            [new DeliveryLineInput(c.Flexy.Id, 1_000m, 0.99m, "0770000002")], 0m));

        Assert.Equal(0.98m, (await c.Deliveries.GetLastPricesAsync(c.Client.Id))[c.Flexy.Id]);
        Assert.Equal(0.99m, (await c.Deliveries.GetLastPricesAsync(other.Id))[c.Flexy.Id]);
        Assert.Equal("0770000001", (await c.Deliveries.GetLastRecipientsAsync(c.Client.Id))[c.Flexy.Id]);
    }

    [Fact]
    public async Task Last_purchase_cost_comes_from_the_most_recent_purchase_whatever_the_supplier()
    {
        var (c, _) = await SetupAsync(); // Flexy acheté à 0.97 aujourd'hui
        using var _ = c.Db;
        var other = await new SupplierService(c.Db).SaveAsync(new Supplier { Reference = "F2", CompanyName = "Autre grossiste" });
        await c.Purchases.CreateAsync(new PurchaseInput(other.Id, DateTime.Now.AddMinutes(1),
            [new PurchaseLineInput(c.Flexy.Id, 10_000m, 0.9725m)], 0m));
        await c.Purchases.CreateAsync(new PurchaseInput(c.Supplier.Id, DateTime.Today.AddDays(-3),
            [new PurchaseLineInput(c.Flexy.Id, 10_000m, 0.96m)], 0m));

        var costs = await new ProductService(c.Db).GetLastPurchaseCostsAsync();

        Assert.Equal(0.9725m, costs[c.Flexy.Id]);
        Assert.Equal(480m, costs[c.Cards.Id]);
    }

    [Fact]
    public async Task Deleting_an_unsold_purchase_removes_its_stock()
    {
        var (c, order) = await SetupAsync();
        using var _ = c.Db;

        await c.Purchases.DeleteAsync(order.Id);

        Assert.Equal(0m, await StockOf(c, c.Flexy));
        Assert.Equal(0m, await StockOf(c, c.Cards));
        Assert.Empty(await c.Purchases.ListAsync());
    }
}
