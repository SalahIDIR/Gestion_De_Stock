using GestionStock.Core.Data;
using GestionStock.Core.Domain;
using GestionStock.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace GestionStock.Tests;

public class DeliveryTests
{
    private sealed record Ctx(
        TestDb Db, Client Client, Product Flexy, Product Cards, DeliveryService Deliveries, ProductService Products);

    /// <summary>Base avec 1 000 000 DA de Flexy et 100 cartes en stock, et un client sans plafond.</summary>
    private static async Task<Ctx> SetupAsync(decimal creditLimit = 0m)
    {
        var db = await TestDb.CreateAsync();
        var supplier = await new SupplierService(db).SaveAsync(new Supplier { Reference = "F1", CompanyName = "Grossiste" });
        var products = new ProductService(db);
        var all = await products.ListAsync();
        var flexy = all.Single(p => p.Name == "Flexy");
        var cards = all.Single(p => p.Kind == ProductKind.Physical);

        await new PurchaseService(db).CreateAsync(new PurchaseInput(supplier.Id, DateTime.Today,
            [new PurchaseLineInput(flexy.Id, 1_000_000m, 0.9725m), new PurchaseLineInput(cards.Id, 100m, 480m)], 0m));

        var client = await new ClientService(db).SaveAsync(new Client { Name = "Boutique", CreditLimit = creditLimit });
        return new Ctx(db, client, flexy, cards, new DeliveryService(db), products);
    }

    private static DeliveryInput Bon(Ctx c, decimal amount, decimal coef, decimal paid = 0m)
        => new(c.Client.Id, DateTime.Today, [new DeliveryLineInput(c.Flexy.Id, amount, coef)], paid);

    private static async Task<decimal> StockOf(Ctx c, Product p)
        => (await c.Products.ListAsync()).Single(x => x.Id == p.Id).StockBalance;

    [Fact]
    public async Task Sale_bills_amount_times_coefficient_and_removes_credit_from_stock()
    {
        var c = await SetupAsync();
        using var _ = c.Db;

        var note = await c.Deliveries.CreateAsync(Bon(c, 5_000m, 0.98m, paid: 1_000m));

        Assert.Equal("BL-000001", note.Number);
        Assert.Equal(4_900m, note.Total);
        Assert.Equal(3_900m, note.Remaining);
        Assert.Equal(995_000m, await StockOf(c, c.Flexy));
        Assert.Equal(3_900m, await c.Deliveries.GetClientDebtAsync(c.Client.Id));
    }

    [Fact]
    public async Task Physical_sale_uses_unit_price_and_needs_whole_quantities()
    {
        var c = await SetupAsync();
        using var _ = c.Db;

        var note = await c.Deliveries.CreateAsync(
            new DeliveryInput(c.Client.Id, DateTime.Today, [new DeliveryLineInput(c.Cards.Id, 10m, 500m)], 5_000m));
        Assert.Equal(5_000m, note.Total);
        Assert.Equal(90m, await StockOf(c, c.Cards));

        await Assert.ThrowsAsync<BusinessException>(() => c.Deliveries.CreateAsync(
            new DeliveryInput(c.Client.Id, DateTime.Today, [new DeliveryLineInput(c.Cards.Id, 2.5m, 500m)], 0m)));
    }

    [Fact]
    public async Task Selling_more_than_the_stock_is_refused_and_changes_nothing()
    {
        var c = await SetupAsync();
        using var _ = c.Db;

        await Assert.ThrowsAsync<BusinessException>(() => c.Deliveries.CreateAsync(Bon(c, 1_000_001m, 0.98m)));
        // Deux lignes du même produit sont cumulées pour le contrôle de stock.
        await Assert.ThrowsAsync<BusinessException>(() => c.Deliveries.CreateAsync(new DeliveryInput(
            c.Client.Id, DateTime.Today,
            [new DeliveryLineInput(c.Flexy.Id, 600_000m, 0.98m), new DeliveryLineInput(c.Flexy.Id, 600_000m, 0.98m)], 0m)));

        Assert.Equal(1_000_000m, await StockOf(c, c.Flexy));
        Assert.Empty(await c.Deliveries.ListAsync());
    }

