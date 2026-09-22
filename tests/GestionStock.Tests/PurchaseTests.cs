using GestionStock.Core.Data;
using GestionStock.Core.Domain;
using GestionStock.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace GestionStock.Tests;

public class PurchaseTests
{
    private static async Task<(Supplier Supplier, Product Flexy, Product Cards)> SetupAsync(TestDb db)
    {
        var supplier = await new SupplierService(db).SaveAsync(new Supplier { Reference = "F1", CompanyName = "Grossiste Alger" });
        var products = await new ProductService(db).ListAsync();
        return (supplier, products.Single(p => p.Name == "Flexy"), products.Single(p => p.Kind == ProductKind.Physical));
    }

    [Fact]
    public async Task Virtual_credit_purchase_applies_the_coefficient_and_adds_face_value_to_stock()
    {
        using var db = await TestDb.CreateAsync();
        var (supplier, flexy, _) = await SetupAsync(db);

        var order = await new PurchaseService(db).CreateAsync(new PurchaseInput(
            supplier.Id, new DateTime(2026, 9, 21),
            [new PurchaseLineInput(flexy.Id, 1_000_000m, 0.9725m)], AmountPaid: 500_000m));

        Assert.Equal("BA-000001", order.Number);
        Assert.Equal(972_500m, order.Total);
        Assert.Equal(472_500m, order.Remaining);

        var stored = (await new ProductService(db).ListAsync()).Single(p => p.Id == flexy.Id);
        Assert.Equal(1_000_000m, stored.StockBalance);
    }

    [Fact]
    public async Task Physical_purchase_multiplies_quantity_by_unit_price()
    {
        using var db = await TestDb.CreateAsync();
        var (supplier, _, cards) = await SetupAsync(db);

        var order = await new PurchaseService(db).CreateAsync(new PurchaseInput(
            supplier.Id, DateTime.Today, [new PurchaseLineInput(cards.Id, 50m, 480m)], 0m));

        Assert.Equal(24_000m, order.Total);
        Assert.Equal(50m, (await new ProductService(db).ListAsync()).Single(p => p.Id == cards.Id).StockBalance);
    }

    [Fact]
    public void Line_total_is_rounded_to_two_decimals()
        => Assert.Equal(4862.5m, PurchaseService.ComputeLineTotal(5000m, 0.9725m));

    [Fact]
    public async Task Stock_balance_always_equals_the_sum_of_movements()
    {
        using var db = await TestDb.CreateAsync();
        var (supplier, flexy, _) = await SetupAsync(db);
        var purchases = new PurchaseService(db);
        var date = DateTime.Today;

        await purchases.CreateAsync(new PurchaseInput(supplier.Id, date, [new PurchaseLineInput(flexy.Id, 100_000m, 0.97m)], 0m));
        await purchases.CreateAsync(new PurchaseInput(supplier.Id, date, [new PurchaseLineInput(flexy.Id, 50_000m, 0.98m)], 0m));
        await new ProductService(db).AdjustStockAsync(flexy.Id, 148_000m, null);

        await using var ctx = db.CreateDbContext();
        var balance = (await ctx.Products.SingleAsync(p => p.Id == flexy.Id)).StockBalance;
        var movements = (await ctx.StockMovements.Where(m => m.ProductId == flexy.Id).ToListAsync()).Sum(m => m.Quantity);
        Assert.Equal(148_000m, balance);
        Assert.Equal(balance, movements);
    }

    [Fact]
    public async Task Invalid_purchases_are_rejected_and_leave_stock_untouched()
    {
        using var db = await TestDb.CreateAsync();
        var (supplier, flexy, cards) = await SetupAsync(db);
        var purchases = new PurchaseService(db);
        var date = DateTime.Today;

        // Aucune ligne
        await Assert.ThrowsAsync<BusinessException>(() => purchases.CreateAsync(new PurchaseInput(supplier.Id, date, [], 0m)));
        // Coefficient saisi comme un pourcentage
        await Assert.ThrowsAsync<BusinessException>(() => purchases.CreateAsync(
            new PurchaseInput(supplier.Id, date, [new PurchaseLineInput(flexy.Id, 1000m, 9725m)], 0m)));
        // Quantité physique non entière
        await Assert.ThrowsAsync<BusinessException>(() => purchases.CreateAsync(
            new PurchaseInput(supplier.Id, date, [new PurchaseLineInput(cards.Id, 2.5m, 100m)], 0m)));
        // Quantité nulle
        await Assert.ThrowsAsync<BusinessException>(() => purchases.CreateAsync(
            new PurchaseInput(supplier.Id, date, [new PurchaseLineInput(flexy.Id, 0m, 0.97m)], 0m)));
        // Payé > total
        await Assert.ThrowsAsync<BusinessException>(() => purchases.CreateAsync(
            new PurchaseInput(supplier.Id, date, [new PurchaseLineInput(flexy.Id, 1000m, 0.97m)], 5000m)));
        // Fournisseur inconnu
        await Assert.ThrowsAsync<BusinessException>(() => purchases.CreateAsync(
            new PurchaseInput(999, date, [new PurchaseLineInput(flexy.Id, 1000m, 0.97m)], 0m)));

        Assert.All(await new ProductService(db).ListAsync(), p => Assert.Equal(0m, p.StockBalance));
        Assert.Empty(await purchases.ListAsync());
    }

    [Fact]
    public async Task Supplier_debt_is_the_sum_of_unpaid_amounts_and_blocks_deletion()
    {
        using var db = await TestDb.CreateAsync();
        var (supplier, flexy, _) = await SetupAsync(db);
        var purchases = new PurchaseService(db);
        var suppliers = new SupplierService(db);

        await purchases.CreateAsync(new PurchaseInput(supplier.Id, DateTime.Today, [new PurchaseLineInput(flexy.Id, 100_000m, 0.97m)], 40_000m));
        await purchases.CreateAsync(new PurchaseInput(supplier.Id, DateTime.Today, [new PurchaseLineInput(flexy.Id, 100_000m, 0.97m)], 97_000m));

        var debts = await suppliers.GetDebtsAsync();
        Assert.Equal(57_000m, debts[supplier.Id]);
        await Assert.ThrowsAsync<BusinessException>(() => suppliers.DeleteAsync(supplier.Id));
    }
}
