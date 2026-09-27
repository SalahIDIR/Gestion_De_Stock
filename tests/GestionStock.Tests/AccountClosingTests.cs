using GestionStock.Core.Domain;
using GestionStock.Core.Services;

namespace GestionStock.Tests;

public class AccountClosingTests
{
    private sealed record Ctx(TestDb Db, Supplier Supplier, Client Client, Product Flexy, Product Cards,
        PurchaseService Purchases, DeliveryService Deliveries, AccountClosingService Closings);

    private static async Task<Ctx> SetupAsync()
    {
        var db = await TestDb.CreateAsync();
        var supplier = await new SupplierService(db).SaveAsync(new Supplier { Reference = "F1", CompanyName = "Grossiste" });
        var all = await new ProductService(db).ListAsync();
        var flexy = all.Single(p => p.Name == "Flexy");
        var cards = all.Single(p => p.Kind == ProductKind.Physical);
        var client = await new ClientService(db).SaveAsync(new Client { Name = "Boutique" });

        var reports = new ReportService(db, new ProductService(db));
        var closings = new AccountClosingService(db, reports, new ProductService(db), new DeliveryService(db), new SupplierService(db));
        return new Ctx(db, supplier, client, flexy, cards, new PurchaseService(db), new DeliveryService(db), closings);
    }

    [Fact]
    public async Task First_closing_has_no_previous_and_benefice_equals_total()
    {
        var c = await SetupAsync();
        using var _ = c.Db;
        await c.Purchases.CreateAsync(new PurchaseInput(c.Supplier.Id, DateTime.Today, [new PurchaseLineInput(c.Flexy.Id, 1_000_000m, 0.97m)], 970_000m));

        var preview = await c.Closings.PreviewAsync(DateTime.Now);

        Assert.Null(preview.PreviousDate);
        Assert.Equal(0m, preview.PreviousCash);
        Assert.Equal(0m, preview.PreviousTotal);
        Assert.Equal(970_000m, preview.StockValue);  // 1 000 000 × 0,97
        Assert.Equal(0m, preview.TotalRecettes);
        Assert.Equal(970_000m, preview.TotalDepenses); // payé au fournisseur
        Assert.Equal(-970_000m, preview.Cash);          // rien encaissé, tout payé cash
    }

    [Fact]
    public async Task Validating_persists_the_closing_and_computes_benefice_and_moyenne()
    {
        var c = await SetupAsync();
        using var _ = c.Db;
        await c.Purchases.CreateAsync(new PurchaseInput(c.Supplier.Id, DateTime.Today, [new PurchaseLineInput(c.Flexy.Id, 1_000_000m, 0.97m)], 0m));
        await c.Deliveries.CreateAsync(new DeliveryInput(c.Client.Id, DateTime.Today, [new DeliveryLineInput(c.Flexy.Id, 100_000m, 0.98m)], 98_000m));

        var now = DateTime.Now;
        var closing = await c.Closings.ValidateAsync(now, prelevements: 1_000m, comments: " RAS ");

        Assert.Equal(98_000m, closing.TotalRecettes);
        Assert.Equal(0m, closing.TotalDepenses);
        Assert.Equal(98_000m, closing.Cash);
        Assert.Equal(873_000m, closing.StockValue); // stock restant 900 000 (1 000 000 acheté − 100 000 vendu) × 0,97
        Assert.Equal(1_000m, closing.Prelevements);
        Assert.Equal("RAS", closing.Comments);
        // Premier solde : bénéfice = total (rien avant), moyenne = bénéfice (1 jour par défaut).
        Assert.Equal(closing.Total, closing.Benefice);
        Assert.Equal(closing.Benefice, closing.Moyenne);

        var last = await c.Closings.GetLastAsync();
        Assert.Equal(closing.Id, last!.Id);
    }