    [Fact]
    public async Task Selling_exactly_the_whole_stock_is_allowed()
    {
        var c = await SetupAsync();
        using var _ = c.Db;
        await c.Deliveries.CreateAsync(Bon(c, 1_000_000m, 0.98m));
        Assert.Equal(0m, await StockOf(c, c.Flexy));
    }

    [Fact]
    public async Task Credit_limit_blocks_a_delivery_that_would_push_the_debt_over_it()
    {
        var c = await SetupAsync(creditLimit: 10_000m);
        using var _ = c.Db;

        await c.Deliveries.CreateAsync(Bon(c, 6_000m, 1m));                    // dette 6 000
        await Assert.ThrowsAsync<BusinessException>(() => c.Deliveries.CreateAsync(Bon(c, 5_000m, 1m))); // 11 000 > 10 000
        await c.Deliveries.CreateAsync(Bon(c, 5_000m, 1m, paid: 1_000m));      // dette 10 000 : exactement le plafond, accepté

        Assert.Equal(10_000m, await c.Deliveries.GetClientDebtAsync(c.Client.Id));
        Assert.Equal(989_000m, await StockOf(c, c.Flexy));                     // le bon refusé n'a rien retiré
        await Assert.ThrowsAsync<BusinessException>(() => c.Deliveries.CreateAsync(Bon(c, 100m, 1m)));
    }

    [Fact]
    public async Task Payment_reduces_the_debt_and_unblocks_the_client()
    {
        var c = await SetupAsync(creditLimit: 10_000m);
        using var _ = c.Db;
        await c.Deliveries.CreateAsync(Bon(c, 10_000m, 1m));
        await Assert.ThrowsAsync<BusinessException>(() => c.Deliveries.CreateAsync(Bon(c, 1_000m, 1m)));

        await c.Deliveries.AddPaymentAsync(c.Client.Id, DateTime.Today, 4_000m, " acompte ");

        Assert.Equal(6_000m, await c.Deliveries.GetClientDebtAsync(c.Client.Id));
        Assert.Equal(6_000m, (await c.Deliveries.GetDebtsAsync())[c.Client.Id]);
        Assert.Equal("acompte", Assert.Single(await c.Deliveries.ListPaymentsAsync(c.Client.Id)).Note);
        await c.Deliveries.CreateAsync(Bon(c, 4_000m, 1m));
    }

    [Fact]
    public async Task Payment_cannot_be_zero_negative_or_exceed_the_debt()
    {
        var c = await SetupAsync();
        using var _ = c.Db;
        await c.Deliveries.CreateAsync(Bon(c, 1_000m, 1m));

        await Assert.ThrowsAsync<BusinessException>(() => c.Deliveries.AddPaymentAsync(c.Client.Id, DateTime.Today, 0m, null));
        await Assert.ThrowsAsync<BusinessException>(() => c.Deliveries.AddPaymentAsync(c.Client.Id, DateTime.Today, -5m, null));
        await Assert.ThrowsAsync<BusinessException>(() => c.Deliveries.AddPaymentAsync(c.Client.Id, DateTime.Today, 1_000.01m, null));
        await c.Deliveries.AddPaymentAsync(c.Client.Id, DateTime.Today, 1_000m, null);
        Assert.Equal(0m, await c.Deliveries.GetClientDebtAsync(c.Client.Id));
    }

