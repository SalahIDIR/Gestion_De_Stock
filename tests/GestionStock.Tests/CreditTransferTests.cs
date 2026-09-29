using GestionStock.Core.Data;
using GestionStock.Core.Domain;
using GestionStock.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace GestionStock.Tests;

/// <summary>Modem factice : ne touche à aucun matériel. Les réponses USSD sont consommées dans l'ordre des appels.</summary>
public class FakeModemPort : IModemPort
{
    public Queue<string?> UssdResponses { get; } = new();
    public string? SmsResponse { get; set; }
    public List<(string ComPort, string Code)> UssdCalls { get; } = new();
    public List<string> SmsCalls { get; } = new();

    public string? SendUssd(string comPort, string ussdCode, TimeSpan timeout)
    {
        UssdCalls.Add((comPort, ussdCode));
        return UssdResponses.Count > 0 ? UssdResponses.Dequeue() : null;
    }

    public string? WaitForSms(string comPort, TimeSpan timeout)
    {
        SmsCalls.Add(comPort);
        return SmsResponse;
    }
}

public class CreditTransferTests
{
    private sealed record Ctx(TestDb Db, Client Client, Product Flexy, Product Cards, DeliveryService Deliveries, FakeModemPort Modem, CreditTransferService Transfers);

    /// <summary>Djezzy configuré comme décrit par l'utilisateur : *760*{numero}*{montant}*2008#, confirmation "1", succès si "TRANSFERE" (pas par SMS).</summary>
    private static async Task<Ctx> SetupAsync(bool viaSms = false)
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
        await settings.SaveOperatorRoutingAsync(djezzy.Id, "COM5", "*760*{numero}*{montant}*2008#", "1", "TRANSFERE", viaSms);

