using GestionStock.Core.Domain;
using GestionStock.Core.Services;

namespace GestionStock.Tests;

public class ReportTests
{
    private sealed record Ctx(TestDb Db, Supplier Supplier, Client Client, Product Flexy, ReportService Reports, DeliveryService Deliveries);

    private static async Task<Ctx> SetupAsync()
    {
        var db = await TestDb.CreateAsync();
        var supplier = await new SupplierService(db).SaveAsync(new Supplier { Reference = "F1", CompanyName = "Grossiste" });
        var flexy = (await new ProductService(db).ListAsync()).Single(p => p.Name == "Flexy");
        await new PurchaseService(db).CreateAsync(new PurchaseInput(supplier.Id, DateTime.Today,
            [new PurchaseLineInput(flexy.Id, 1_000_000m, 0.97m)], 0m));
        var client = await new ClientService(db).SaveAsync(new Client { Name = "Boutique" });
        return new Ctx(db, supplier, client, flexy, new ReportService(db), new DeliveryService(db));
    }

    [Fact]
    public async Task A_purchase_produces_one_achat_row_per_line()
    {
        var c = await SetupAsync();
        using var _ = c.Db;

        var rows = await c.Reports.GetOperationsAsync();

        var achat = Assert.Single(rows, r => r.Type == "Achat");
        Assert.Equal("Grossiste", achat.Tiers);
        Assert.Equal("Flexy", achat.ProductName);
        Assert.Equal(970_000m, achat.Total);
    }

    [Fact]
    public async Task A_sale_paid_in_full_produces_a_vente_row_followed_by_an_encaissement_row()
    {
        var c = await SetupAsync();
        using var _ = c.Db;
        await c.Deliveries.CreateAsync(new DeliveryInput(c.Client.Id, DateTime.Today,
            [new DeliveryLineInput(c.Flexy.Id, 10_000m, 1m)], 10_000m));

        var rows = await c.Reports.GetOperationsAsync();

        var vente = Assert.Single(rows, r => r.Type == "Vente");
        Assert.Equal(10_000m, vente.Total);

        // Le montant encaissé dans le même bon apparaît comme une ligne séparée, avec le même numéro, juste après la vente.
        var enc = Assert.Single(rows, r => r.Type == "Encaissement");
        Assert.Equal(10_000m, enc.Total);
        Assert.Equal(vente.Number, enc.Number);
        Assert.Equal(rows.IndexOf(vente) + 1, rows.IndexOf(enc));
    }

    [Fact]
    public async Task A_sale_paid_partially_shows_only_the_amount_actually_paid()
    {
        var c = await SetupAsync();
        using var _ = c.Db;
        await c.Deliveries.CreateAsync(new DeliveryInput(c.Client.Id, DateTime.Today,
            [new DeliveryLineInput(c.Flexy.Id, 10_000m, 1m)], 4_000m));

        var rows = await c.Reports.GetOperationsAsync();

        Assert.Equal(10_000m, Assert.Single(rows, r => r.Type == "Vente").Total);
        Assert.Equal(4_000m, Assert.Single(rows, r => r.Type == "Encaissement").Total);
    }

    [Fact]
    public async Task An_unpaid_sale_creates_no_encaissement_row_at_all()
    {
        var c = await SetupAsync();
        using var _ = c.Db;
        await c.Deliveries.CreateAsync(new DeliveryInput(c.Client.Id, DateTime.Today,
            [new DeliveryLineInput(c.Flexy.Id, 10_000m, 1m)], 0m));

        var rows = await c.Reports.GetOperationsAsync();

        Assert.DoesNotContain(rows, r => r.Type == "Encaissement");
    }

    [Fact]
    public async Task Transactions_report_keeps_only_virtual_credit_purchases_and_sales()
    {
        var c = await SetupAsync();
        using var _ = c.Db;
        var cards = (await new ProductService(c.Db).ListAsync()).Single(p => p.Kind == ProductKind.Physical);
        await new PurchaseService(c.Db).CreateAsync(new PurchaseInput(c.Supplier.Id, DateTime.Today,
            [new PurchaseLineInput(cards.Id, 10m, 480m)], 0m));
        await c.Deliveries.CreateAsync(new DeliveryInput(c.Client.Id, DateTime.Today,
            [new DeliveryLineInput(c.Flexy.Id, 10_000m, 0.98m), new DeliveryLineInput(cards.Id, 2m, 500m)], 5_000m));

        var rows = await c.Reports.GetVirtualCreditTransactionsAsync();

        Assert.Equal(2, rows.Count); // l'achat et la vente de Flexy ; ni cartes, ni encaissement
        Assert.All(rows, r => Assert.Equal("Flexy", r.ProductName));
        Assert.Contains(rows, r => r.Type == "Achat");
        Assert.Contains(rows, r => r.Type == "Vente");
    }

    [Fact]
    public async Task A_payment_only_bon_produces_an_encaissement_row()
    {
        var c = await SetupAsync();
        using var _ = c.Db;
        await c.Deliveries.CreateAsync(new DeliveryInput(c.Client.Id, DateTime.Today, [new DeliveryLineInput(c.Flexy.Id, 10_000m, 1m)], 0m));
        await c.Deliveries.CreateAsync(new DeliveryInput(c.Client.Id, DateTime.Today, [], 3_000m));

        var rows = await c.Reports.GetOperationsAsync();

        var enc = Assert.Single(rows, r => r.Type == "Encaissement");
        Assert.Equal(3_000m, enc.Total);
    }
}