    [Fact]
    public async Task Last_price_of_each_product_is_remembered_per_client()
    {
        var c = await SetupAsync();
        using var _ = c.Db;
        var other = await new ClientService(c.Db).SaveAsync(new Client { Name = "Autre" });

        await c.Deliveries.CreateAsync(Bon(c, 1_000m, 0.98m));
        await c.Deliveries.CreateAsync(Bon(c, 1_000m, 0.985m));
        await c.Deliveries.CreateAsync(new DeliveryInput(other.Id, DateTime.Today, [new DeliveryLineInput(c.Flexy.Id, 1_000m, 0.97m)], 0m));

        Assert.Equal(0.985m, (await c.Deliveries.GetLastPricesAsync(c.Client.Id))[c.Flexy.Id]);
        Assert.Equal(0.97m, (await c.Deliveries.GetLastPricesAsync(other.Id))[c.Flexy.Id]);
        Assert.Empty(await c.Deliveries.GetLastPricesAsync(999));
    }

    [Fact]
    public async Task Invalid_deliveries_are_rejected()
    {
        var c = await SetupAsync();
        using var _ = c.Db;
        var d = c.Deliveries;
        var today = DateTime.Today;

        await Assert.ThrowsAsync<BusinessException>(() => d.CreateAsync(new DeliveryInput(c.Client.Id, today, [], 0m)));
        await Assert.ThrowsAsync<BusinessException>(() => d.CreateAsync(Bon(c, 1_000m, 9_800m)));       // coefficient aberrant
        await Assert.ThrowsAsync<BusinessException>(() => d.CreateAsync(Bon(c, 0m, 0.98m)));            // montant nul
        await Assert.ThrowsAsync<BusinessException>(() => d.CreateAsync(Bon(c, 1_000m, 0m)));           // coefficient nul
        await Assert.ThrowsAsync<BusinessException>(() => d.CreateAsync(Bon(c, 1_000m, 0.98m, -1m)));    // encaissé négatif
        await Assert.ThrowsAsync<BusinessException>(() => d.CreateAsync(
            new DeliveryInput(999, today, [new DeliveryLineInput(c.Flexy.Id, 1_000m, 0.98m)], 0m)));   // client inconnu

        Assert.Empty(await d.ListAsync());
        Assert.Equal(1_000_000m, await StockOf(c, c.Flexy));
    }

    [Fact]
    public async Task Stock_balance_still_equals_the_sum_of_movements_after_sales()
    {
        var c = await SetupAsync();
        using var _ = c.Db;
        await c.Deliveries.CreateAsync(Bon(c, 5_000m, 0.98m));
        await c.Deliveries.CreateAsync(Bon(c, 12_500m, 0.98m));
        await c.Products.AdjustStockAsync(c.Flexy.Id, 900_000m, null);

        await using var ctx = c.Db.CreateDbContext();
        var balance = (await ctx.Products.SingleAsync(p => p.Id == c.Flexy.Id)).StockBalance;
        var movements = (await ctx.StockMovements.Where(m => m.ProductId == c.Flexy.Id).ToListAsync()).Sum(m => m.Quantity);
        Assert.Equal(900_000m, balance);
        Assert.Equal(balance, movements);
        Assert.Equal(2, await ctx.StockMovements.CountAsync(m => m.Kind == StockMovementKind.Sale));
    }

    [Fact]
    public async Task A_client_with_deliveries_or_payments_cannot_be_deleted()
    {
        var c = await SetupAsync();
        using var _ = c.Db;
        var clients = new ClientService(c.Db);
        await c.Deliveries.CreateAsync(Bon(c, 1_000m, 1m));

        await Assert.ThrowsAsync<BusinessException>(() => clients.DeleteAsync(c.Client.Id));

        var unused = await clients.SaveAsync(new Client { Name = "Sans historique" });
        await clients.DeleteAsync(unused.Id);
        Assert.DoesNotContain(await clients.ListAsync(), x => x.Id == unused.Id);
    }

