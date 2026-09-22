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
        Assert.True(achat.IncludeInList);
    }

    [Fact]
    public async Task A_sale_paid_in_full_produces_a_single_vente_row_plus_a_hidden_encaissement_amount()
    {
        var c = await SetupAsync();
        using var _ = c.Db;
        await c.Deliveries.CreateAsync(new DeliveryInput(c.Client.Id, DateTime.Today,
            [new DeliveryLineInput(c.Flexy.Id, 10_000m, 1m)], 10_000m));

        var rows = await c.Reports.GetOperationsAsync();

        // Une seule ligne "Vente" visible : l'encaissement fait à la vente n'est pas dupliqué dans le tableau.
        var ventes = rows.Where(r => r.Type == "Vente").ToList();
        var vente = Assert.Single(ventes);
        Assert.Equal(10_000m, vente.Total);
        Assert.True(vente.IncludeInList);

        // Mais son montant encaissé existe bien comme ligne cachée, pour alimenter le total des encaissements.
        var hidden = Assert.Single(rows, r => r.Type == "Encaissement");
        Assert.Equal(10_000m, hidden.Total);
        Assert.False(hidden.IncludeInList);
        Assert.Equal(vente.Number, hidden.Number);
    }

    [Fact]
    public async Task A_sale_paid_partially_hides_only_the_amount_actually_paid()
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
    public async Task A_payment_only_bon_still_produces_a_visible_encaissement_row()
    {
        var c = await SetupAsync();
        using var _ = c.Db;
        await c.Deliveries.CreateAsync(new DeliveryInput(c.Client.Id, DateTime.Today, [new DeliveryLineInput(c.Flexy.Id, 10_000m, 1m)], 0m));
        await c.Deliveries.CreateAsync(new DeliveryInput(c.Client.Id, DateTime.Today, [], 3_000m));

        var rows = await c.Reports.GetOperationsAsync();

        var enc = Assert.Single(rows, r => r.Type == "Encaissement");
        Assert.Equal(3_000m, enc.Total);
        Assert.True(enc.IncludeInList); // celui-ci doit apparaître dans le tableau, contrairement à l'encaissement caché.
    }
}