    [Fact]
    public async Task A_second_closing_only_counts_money_since_the_first_one()
    {
        var c = await SetupAsync();
        using var _ = c.Db;
        await c.Purchases.CreateAsync(new PurchaseInput(c.Supplier.Id, DateTime.Today, [new PurchaseLineInput(c.Flexy.Id, 1_000_000m, 0.97m)], 500_000m));
        var t0 = DateTime.Now;
        var first = await c.Closings.ValidateAsync(t0, 0m, null);

        // Après la première clôture (une seconde plus tard) : un paiement fournisseur et un encaissement.
        var t1 = t0.AddSeconds(1);
        await c.Purchases.CreateAsync(new PurchaseInput(c.Supplier.Id, t1, [new PurchaseLineInput(c.Flexy.Id, 200_000m, 0.97m)], 194_000m));
        await c.Deliveries.CreateAsync(new DeliveryInput(c.Client.Id, t1, [new DeliveryLineInput(c.Flexy.Id, 50_000m, 0.98m)], 49_000m));

        var preview = await c.Closings.PreviewAsync(t1.AddSeconds(1));

        Assert.Equal(first.Date, preview.PreviousDate);
        Assert.Equal(first.Cash, preview.PreviousCash);
        Assert.Equal(49_000m, preview.TotalRecettes);   // le paiement compté dans la 1ère clôture n'est pas repris
        Assert.Equal(194_000m, preview.TotalDepenses);
        Assert.Equal(first.Cash + 49_000m - 194_000m, preview.Cash);
    }

    [Fact]
    public async Task Prelevements_reduce_the_total_and_therefore_the_benefice()
    {
        var c = await SetupAsync();
        using var _ = c.Db;
        await c.Deliveries.CreateAsync(new DeliveryInput(c.Client.Id, DateTime.Today, [], 10_000m)); // encaissement pur (dette négative permise)

        var previewNoPrelevement = await c.Closings.PreviewAsync(DateTime.Now);
        var totalSansPrelevement = previewNoPrelevement.StockValue + previewNoPrelevement.ClientCredit
                                    + previewNoPrelevement.Cash - previewNoPrelevement.SupplierCredit;

        var closing = await c.Closings.ValidateAsync(DateTime.Now, prelevements: 3_000m, comments: null);

        Assert.Equal(totalSansPrelevement - 3_000m, closing.Total);
    }

    [Fact]
    public async Task Negative_prelevement_is_rejected()
    {
        var c = await SetupAsync();
        using var _ = c.Db;
        await Assert.ThrowsAsync<BusinessException>(() => c.Closings.ValidateAsync(DateTime.Now, -1m, null));
    }

    [Fact]
    public async Task Moyenne_divides_the_benefice_by_days_since_the_previous_closing()
    {
        var c = await SetupAsync();
        using var _ = c.Db;

        // Simulation à deux instants espacés de 4 jours pile, sans retoucher aucune date après coup (ce qui
        // recompterait les mêmes opérations d'une clôture sur l'autre).
        var now1 = DateTime.Now.AddDays(-4);
        await c.Purchases.CreateAsync(new PurchaseInput(c.Supplier.Id, now1, [new PurchaseLineInput(c.Flexy.Id, 1_000_000m, 0.97m)], 970_000m));
        var first = await c.Closings.ValidateAsync(now1, 0m, null);

        // Vente avec marge, payée intégralement : 10 000 × 1,05 facturés (10 500), coût 10 000 × 0,97 (9 700) => marge 800.
        var now2 = now1.AddDays(4);
        await c.Deliveries.CreateAsync(new DeliveryInput(c.Client.Id, now2, [new DeliveryLineInput(c.Flexy.Id, 10_000m, 1.05m)], 10_500m));
        var second = await c.Closings.ValidateAsync(now2, 0m, null);

        Assert.Equal(first.Date, now1);
        Assert.Equal(800m, second.Benefice);
        Assert.Equal(200m, second.Moyenne); // 800 DA sur 4 jours
    }
}