    [Fact]
    public async Task Payment_only_bon_reduces_debt_without_touching_stock_and_is_numbered_ENC()
    {
        var c = await SetupAsync();
        using var _ = c.Db;
        await c.Deliveries.CreateAsync(Bon(c, 10_000m, 1m)); // dette 10 000, BL-000001

        var receipt = await c.Deliveries.CreateAsync(new DeliveryInput(c.Client.Id, DateTime.Today, [], 4_000m));

        Assert.Equal("ENC-000002", receipt.Number);
        Assert.Equal(0m, receipt.Total);
        Assert.Equal(-4_000m, receipt.Remaining);
        Assert.Equal(6_000m, await c.Deliveries.GetClientDebtAsync(c.Client.Id));
        Assert.Equal(990_000m, await StockOf(c, c.Flexy)); // le stock n'a pas bougé
    }

    [Fact]
    public async Task Payment_only_bon_must_be_positive_but_may_exceed_the_debt()
    {
        var c = await SetupAsync();
        using var _ = c.Db;
        await c.Deliveries.CreateAsync(Bon(c, 1_000m, 1m)); // dette 1 000

        await Assert.ThrowsAsync<BusinessException>(() => c.Deliveries.CreateAsync(new DeliveryInput(c.Client.Id, DateTime.Today, [], 0m)));
        await c.Deliveries.CreateAsync(new DeliveryInput(c.Client.Id, DateTime.Today, [], 1_500m));
        Assert.Equal(-500m, await c.Deliveries.GetClientDebtAsync(c.Client.Id)); // avoir en faveur du client
    }

    [Fact]
    public async Task Encaissement_above_the_bon_total_is_accepted_and_puts_the_client_in_credit()
    {
        var c = await SetupAsync(creditLimit: 1_000m);
        using var _ = c.Db;

        var note = await c.Deliveries.CreateAsync(Bon(c, 5_000m, 1m, paid: 8_000m));

        Assert.Equal(-3_000m, note.Remaining);
        Assert.Equal(-3_000m, await c.Deliveries.GetClientDebtAsync(c.Client.Id));
    }

    [Fact]
    public async Task Recipient_phone_is_stored_and_returned_with_the_line()
    {
        var c = await SetupAsync();
        using var _ = c.Db;
        var note = await c.Deliveries.CreateAsync(new DeliveryInput(c.Client.Id, DateTime.Today,
            [new DeliveryLineInput(c.Flexy.Id, 1_000m, 1m, "0770123456")], 0m));

        Assert.Equal("0770123456", note.Lines.Single().RecipientPhone);
    }

    [Fact]
    public async Task Client_rate_is_fixed_on_first_bon_and_only_future_bons_use_the_updated_rate()
    {
        var c = await SetupAsync();
        using var _ = c.Db;

        var first = await c.Deliveries.CreateAsync(Bon(c, 1_000m, 0.98m));
        Assert.Equal(0.98m, (await c.Deliveries.GetLastPricesAsync(c.Client.Id))[c.Flexy.Id]);

        await c.Deliveries.CreateAsync(Bon(c, 1_000m, 0.985m));
        Assert.Equal(0.985m, (await c.Deliveries.GetLastPricesAsync(c.Client.Id))[c.Flexy.Id]);

        // Le bon déjà émis garde son propre tarif, même après que le tarif courant a changé.
        Assert.Equal(0.98m, first.Lines.Single().UnitPrice);
    }

    [Fact]
    public async Task Last_activity_date_is_the_last_payment_or_the_oldest_unpaid_bon()
    {
        var c = await SetupAsync();
        using var _ = c.Db;

        await c.Deliveries.CreateAsync(new DeliveryInput(c.Client.Id, new DateTime(2026, 1, 10), [new DeliveryLineInput(c.Flexy.Id, 1_000m, 1m)], 0m));
        var noPayment = (await c.Deliveries.GetLastActivityDatesAsync())[c.Client.Id];
        Assert.Equal(new DateTime(2026, 1, 10), noPayment);

        await c.Deliveries.CreateAsync(new DeliveryInput(c.Client.Id, new DateTime(2026, 1, 20), [], 500m));
        var afterPayment = (await c.Deliveries.GetLastActivityDatesAsync())[c.Client.Id];
        Assert.Equal(new DateTime(2026, 1, 20), afterPayment);
    }
}
