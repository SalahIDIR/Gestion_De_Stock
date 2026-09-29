using GestionStock.Core.Data;
using GestionStock.Core.Domain;
using GestionStock.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace GestionStock.Tests;

/// <summary>Modem factice : ne touche à aucun matériel, se contente d'enregistrer les appels et de renvoyer une réponse programmée.</summary>
public class FakeModemPort : IModemPort
{
    public Func<string, string, UssdSendResult> Handler { get; set; } = (_, _) => new UssdSendResult(true, "OK");
    public List<(string ComPort, string Code)> Calls { get; } = new();

    public UssdSendResult SendUssd(string comPort, string ussdCode, TimeSpan timeout)
    {
        Calls.Add((comPort, ussdCode));
        return Handler(comPort, ussdCode);
    }
}

public class CreditTransferTests
{
    private sealed record Ctx(TestDb Db, Client Client, Product Flexy, Product Cards, DeliveryService Deliveries, FakeModemPort Modem, CreditTransferService Transfers);

    private static async Task<Ctx> SetupAsync()
    {
        var db = await TestDb.CreateAsync();
        var supplier = await new SupplierService(db).SaveAsync(new Supplier { Reference = "F1", CompanyName = "Grossiste" });
        var products = await new ProductService(db).ListAsync();
        var flexy = products.Single(p => p.Name == "Flexy");
        var cards = products.Single(p => p.Kind == ProductKind.Physical);

        await new PurchaseService(db).CreateAsync(new PurchaseInput(supplier.Id, DateTime.Today,
            [new PurchaseLineInput(flexy.Id, 1_000_000m, 0.9725m), new PurchaseLineInput(cards.Id, 100m, 480m)], 0m));

        var settings = new SettingsService(db);
        var djezzy = (await settings.GetOperatorsAsync()).Single(o => o.Name == "Djezzy");
        await settings.SaveOperatorRoutingAsync(djezzy.Id, "COM5", "*760*{numero}*{montant}#");

        var client = await new ClientService(db).SaveAsync(new Client { Name = "Boutique" });
        var modem = new FakeModemPort();
        return new Ctx(db, client, flexy, cards, new DeliveryService(db), modem, new CreditTransferService(db, modem));
    }

    private static async Task<DeliveryLine> LineOf(Ctx c, int noteId)
    {
        await using var db = c.Db.CreateDbContext();
        return (await db.DeliveryLines.Include(l => l.Product).SingleAsync(l => l.DeliveryNoteId == noteId));
    }

    [Fact]
    public async Task Successful_send_marks_the_line_Sent_and_builds_the_ussd_code_from_the_template()
    {
        var c = await SetupAsync();
        using var _ = c.Db;
        c.Modem.Handler = (_, _) => new UssdSendResult(true, "Transfert de 10 000,00 DA effectue");

        var note = await c.Deliveries.CreateAsync(new DeliveryInput(c.Client.Id, DateTime.Today,
            [new DeliveryLineInput(c.Flexy.Id, 10_000m, 1m, "0778123456")], 0m));

        var results = await c.Transfers.SendPendingAsync(note.Id);

        Assert.Single(results);
        Assert.True(results[0].Success);
        Assert.Equal(("COM5", "*760*0778123456*10000#"), c.Modem.Calls.Single());

        var line = await LineOf(c, note.Id);
        Assert.Equal(UssdSendStatus.Sent, line.UssdStatus);
        Assert.Equal("Transfert de 10 000,00 DA effectue", line.UssdMessage);
    }

    [Fact]
    public async Task Failed_send_marks_the_line_Failed_and_can_be_retried_without_resending_once_sent()
    {
        var c = await SetupAsync();
        using var _ = c.Db;
        c.Modem.Handler = (_, _) => new UssdSendResult(false, "Le modem ne répond pas (AT).");

        var note = await c.Deliveries.CreateAsync(new DeliveryInput(c.Client.Id, DateTime.Today,
            [new DeliveryLineInput(c.Flexy.Id, 10_000m, 1m, "0778123456")], 0m));
        await c.Transfers.SendPendingAsync(note.Id);

        Assert.Equal(UssdSendStatus.Failed, (await LineOf(c, note.Id)).UssdStatus);
        Assert.Single(c.Modem.Calls);

        // On corrige le "matériel" (le modem répond maintenant) et on relance : seule la ligne en échec est retentée.
        c.Modem.Handler = (_, _) => new UssdSendResult(true, "OK");
        var retry = await c.Transfers.SendPendingAsync(note.Id);

        Assert.Single(retry);
        Assert.True(retry[0].Success);
        Assert.Equal(UssdSendStatus.Sent, (await LineOf(c, note.Id)).UssdStatus);
        Assert.Equal(2, c.Modem.Calls.Count);

        // Une ligne déjà envoyée n'est plus jamais retentée.
        var again = await c.Transfers.SendPendingAsync(note.Id);
        Assert.Empty(again);
        Assert.Equal(2, c.Modem.Calls.Count);
    }

    [Fact]
    public async Task Missing_com_port_or_template_fails_without_touching_the_modem()
    {
        var c = await SetupAsync();
        using var _ = c.Db;
        var settings = new SettingsService(c.Db);
        var djezzy = (await settings.GetOperatorsAsync()).Single(o => o.Name == "Djezzy");
        await settings.SaveOperatorRoutingAsync(djezzy.Id, null, null); // routage effacé

        var note = await c.Deliveries.CreateAsync(new DeliveryInput(c.Client.Id, DateTime.Today,
            [new DeliveryLineInput(c.Flexy.Id, 10_000m, 1m, "0778123456")], 0m));
        var results = await c.Transfers.SendPendingAsync(note.Id);

        Assert.False(results[0].Success);
        Assert.Empty(c.Modem.Calls); // le modem n'est jamais sollicité si le routage n'est pas configuré
        Assert.Equal(UssdSendStatus.Failed, (await LineOf(c, note.Id)).UssdStatus);
    }

    [Fact]
    public async Task A_physical_product_line_is_never_sent()
    {
        var c = await SetupAsync();
        using var _ = c.Db;

        var note = await c.Deliveries.CreateAsync(new DeliveryInput(c.Client.Id, DateTime.Today,
            [new DeliveryLineInput(c.Cards.Id, 5m, 500m)], 0m));
        var results = await c.Transfers.SendPendingAsync(note.Id);

        Assert.Empty(results);
        Assert.Empty(c.Modem.Calls);
        Assert.Null((await LineOf(c, note.Id)).UssdStatus);
    }
}
