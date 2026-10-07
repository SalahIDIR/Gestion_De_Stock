using GestionStock.Core.Data;
using GestionStock.Core.Domain;
using GestionStock.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace GestionStock.Tests;

/// <summary>Modem factice : ne touche à aucun matériel. Les réponses USSD sont consommées dans l'ordre des appels.</summary>
public class FakeModemPort : IModemPort
{
    private int _inside;

    public Queue<string?> UssdResponses { get; } = new();
    public string? SmsResponse { get; set; }
    public List<(string ComPort, string Code)> UssdCalls { get; } = new();
    public List<string> SmsCalls { get; } = new();

    /// <summary>Durée simulée de chaque échange USSD, pour laisser à des envois simultanés l'occasion de se chevaucher.</summary>
    public TimeSpan UssdDelay { get; set; }

    /// <summary>Nombre maximal d'échanges USSD en cours au même moment (doit rester à 1 sur un même modem).</summary>
    public int MaxConcurrentUssd { get; private set; }

    /// <summary>Appelé à chaque échange USSD, avant la réponse (pour observer l'état de la base pendant un envoi).</summary>
    public Action<string>? OnUssd { get; set; }

    public string? SendUssd(string comPort, string ussdCode, TimeSpan timeout)
    {
        var inside = Interlocked.Increment(ref _inside);
        MaxConcurrentUssd = Math.Max(MaxConcurrentUssd, inside);
        try
        {
            if (UssdDelay > TimeSpan.Zero) Thread.Sleep(UssdDelay);
            lock (UssdCalls) UssdCalls.Add((comPort, ussdCode));
            OnUssd?.Invoke(ussdCode);
            lock (UssdResponses) return UssdResponses.Count > 0 ? UssdResponses.Dequeue() : null;
        }
        finally
        {
            Interlocked.Decrement(ref _inside);
        }
    }

    public IReadOnlySet<int> ListSmsIndexes(string comPort) => new HashSet<int>();

    /// <summary>Renvoie <see cref="SmsResponse"/> seulement s'il est accepté (ex. s'il cite le bon numéro), comme le vrai modem.</summary>
    public string? WaitForSms(string comPort, IReadOnlySet<int> existing, Func<string, bool> accept, TimeSpan timeout)
    {
        SmsCalls.Add(comPort);
        return SmsResponse != null && accept(SmsResponse) ? SmsResponse : null;
    }

    /// <summary>Résultat déjà recollé renvoyé par <see cref="WaitForSmsBatch"/> (le vrai recollage est testé séparément).</summary>
    public string? SmsBatchResponse { get; set; }
    public List<string> SmsBatchCalls { get; } = new();

    public string? WaitForSmsBatch(string comPort, IReadOnlySet<int> existing, TimeSpan timeout)
    {
        SmsBatchCalls.Add(comPort);
        return SmsBatchResponse;
    }

    /// <summary>Résultat de CheckLink par port ; un port absent est considéré comme débranché.</summary>
    public Dictionary<string, ModemCheck> LinkChecks { get; } = new();

    public ModemCheck CheckLink(string comPort)
        => LinkChecks.TryGetValue(comPort, out var check) ? check : new ModemCheck(false, "Le modem ne répond pas.");

    /// <summary>Null par défaut (comme si la question échouait) ; configurable par les tests qui testent l'avertissement.</summary>
    public (int Used, int Total)? StorageUsage { get; set; }

    public (int Used, int Total)? GetSmsStorageUsage(string comPort) => StorageUsage;

    public List<string> CancelUssdCalls { get; } = new();
    public void CancelUssd(string comPort) => CancelUssdCalls.Add(comPort);
}

public class CreditTransferTests
{
    private sealed record Ctx(TestDb Db, Client Client, Product Flexy, Product Cards, DeliveryService Deliveries, FakeModemPort Modem, CreditTransferService Transfers);