        var client = await new ClientService(db).SaveAsync(new Client { Name = "Boutique" });
        var modem = new FakeModemPort();
        return new Ctx(db, client, flexy, cards, new DeliveryService(db), modem, new CreditTransferService(db, modem));
    }

    private static async Task<DeliveryLine> LineOf(Ctx c, int noteId)
    {
        await using var db = c.Db.CreateDbContext();
        return await db.DeliveryLines.Include(l => l.Product).SingleAsync(l => l.DeliveryNoteId == noteId);
    }

    private static Task<DeliveryNote> CreateNoteAsync(Ctx c, string phone = "0778123456")
        => c.Deliveries.CreateAsync(new DeliveryInput(c.Client.Id, DateTime.Today, [new DeliveryLineInput(c.Flexy.Id, 10_000m, 1m, phone)], 0m));

    [Fact]
    public async Task Successful_two_step_exchange_marks_the_line_Sent_and_builds_the_ussd_code_from_the_template()
    {
        var c = await SetupAsync();
        using var _ = c.Db;
        c.Modem.UssdResponses.Enqueue("VOUS VOULEZ TRANSFERER 10000 DA... 1: POUR CONFIRMER");
        c.Modem.UssdResponses.Enqueue("10000 DA TRANSFERE DE VOTRE COMPTE VERS LE NUMERO 0778123456");

        var note = await CreateNoteAsync(c);
        var results = await c.Transfers.SendPendingAsync(note.Id);

        Assert.Single(results);
        Assert.True(results[0].Success);
        Assert.Equal(2, c.Modem.UssdCalls.Count);
        Assert.Equal(("COM5", "*760*0778123456*10000*2008#"), c.Modem.UssdCalls[0]); // 1er échange : le code
        Assert.Equal(("COM5", "1"), c.Modem.UssdCalls[1]); // 2ème échange : la confirmation

        var line = await LineOf(c, note.Id);
        Assert.Equal(UssdSendStatus.Sent, line.UssdStatus);
        Assert.Contains("TRANSFERE", line.UssdMessage);
    }

    [Fact]
    public async Task Final_message_without_the_success_keyword_is_a_failure()
    {
        var c = await SetupAsync();
        using var _ = c.Db;
        c.Modem.UssdResponses.Enqueue("VOUS VOULEZ TRANSFERER 10000 DA... 1: POUR CONFIRMER");
        c.Modem.UssdResponses.Enqueue("SOLDE INSUFFISANT POUR CETTE OPERATION");

        var note = await CreateNoteAsync(c);
        var results = await c.Transfers.SendPendingAsync(note.Id);

        Assert.False(results[0].Success);
        Assert.Equal(UssdSendStatus.Failed, (await LineOf(c, note.Id)).UssdStatus);
    }

    [Fact]
    public async Task No_response_to_the_initial_code_fails_without_attempting_the_confirmation()
    {
        var c = await SetupAsync();
        using var _ = c.Db;
        // La file est vide : le premier SendUssd renvoie déjà null (pas de réponse du modem).

        var note = await CreateNoteAsync(c);
        var results = await c.Transfers.SendPendingAsync(note.Id);

        Assert.False(results[0].Success);
        Assert.Single(c.Modem.UssdCalls); // un seul échange tenté, pas de confirmation envoyée sans invite reçue
    }

    [Fact]
    public async Task Confirmation_via_sms_reads_the_success_keyword_from_the_sms_not_the_ussd_reply()
    {
        var c = await SetupAsync(viaSms: true);
        using var _ = c.Db;
        c.Modem.UssdResponses.Enqueue("Vous voulez transferer 10000 DA a 0778123456 ! 1 OK, 0 annuler");
        c.Modem.UssdResponses.Enqueue("Votre demande est prise en charge, un sms vous sera envoye"); // ne contient pas TRANSFERE
        c.Modem.SmsResponse = "Vous avez transfere 10000 DA vers le 0778123456 de votre GTS. Numero de la transaction TRANSFERE-123";

        var note = await CreateNoteAsync(c);
        var results = await c.Transfers.SendPendingAsync(note.Id);

        Assert.True(results[0].Success); // le mot-clé "TRANSFERE" est dans le SMS, pas dans la réponse USSD
        Assert.Single(c.Modem.SmsCalls);
        Assert.Equal(c.Modem.SmsResponse, (await LineOf(c, note.Id)).UssdMessage);
    }

    [Fact]
    public async Task Confirmation_via_sms_fails_if_no_sms_arrives_before_the_timeout()
    {
        var c = await SetupAsync(viaSms: true);
        using var _ = c.Db;
        c.Modem.UssdResponses.Enqueue("Vous voulez transferer 10000 DA a 0778123456 ! 1 OK, 0 annuler");
        c.Modem.UssdResponses.Enqueue("Votre demande est prise en charge, un sms vous sera envoye");
        c.Modem.SmsResponse = null; // rien ne remonte

        var note = await CreateNoteAsync(c);
        var results = await c.Transfers.SendPendingAsync(note.Id);

        Assert.False(results[0].Success);
    }

    [Fact]
    public async Task A_failed_line_can_be_retried_and_a_sent_line_is_never_resent()
    {
        var c = await SetupAsync();
        using var _ = c.Db;
        c.Modem.UssdResponses.Enqueue("VOUS VOULEZ TRANSFERER..."); // 1ère tentative : échec (pas de mot-clé)
        c.Modem.UssdResponses.Enqueue("ECHEC DE L'OPERATION");

        var note = await CreateNoteAsync(c);
        await c.Transfers.SendPendingAsync(note.Id);
        Assert.Equal(UssdSendStatus.Failed, (await LineOf(c, note.Id)).UssdStatus);
        Assert.Equal(2, c.Modem.UssdCalls.Count);

        c.Modem.UssdResponses.Enqueue("VOUS VOULEZ TRANSFERER..."); // on relance : cette fois ça passe
        c.Modem.UssdResponses.Enqueue("10000 DA TRANSFERE AVEC SUCCES");
        var retry = await c.Transfers.SendPendingAsync(note.Id);

        Assert.Single(retry);
        Assert.True(retry[0].Success);
        Assert.Equal(UssdSendStatus.Sent, (await LineOf(c, note.Id)).UssdStatus);
        Assert.Equal(4, c.Modem.UssdCalls.Count);

        var again = await c.Transfers.SendPendingAsync(note.Id); // déjà envoyé : plus jamais retenté
        Assert.Empty(again);
        Assert.Equal(4, c.Modem.UssdCalls.Count);
    }

    [Fact]
    public async Task Missing_routing_configuration_fails_without_touching_the_modem()
    {
        var c = await SetupAsync();
        using var _ = c.Db;
        var settings = new SettingsService(c.Db);
        var djezzy = (await settings.GetOperatorsAsync()).Single(o => o.Name == "Djezzy");
        await settings.SaveOperatorRoutingAsync(djezzy.Id, null, null); // routage effacé

        var note = await CreateNoteAsync(c);
        var results = await c.Transfers.SendPendingAsync(note.Id);

        Assert.False(results[0].Success);
        Assert.Empty(c.Modem.UssdCalls);
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
        Assert.Empty(c.Modem.UssdCalls);
        Assert.Null((await LineOf(c, note.Id)).UssdStatus);
    }

    [Fact]
    public void ExtractQuoted_reads_the_message_out_of_a_plus_cusd_line()
    {
        var text = "AT+CUSD=1,\"*760*0778123456*10000*2008#\",15\r\n+CUSD: 1,\"VOUS VOULEZ TRANSFERER 10000 DA\",15\r\nOK\r\n";
        Assert.Equal("VOUS VOULEZ TRANSFERER 10000 DA", SerialModemPort.ExtractQuoted(text, "+CUSD:"));
    }

    [Fact]
    public void ExtractQuoted_returns_null_when_there_is_no_cusd_marker()
    {
        Assert.Null(SerialModemPort.ExtractQuoted("ERROR\r\n", "+CUSD:"));
    }

    [Fact]
    public void ParseLastSms_takes_the_most_recent_entry_and_joins_multiline_bodies()
    {
        var listing =
            "+CMGL: 1,\"REC READ\",\"1000\",,\"26/09/29,10:00:00\"\r\n" +
            "Ancien message\r\n" +
            "+CMGL: 2,\"REC UNREAD\",\"1000\",,\"26/09/29,10:05:00\"\r\n" +
            "Vous avez transfere 10000 DA\r\n" +
            "vers le 0778123456 de votre GTS.\r\n" +
            "OK\r\n";

        var sms = SerialModemPort.ParseLastSms(listing);

        Assert.NotNull(sms);
        Assert.Equal(2, sms!.Value.Index);
        Assert.Equal("Vous avez transfere 10000 DA vers le 0778123456 de votre GTS.", sms.Value.Body);
    }

    [Fact]
    public void ParseLastSms_returns_null_when_the_listing_is_empty()
    {
        Assert.Null(SerialModemPort.ParseLastSms("OK\r\n"));
    }
}