    /// <summary>Djezzy configuré comme décrit par l'utilisateur : *760*{numero}*{montant}*2008#, confirmation "1", succès si "TRANSFERE" (pas par SMS).</summary>
    private static async Task<Ctx> SetupAsync(bool viaSms = false, bool onDisk = false)
    {
        var db = onDisk ? await TestDb.CreateOnDiskAsync() : await TestDb.CreateAsync();
        var supplier = await new SupplierService(db).SaveAsync(new Supplier { Reference = "F1", CompanyName = "Grossiste" });
        var products = await new ProductService(db).ListAsync();
        var flexy = products.Single(p => p.Name == "Flexy");
        var cards = products.Single(p => p.Kind == ProductKind.Physical);

        await new PurchaseService(db).CreateAsync(new PurchaseInput(supplier.Id, DateTime.Today,
            [new PurchaseLineInput(flexy.Id, 1_000_000m, 0.9725m), new PurchaseLineInput(cards.Id, 100m, 480m)], 0m));

        var settings = new SettingsService(db);
        var djezzy = (await settings.GetOperatorsAsync()).Single(o => o.Name == "Djezzy");
        await settings.SaveOperatorRoutingAsync(djezzy.Id, "COM5", "*760*{numero}*{montant}*2008#", "1", "TRANSFERE", viaSms, "*766#");

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
    public async Task Confirmation_via_sms_is_uncertain_if_no_sms_arrives_before_the_timeout()
    {
        var c = await SetupAsync(viaSms: true);
        using var _ = c.Db;
        c.Modem.UssdResponses.Enqueue("Vous voulez transferer 10000 DA a 0778123456 ! 1 OK, 0 annuler");
        c.Modem.UssdResponses.Enqueue("Votre demande est prise en charge, un sms vous sera envoye");
        c.Modem.SmsResponse = null; // rien ne remonte

        var note = await CreateNoteAsync(c);
        var results = await c.Transfers.SendPendingAsync(note.Id);

        Assert.False(results[0].Success);
        Assert.Equal(UssdSendStatus.Uncertain, (await LineOf(c, note.Id)).UssdStatus); // confirmé : le crédit est peut-être parti
    }

    [Fact]
    public async Task An_sms_that_does_not_cite_the_recipient_is_not_taken_as_its_confirmation()
    {
        var c = await SetupAsync(viaSms: true);
        using var _ = c.Db;
        c.Modem.UssdResponses.Enqueue("Vous voulez transferer 10000 DA a 0778123456 ! 1 OK, 0 annuler");
        c.Modem.UssdResponses.Enqueue("Votre demande est prise en charge, un sms vous sera envoye");
        // SMS en retard d'un transfert précédent, vers un autre numéro du client.
        c.Modem.SmsResponse = "Vous avez transfere 500 DA vers le 0661000000. TRANSFERE-99";

        var note = await CreateNoteAsync(c);
        var results = await c.Transfers.SendPendingAsync(note.Id);

        Assert.Equal(UssdSendStatus.Uncertain, results[0].Status);
    }

    [Fact]
    public async Task No_answer_to_the_confirmation_is_uncertain_and_never_resent_without_an_explicit_request()
    {
        var c = await SetupAsync();
        using var _ = c.Db;
        c.Modem.UssdResponses.Enqueue("VOUS VOULEZ TRANSFERER 10000 DA... 1: POUR CONFIRMER"); // puis plus rien

        var note = await CreateNoteAsync(c);
        var first = await c.Transfers.SendPendingAsync(note.Id);
        Assert.Equal(UssdSendStatus.Uncertain, first[0].Status);
        Assert.Equal(2, c.Modem.UssdCalls.Count); // le code et la confirmation sont partis

        var retry = await c.Transfers.SendPendingAsync(note.Id); // « Renvoyer » simple : la ligne incertaine est laissée
        Assert.Empty(retry);
        Assert.Equal(2, c.Modem.UssdCalls.Count);

        c.Modem.UssdResponses.Enqueue("VOUS VOULEZ TRANSFERER...");
        c.Modem.UssdResponses.Enqueue("10000 DA TRANSFERE");
        var forced = await c.Transfers.SendPendingAsync(note.Id, resendUncertain: true); // l'utilisateur a vérifié : pas reçu
        Assert.True(forced[0].Success);
        Assert.Equal(UssdSendStatus.Sent, (await LineOf(c, note.Id)).UssdStatus);
    }

    [Fact]
    public async Task An_uncertain_line_can_be_marked_as_sent_after_checking()
    {
        var c = await SetupAsync();
        using var _ = c.Db;
        c.Modem.UssdResponses.Enqueue("VOUS VOULEZ TRANSFERER 10000 DA... 1: POUR CONFIRMER");

        var note = await CreateNoteAsync(c);
        await c.Transfers.SendPendingAsync(note.Id);
        var marked = await c.Transfers.MarkUncertainAsSentAsync(note.Id);

        Assert.Equal(1, marked);
        Assert.Equal(UssdSendStatus.Sent, (await LineOf(c, note.Id)).UssdStatus);
        Assert.Empty(await c.Transfers.SendPendingAsync(note.Id, resendUncertain: true)); // plus jamais renvoyée
    }

    [Fact]
    public async Task The_line_is_saved_as_uncertain_before_the_confirmation_is_sent()
    {
        var c = await SetupAsync();
        using var _ = c.Db;
        c.Modem.UssdResponses.Enqueue("VOUS VOULEZ TRANSFERER...");
        c.Modem.UssdResponses.Enqueue("10000 DA TRANSFERE");
        var note = await CreateNoteAsync(c);

        UssdSendStatus? statusWhenConfirming = null;
        c.Modem.OnUssd = code =>
        {
            if (code != "1") return;
            using var db = c.Db.CreateDbContext();
            statusWhenConfirming = db.DeliveryLines.Single(l => l.DeliveryNoteId == note.Id).UssdStatus;
        };
        await c.Transfers.SendPendingAsync(note.Id);

        // Si l'application plantait pendant la confirmation, la ligne ne serait pas renvoyée automatiquement.
        Assert.Equal(UssdSendStatus.Uncertain, statusWhenConfirming);
        Assert.Equal(UssdSendStatus.Sent, (await LineOf(c, note.Id)).UssdStatus);
    }

    [Fact]
    public async Task Two_lines_of_the_same_note_are_sent_one_after_the_other()
    {
        var c = await SetupAsync();
        using var _ = c.Db;
        foreach (var r in new[] { "INVITE 1", "TRANSFERE 1", "INVITE 2", "TRANSFERE 2" }) c.Modem.UssdResponses.Enqueue(r);

        var note = await c.Deliveries.CreateAsync(new DeliveryInput(c.Client.Id, DateTime.Today,
            [new DeliveryLineInput(c.Flexy.Id, 1_000m, 1m, "0778111111"), new DeliveryLineInput(c.Flexy.Id, 2_000m, 1m, "0778222222")], 0m));
        var results = await c.Transfers.SendPendingAsync(note.Id);

        Assert.All(results, r => Assert.True(r.Success));
        Assert.Equal(["*760*0778111111*1000*2008#", "1", "*760*0778222222*2000*2008#", "1"], c.Modem.UssdCalls.Select(x => x.Code));
    }

    [Fact]
    public async Task Two_notes_sent_at_the_same_time_never_interleave_on_the_same_modem()
    {
        var c = await SetupAsync(onDisk: true);
        using var _ = c.Db;
        c.Modem.UssdDelay = TimeSpan.FromMilliseconds(100);
        foreach (var r in new[] { "INVITE", "TRANSFERE", "INVITE", "TRANSFERE" }) c.Modem.UssdResponses.Enqueue(r);
        var a = await CreateNoteAsync(c, "0778111111");
        var b = await CreateNoteAsync(c, "0778222222");

        var sending = new[] { c.Transfers.SendPendingAsync(a.Id), c.Transfers.SendPendingAsync(b.Id) };
        Assert.True(c.Transfers.IsSending);
        var results = await Task.WhenAll(sending);

        Assert.False(c.Transfers.IsSending);
        Assert.Equal(1, c.Modem.MaxConcurrentUssd);
        // Chaque code est immédiatement suivi de sa propre confirmation : aucune session ne s'intercale dans l'autre.
        var codes = c.Modem.UssdCalls.Select(x => x.Code).ToList();
        Assert.Equal("1", codes[1]);
        Assert.Equal("1", codes[3]);
        Assert.All(results, r => Assert.True(r[0].Success));
    }

    [Theory]
    [InlineData("Vous avez transfere 10000 DA vers le 0778123456.", "0778123456", true)]
    [InlineData("Transfert vers 213778123456 effectue", "0778123456", true)]
    [InlineData("Transfert vers 778 12 34 56 effectue", "0778123456", true)]
    [InlineData("Vous avez transfere 500 DA vers le 0661000000.", "0778123456", false)]
    [InlineData("Votre solde est de 5000 DA", "0778123456", false)]
    public void MentionsNumber_matches_the_number_whatever_its_format(string sms, string phone, bool expected)
    {
        Assert.Equal(expected, CreditTransferService.MentionsNumber(sms, phone));
    }

    [Fact]
    public void ParseSmsList_returns_every_message_with_its_index()
    {
        var listing = "+CMGL: 1,\"REC READ\",\"+213...\",,\"26/10/07,10:00:00+04\"\r\nAncien\r\n" +
                      "+CMGL: 4,\"REC UNREAD\",\"+213...\",,\"26/10/07,10:05:00+04\"\r\nNouveau\r\nOK\r\n";

        Assert.Equal([(1, "Ancien"), (4, "Nouveau")], SerialModemPort.ParseSmsList(listing));
    }

    [Fact]
    public void ParseSmsList_decodes_a_ucs2_hex_body_even_when_split_across_several_lines()
    {
        // "ABCD" en UCS2 hexadecimal ("0041004200430044") coupe en deux lignes par le modem : chaque ligne doit etre
        // decodee seule, sinon l'espace de jonction casse la detection "tout en hexadecimal" (bug reel observe).
        var listing = "+CMGL: 1,\"REC READ\",\"+213...\",,\"26/10/07,10:00:00+04\"\r\n00410042\r\n00430044\r\nOK\r\n";

        var sms = Assert.Single(SerialModemPort.ParseSmsList(listing));
        Assert.Equal("AB CD", sms.Body);
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
    public async Task CheckBalancesAsync_sends_the_balance_code_and_returns_the_modem_reply()
    {
        var c = await SetupAsync();
        using var _ = c.Db;
        c.Modem.UssdResponses.Enqueue("Solde disponible : 5000 DA");

        var results = await c.Transfers.CheckBalancesAsync();

        var djezzy = results.Single(r => r.OperatorName == "Djezzy");
        Assert.True(djezzy.Success);
        Assert.Equal("Solde disponible : 5000 DA", djezzy.Message);
        Assert.Contains(("COM5", "*766#"), c.Modem.UssdCalls); // le gabarit par défaut de Djezzy, sans confirmation
    }

    [Fact]
    public async Task CheckBalancesAsync_reports_missing_configuration_without_touching_the_modem()
    {
        var c = await SetupAsync(); // seul Djezzy a un port COM configuré dans SetupAsync
        using var _ = c.Db;

        var results = await c.Transfers.CheckBalancesAsync();

        var ooredoo = results.Single(r => r.OperatorName == "Ooredoo");
        Assert.False(ooredoo.Success);
        Assert.DoesNotContain(c.Modem.UssdCalls, call => call.ComPort != "COM5"); // jamais appelé pour un opérateur non configuré
    }

    [Fact]
    public async Task CheckBalancesAsync_reports_failure_when_the_modem_does_not_respond()
    {
        var c = await SetupAsync();
        using var _ = c.Db;
        // La file est vide : SendUssd renvoie null (pas de réponse). CheckLink (non configuré) répond "ne répond pas" par défaut.

        var results = await c.Transfers.CheckBalancesAsync();

        var djezzy = results.Single(r => r.OperatorName == "Djezzy");
        Assert.False(djezzy.Success);
        Assert.Equal("Le modem ne répond pas.", djezzy.Message);
    }

    [Fact]
    public async Task CheckBalancesAsync_reports_the_real_cause_when_the_port_cannot_even_be_opened()
    {
        var c = await SetupAsync();
        using var _ = c.Db;
        c.Modem.LinkChecks["COM5"] = new ModemCheck(false, "Impossible d'ouvrir le port COM5 (modem débranché ou déjà utilisé).");
        // La file est vide : SendUssd renvoie null comme si le port était occupé par un autre programme.

        var results = await c.Transfers.CheckBalancesAsync();

        Assert.Equal("Impossible d'ouvrir le port COM5 (modem débranché ou déjà utilisé).",
            results.Single(r => r.OperatorName == "Djezzy").Message);
    }

    [Fact]
    public async Task CheckBalancesAsync_reports_a_generic_message_when_the_link_is_fine_but_ussd_still_fails()
    {
        var c = await SetupAsync();
        using var _ = c.Db;
        c.Modem.LinkChecks["COM5"] = new ModemCheck(true, "En ligne.");
        // La file est vide : SendUssd renvoie null malgré une liaison qui fonctionne (ex. requête USSD refusée).

        var results = await c.Transfers.CheckBalancesAsync();

        Assert.Equal("Aucune réponse à la requête de solde (la liaison de base fonctionne).",
            results.Single(r => r.OperatorName == "Djezzy").Message);
    }

    [Fact]
    public async Task CheckBalancesAsync_rebuilds_the_mobilis_balance_from_an_sms_split_in_two_and_shows_only_its_distributeur()
    {
        var c = await SetupAsync();
        using var _ = c.Db;
        var settings = new SettingsService(c.Db);
        var mobilis = (await settings.GetOperatorsAsync()).Single(o => o.Name == "Mobilis");
        // Le "03" juste après {numero} désigne le distributeur Data (3e de la liste Poste/Assilou/Data/GTS).
        // Mobilis confirme par SMS (comme pour un transfert) : la réponse USSD n'est qu'un accusé de réception.
        await settings.SaveOperatorRoutingAsync(mobilis.Id, "COM6", "*630*{numero}*03*{montant}*00000#", "1", "TRANSFERE", true, "*632*01*00000#");
        c.Modem.UssdResponses.Enqueue("peu importe, consomme par Djezzy (deja configure dans SetupAsync)");
        c.Modem.UssdResponses.Enqueue("Votre demande est prise en charge, un sms vous sera envoye.");
        // Exemple réel : le SMS arrive coupé en deux, en plein mot ("...DAT" puis "A est...").
        c.Modem.SmsBatchResponse = "Votre Balance: POSTE est : 0.00 DZD . ASSILOU est : 0.00 DZD . DATA est : 105.00 DZD . GTS est : 0.00 DZD . MOBILIS est : 20.00 DZD .";

        var results = await c.Transfers.CheckBalancesAsync();

        var mobilisResult = results.Single(r => r.OperatorName == "Mobilis");
        Assert.True(mobilisResult.Success);
        Assert.Equal("105.00 DZD", mobilisResult.Message);
        Assert.Contains("COM6", c.Modem.SmsBatchCalls);
    }

    [Fact]
    public async Task CheckBalancesAsync_shows_the_raw_ussd_reply_when_mobilis_sms_never_arrives()
    {
        var c = await SetupAsync();
        using var _ = c.Db;
        var settings = new SettingsService(c.Db);
        var mobilis = (await settings.GetOperatorsAsync()).Single(o => o.Name == "Mobilis");
        await settings.SaveOperatorRoutingAsync(mobilis.Id, "COM6", "*630*{numero}*03*{montant}*00000#", "1", "TRANSFERE", true, "*632*01*00000#");
        c.Modem.UssdResponses.Enqueue("peu importe, consomme par Djezzy (deja configure dans SetupAsync)");
        c.Modem.UssdResponses.Enqueue("Votre demande est prise en charge, un sms vous sera envoye.");
        // SmsBatchResponse reste null : aucun SMS ne "arrive" avant la fin du délai.

        var results = await c.Transfers.CheckBalancesAsync();

        var mobilisResult = results.Single(r => r.OperatorName == "Mobilis");
        Assert.True(mobilisResult.Success); // la requête USSD a bien eu une réponse, même sans le détail du solde
        Assert.Equal("Votre demande est prise en charge, un sms vous sera envoye.", mobilisResult.Message);
    }

    [Fact]
    public async Task CheckBalancesAsync_warns_when_the_sim_sms_storage_is_almost_full()
    {
        var c = await SetupAsync();
        using var _ = c.Db;
        var settings = new SettingsService(c.Db);
        var mobilis = (await settings.GetOperatorsAsync()).Single(o => o.Name == "Mobilis");
        await settings.SaveOperatorRoutingAsync(mobilis.Id, "COM6", "*630*{numero}*03*{montant}*00000#", "1", "TRANSFERE", true, "*632*01*00000#");
        c.Modem.UssdResponses.Enqueue("peu importe, consomme par Djezzy (deja configure dans SetupAsync)");
        c.Modem.UssdResponses.Enqueue("Votre demande est prise en charge, un sms vous sera envoye.");
        c.Modem.SmsBatchResponse = "Votre Balance: DATA est : 105.00 DZD .";
        c.Modem.StorageUsage = (25, 25); // pleine, comme observé sur la vraie carte SIM

        var results = await c.Transfers.CheckBalancesAsync();

        var mobilisResult = results.Single(r => r.OperatorName == "Mobilis");
        Assert.Contains("105.00 DZD", mobilisResult.Message);
        Assert.Contains("mémoire SMS", mobilisResult.Message);
        Assert.Contains("25/25", mobilisResult.Message);
    }

    [Fact]
    public async Task CheckBalancesAsync_does_not_warn_when_the_sim_sms_storage_has_room()
    {
        var c = await SetupAsync();
        using var _ = c.Db;
        var settings = new SettingsService(c.Db);
        var mobilis = (await settings.GetOperatorsAsync()).Single(o => o.Name == "Mobilis");
        await settings.SaveOperatorRoutingAsync(mobilis.Id, "COM6", "*630*{numero}*03*{montant}*00000#", "1", "TRANSFERE", true, "*632*01*00000#");
        c.Modem.UssdResponses.Enqueue("peu importe, consomme par Djezzy (deja configure dans SetupAsync)");
        c.Modem.UssdResponses.Enqueue("Votre demande est prise en charge, un sms vous sera envoye.");
        c.Modem.SmsBatchResponse = "Votre Balance: DATA est : 105.00 DZD .";
        c.Modem.StorageUsage = (3, 25);

        var results = await c.Transfers.CheckBalancesAsync();

        Assert.Equal("105.00 DZD", results.Single(r => r.OperatorName == "Mobilis").Message);
    }

    [Theory]
    [InlineData(25, 25, true)]
    [InlineData(20, 25, true)] // 80% : seuil atteint
    [InlineData(19, 25, false)]
    [InlineData(0, 25, false)]
    public void AppendStorageWarning_warns_from_eighty_percent_full(int used, int total, bool expectWarning)
    {
        var message = CreditTransferService.AppendStorageWarning("Solde : 100 DZD", (used, total));
        Assert.Equal(expectWarning, message.Contains("mémoire SMS"));
    }

    [Fact]
    public void AppendStorageWarning_leaves_the_message_alone_when_usage_is_unknown()
    {
        Assert.Equal("Solde : 100 DZD", CreditTransferService.AppendStorageWarning("Solde : 100 DZD", null));
    }

    [Fact]
    public void ParseStorageUsage_reads_used_and_total_from_a_cpms_reply()
    {
        Assert.Equal((25, 25), SerialModemPort.ParseStorageUsage("+CPMS: \"SM\",25,25,\"SM\",25,25,\"SM\",25,25"));
        Assert.Null(SerialModemPort.ParseStorageUsage("ERROR"));
    }

    [Fact]
    public async Task CheckBalancesAsync_closes_the_ussd_session_after_reading_the_balance()
    {
        var c = await SetupAsync();
        using var _ = c.Db;
        c.Modem.UssdResponses.Enqueue("VOTRE SOLDE EST 6523.35 DA.\n1:Plus de detail Bonus");

        await c.Transfers.CheckBalancesAsync();

        Assert.Contains("COM5", c.Modem.CancelUssdCalls); // referme la session pour que la requete suivante ne soit pas refusee
    }

    [Fact]
    public async Task CheckBalancesAsync_shows_only_the_number_after_votre_solde_est_for_djezzy()
    {
        var c = await SetupAsync();
        using var _ = c.Db;
        c.Modem.UssdResponses.Enqueue(
            "VOTRE SOLDE EST 6523.35 DA. VOTRE ANCIEN CREDIT EST 0.00 DA.VOTRE BONUS EST 3852.90 DA. VOTRE BONUS INTERNET EST 0.00 MO.\n1:Plus de detail Bonus");

        var results = await c.Transfers.CheckBalancesAsync();

        Assert.Equal("6523.35 DA", results.Single(r => r.OperatorName == "Djezzy").Message);
    }

    [Theory]
    [InlineData("VOTRE SOLDE EST 6523.35 DA. VOTRE ANCIEN CREDIT EST 0.00 DA.", "6523.35 DA")]
    [InlineData("votre solde est : 100 DZD", "100 DZD")]
    [InlineData("Solde disponible : 5000 DA", null)]
    public void ExtractSoldeEst_reads_only_the_number_and_unit(string text, string? expected)
    {
        Assert.Equal(expected, CreditTransferService.ExtractSoldeEst(text));
    }

    [Theory]
    [InlineData("Votre credit Storm-Credit est 10447 Dinar Fidélité:50DA.", "10447")]
    [InlineData("votre credit storm-credit est : 99", "99")]
    [InlineData("VOTRE SOLDE EST 6523.35 DA.", null)]
    public void ExtractStormCredit_reads_only_the_number(string text, string? expected)
    {
        Assert.Equal(expected, CreditTransferService.ExtractStormCredit(text));
    }

    [Fact]
    public async Task CheckBalancesAsync_shows_only_the_number_for_ooredoo_storm_credit()
    {
        var c = await SetupAsync();
        using var _ = c.Db;
        var settings = new SettingsService(c.Db);
        var ooredoo = (await settings.GetOperatorsAsync()).Single(o => o.Name == "Ooredoo");
        await settings.SaveOperatorRoutingAsync(ooredoo.Id, "COM20", "*599*{numero}*{montant}*2008#", "1", "STORMCREDIT", false, "*200*2008#");
        c.Modem.UssdResponses.Enqueue("peu importe, consomme par Djezzy (deja configure dans SetupAsync)");
        c.Modem.UssdResponses.Enqueue("Votre credit Storm-Credit est 10447 Dinar Fidélité:50DA.");

        var results = await c.Transfers.CheckBalancesAsync();

        Assert.Equal("10447", results.Single(r => r.OperatorName == "Ooredoo").Message);
    }

    [Fact]
    public async Task CheckBalancesAsync_shows_the_full_reply_when_no_distributeur_can_be_matched()
    {
        var c = await SetupAsync(); // Djezzy n'a pas de decoupage par distributeur
        using var _ = c.Db;
        c.Modem.UssdResponses.Enqueue("Solde disponible : 5000 DA");

        var results = await c.Transfers.CheckBalancesAsync();

        Assert.Equal("Solde disponible : 5000 DA", results.Single(r => r.OperatorName == "Djezzy").Message);
    }

    [Theory]
    [InlineData("*630*{numero}*03*{montant}*00000#", 3)]
    [InlineData("*760*{numero}*{montant}*2008#", null)]
    [InlineData(null, null)]
    public void ExtractDistributeurNumber_reads_the_digit_right_after_numero(string? template, int? expected)
    {
        Assert.Equal(expected, CreditTransferService.ExtractDistributeurNumber(template));
    }

    [Fact]
    public void ExtractDistributeurBalance_finds_the_value_after_the_distributeur_name()
    {
        const string reply = "Poste est : 10.00 DZD . Assilou est : 0.00 DZD . Data est : 155.00 DZD . GTS est : 25.00 DZD .";
        Assert.Equal("155.00", CreditTransferService.ExtractDistributeurBalance(reply, 3));
        Assert.Equal("25.00", CreditTransferService.ExtractDistributeurBalance(reply, 4));
        Assert.Null(CreditTransferService.ExtractDistributeurBalance(reply, 5)); // aucun distributeur numero 5
    }

    [Fact]
    public void ExtractDistributeurBalance_returns_null_when_the_label_is_absent()
    {
        Assert.Null(CreditTransferService.ExtractDistributeurBalance("Reponse sans les distributeurs attendus", 3));
    }

    [Fact]
    public void DecodeIfUcs2Hex_decodes_ucs2_hex_and_leaves_plain_text_alone()
    {
        Assert.Equal("ABCD", SerialModemPort.DecodeIfUcs2Hex("0041004200430044"));
        Assert.Equal("Solde disponible : 5000 DA", SerialModemPort.DecodeIfUcs2Hex("Solde disponible : 5000 DA"));
        Assert.Equal("0778123456", SerialModemPort.DecodeIfUcs2Hex("0778123456"));
    }

    [Fact]
    public async Task CheckConnectionsAsync_reports_each_operator_and_sends_nothing_to_the_operator()
    {
        var c = await SetupAsync(); // seul Djezzy a un port COM configuré dans SetupAsync
        using var _ = c.Db;
        c.Modem.LinkChecks["COM5"] = new ModemCheck(true, "En ligne.");

        var results = await c.Transfers.CheckConnectionsAsync();

        Assert.Equal(3, results.Count);
        Assert.True(results.Single(r => r.OperatorName == "Djezzy").Online);
        var ooredoo = results.Single(r => r.OperatorName == "Ooredoo");
        Assert.False(ooredoo.Online);
        Assert.Contains("non configuré", ooredoo.Message);
        Assert.Empty(c.Modem.UssdCalls); // aucune requête USSD : le test est gratuit
    }

    [Fact]
    public async Task CheckConnectionsAsync_reports_an_unreachable_modem_as_offline()
    {
        var c = await SetupAsync();
        using var _ = c.Db;
        // Aucun résultat pour COM5 : le faux modem le considère comme débranché.

        var results = await c.Transfers.CheckConnectionsAsync();

        var djezzy = results.Single(r => r.OperatorName == "Djezzy");
        Assert.False(djezzy.Online);
        Assert.Equal("Le modem ne répond pas.", djezzy.Message);
    }

    [Theory]
    [InlineData("AT+CREG?\r\n+CREG: 0,1\r\nOK\r\n", true)]
    [InlineData("+CREG: 2,5,\"1A2B\",\"00C3\"\r\nOK\r\n", true)]
    [InlineData("+CEREG: 0,1\r\nOK\r\n", true)]
    [InlineData("+CREG: 0,2\r\nOK\r\n", false)] // recherche de réseau en cours
    [InlineData("+CREG: 0,3\r\nOK\r\n", false)] // inscription refusée
    [InlineData("+CREG: 0,0\r\nOK\r\n", false)]
    [InlineData("ERROR\r\n", false)]
    public void IsRegistered_reads_the_network_registration_state(string reply, bool expected)
    {
        Assert.Equal(expected, SerialModemPort.IsRegistered(reply));
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
    public void ExtractQuoted_reads_the_message_even_with_a_real_line_break_inside_the_quotes()
    {
        // Cas reel observe (Djezzy, *766#) : le modem renvoie un vrai retour a la ligne a l'interieur du texte.
        var text = "AT+CUSD=1,\"*766#\",15\r\nOK\r\n\r\n+CUSD: 1,\"VOTRE SOLDE EST 6523.35 DA.\n1:Plus de detail Bonus\",68\r\n";

        Assert.Equal("VOTRE SOLDE EST 6523.35 DA.\n1:Plus de detail Bonus", SerialModemPort.ExtractQuoted(text, "+CUSD:"));
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
