using GestionStock.Core.Domain;
using GestionStock.Core.Services;

namespace GestionStock.Tests;

public class MasterDataTests
{
    [Fact]
    public async Task Database_is_seeded_with_operators_and_products()
    {
        using var db = await TestDb.CreateAsync();

        var operators = await new SettingsService(db).GetOperatorsAsync();
        Assert.Equal(["Djezzy", "Ooredoo", "Mobilis"], operators.Select(o => o.Name));

        var products = await new ProductService(db).ListAsync();
        Assert.Contains(products, p => p.Name == "Flexy" && p.Kind == ProductKind.VirtualCredit);
        Assert.Contains(products, p => p.Kind == ProductKind.Physical);
    }

    [Fact]
    public async Task Supplier_reference_must_be_unique_and_fields_are_required()
    {
        using var db = await TestDb.CreateAsync();
        var suppliers = new SupplierService(db);

        await suppliers.SaveAsync(new Supplier { Reference = "F1", CompanyName = "A" });
        await Assert.ThrowsAsync<BusinessException>(() => suppliers.SaveAsync(new Supplier { Reference = "F1", CompanyName = "B" }));
        await Assert.ThrowsAsync<BusinessException>(() => suppliers.SaveAsync(new Supplier { Reference = "", CompanyName = "B" }));
        await Assert.ThrowsAsync<BusinessException>(() => suppliers.SaveAsync(new Supplier { Reference = "F2", CompanyName = " " }));
    }

    [Fact]
    public async Task Supplier_can_be_edited_in_place()
    {
        using var db = await TestDb.CreateAsync();
        var suppliers = new SupplierService(db);
        var s = await suppliers.SaveAsync(new Supplier { Reference = "F1", CompanyName = "A" });

        await suppliers.SaveAsync(new Supplier { Id = s.Id, Reference = "F1", CompanyName = "A bis", Phone1 = " 0550 " });

        var stored = Assert.Single(await suppliers.ListAsync());
        Assert.Equal("A bis", stored.CompanyName);
        Assert.Equal("0550", stored.Phone1);
    }

    [Fact]
    public async Task Client_keeps_one_chip_per_operator_and_replaces_chips_on_save()
    {
        using var db = await TestDb.CreateAsync();
        var clients = new ClientService(db);
        var ops = await new SettingsService(db).GetOperatorsAsync();
        var djezzy = ops.Single(o => o.Name == "Djezzy");
        var mobilis = ops.Single(o => o.Name == "Mobilis");

        var saved = await clients.SaveAsync(new Client
        {
            Name = "Boutique Aokas",
            City = "Aokas",
            CreditLimit = 15_000_000m,
            Chips =
            {
                new ClientChip { OperatorId = djezzy.Id, PhoneNumber = "0770123456" },
                new ClientChip { OperatorId = mobilis.Id, PhoneNumber = "" },
            },
        });
        Assert.Single((await clients.ListAsync()).Single().Chips);

        await clients.SaveAsync(new Client
        {
            Id = saved.Id,
            Name = "Boutique Aokas",
            Chips = { new ClientChip { OperatorId = mobilis.Id, PhoneNumber = "0660123456" } },
        });
        var chip = Assert.Single((await clients.ListAsync()).Single().Chips);
        Assert.Equal(mobilis.Id, chip.OperatorId);
    }

    [Fact]
    public async Task Client_validation()
    {
        using var db = await TestDb.CreateAsync();
        var clients = new ClientService(db);
        var djezzy = (await new SettingsService(db).GetOperatorsAsync()).First();

        await Assert.ThrowsAsync<BusinessException>(() => clients.SaveAsync(new Client { Name = " " }));
        await Assert.ThrowsAsync<BusinessException>(() => clients.SaveAsync(new Client { Name = "X", CreditLimit = -1 }));
        await Assert.ThrowsAsync<BusinessException>(() => clients.SaveAsync(new Client
        {
            Name = "X",
            Chips =
            {
                new ClientChip { OperatorId = djezzy.Id, PhoneNumber = "0770123456" },
                new ClientChip { OperatorId = djezzy.Id, PhoneNumber = "0770123457" },
            },
        }));
        await Assert.ThrowsAsync<BusinessException>(() => clients.SaveAsync(new Client
        {
            Name = "X",
            Chips = { new ClientChip { OperatorId = djezzy.Id, PhoneNumber = "07 70 abc" } },
        }));
    }

    [Fact]
    public async Task Client_can_have_a_second_chip_per_operator_but_not_a_third()
    {
        using var db = await TestDb.CreateAsync();
        var clients = new ClientService(db);
        var djezzy = (await new SettingsService(db).GetOperatorsAsync()).First();

        var saved = await clients.SaveAsync(new Client
        {
            Name = "Boutique",
            Chips =
            {
                new ClientChip { OperatorId = djezzy.Id, Slot = 1, PhoneNumber = "0770000001" },
                new ClientChip { OperatorId = djezzy.Id, Slot = 2, PhoneNumber = "0770000002" },
            },
        });

        var chips = (await clients.ListAsync()).Single(c => c.Id == saved.Id).Chips;
        Assert.Equal(2, chips.Count);
        Assert.Contains(chips, c => c.Slot == 1 && c.PhoneNumber == "0770000001");
        Assert.Contains(chips, c => c.Slot == 2 && c.PhoneNumber == "0770000002");

        // Deux fois le même slot pour le même opérateur est refusé.
        await Assert.ThrowsAsync<BusinessException>(() => clients.SaveAsync(new Client
        {
            Name = "X",
            Chips =
            {
                new ClientChip { OperatorId = djezzy.Id, Slot = 1, PhoneNumber = "0770000003" },
                new ClientChip { OperatorId = djezzy.Id, Slot = 1, PhoneNumber = "0770000004" },
            },
        }));

        // Un slot 2 sans slot 1 est refusé (il faut d'abord le premier numéro).
        await Assert.ThrowsAsync<BusinessException>(() => clients.SaveAsync(new Client
        {
            Name = "Y",
            Chips = { new ClientChip { OperatorId = djezzy.Id, Slot = 2, PhoneNumber = "0770000005" } },
        }));

        // Un slot invalide est refusé.
        await Assert.ThrowsAsync<BusinessException>(() => clients.SaveAsync(new Client
        {
            Name = "Z",
            Chips = { new ClientChip { OperatorId = djezzy.Id, Slot = 3, PhoneNumber = "0770000006" } },
        }));
    }

    [Fact]
    public async Task New_products_get_a_default_color_and_a_valid_custom_color_is_kept()
    {
        using var db = await TestDb.CreateAsync();
        var products = new ProductService(db);

        var p1 = await products.SaveAsync(new Product { Name = "Nouveau 1", Kind = ProductKind.Physical });
        Assert.False(string.IsNullOrWhiteSpace(p1.ColorHex));

        var p2 = await products.SaveAsync(new Product { Name = "Nouveau 2", Kind = ProductKind.Physical, ColorHex = "#123ABC" });
        Assert.Equal("#123ABC", p2.ColorHex);

        await Assert.ThrowsAsync<BusinessException>(() => products.SaveAsync(new Product { Name = "Nouveau 3", Kind = ProductKind.Physical, ColorHex = "pasunecouleur" }));
    }

    [Fact]
    public async Task Deleting_a_client_removes_its_chips()
    {
        using var db = await TestDb.CreateAsync();
        var clients = new ClientService(db);
        var djezzy = (await new SettingsService(db).GetOperatorsAsync()).First();
        var c = await clients.SaveAsync(new Client
        {
            Name = "X",
            Chips = { new ClientChip { OperatorId = djezzy.Id, PhoneNumber = "0770123456" } },
        });

        await clients.DeleteAsync(c.Id);

        Assert.Empty(await clients.ListAsync());
        await using var ctx = db.CreateDbContext();
        Assert.Empty(ctx.ClientChips);
    }

    [Fact]
    public async Task Com_port_routing_is_validated_and_unique_per_operator()
    {
        using var db = await TestDb.CreateAsync();
        var settings = new SettingsService(db);
        var ops = await settings.GetOperatorsAsync();

        await settings.SaveOperatorRoutingAsync(ops[0].Id, " com32 ", "*760*{numero}*{montant}#");
        Assert.Equal("COM32", (await settings.GetOperatorsAsync())[0].ComPort);

        await Assert.ThrowsAsync<BusinessException>(() => settings.SaveOperatorRoutingAsync(ops[1].Id, "COM32", null));
        await Assert.ThrowsAsync<BusinessException>(() => settings.SaveOperatorRoutingAsync(ops[1].Id, "port1", null));

        // Ré-enregistrer le même port pour le même opérateur reste possible
        await settings.SaveOperatorRoutingAsync(ops[0].Id, "COM32", null);
    }

    [Fact]
    public async Task Settings_round_trip_and_validation()
    {
        using var db = await TestDb.CreateAsync();
        var settings = new SettingsService(db);

        await settings.SaveAsync(new AppSettings
        {
            CompanyEmail = " contact@exemple.dz ",
            InventoryStart = new DateTime(2026, 1, 1),
            InventoryEnd = new DateTime(2026, 12, 31),
        });
        var s = await settings.GetAsync();
        Assert.Equal("contact@exemple.dz", s.CompanyEmail);
        Assert.Equal(new DateTime(2026, 12, 31), s.InventoryEnd);

        await Assert.ThrowsAsync<BusinessException>(() => settings.SaveAsync(new AppSettings { CompanyEmail = "pas-un-email" }));
        await Assert.ThrowsAsync<BusinessException>(() => settings.SaveAsync(new AppSettings
        {
            InventoryStart = new DateTime(2026, 5, 1),
            InventoryEnd = new DateTime(2026, 4, 1),
        }));
    }

    [Fact]
    public void Each_virtual_credit_has_its_fixed_operator()
    {
        Assert.Equal("Djezzy", ProductOperators.OperatorNameFor("Flexy"));
        Assert.Equal("Ooredoo", ProductOperators.OperatorNameFor("Storm"));
        Assert.Equal("Mobilis", ProductOperators.OperatorNameFor("erselli"));
        Assert.Null(ProductOperators.OperatorNameFor("Cartes Idoom"));
    }

    [Fact]
    public async Task Product_rules()
    {
        using var db = await TestDb.CreateAsync();
        var products = new ProductService(db);
        var flexy = (await products.ListAsync()).Single(p => p.Name == "Flexy");

        await Assert.ThrowsAsync<BusinessException>(() => products.SaveAsync(new Product { Name = "Flexy", Kind = ProductKind.Physical }));
        // Un crédit virtuel n'a plus besoin d'opérateur : seul son type compte.
        var nouveau = await products.SaveAsync(new Product { Name = "Nouveau", Kind = ProductKind.VirtualCredit });
        Assert.Equal(ProductKind.VirtualCredit, nouveau.Kind);

        await products.AdjustStockAsync(flexy.Id, 5_000m, "Comptage");
        Assert.Single(await products.GetMovementsAsync(flexy.Id));
        await Assert.ThrowsAsync<BusinessException>(() => products.DeleteAsync(flexy.Id));
        await Assert.ThrowsAsync<BusinessException>(() => products.SaveAsync(new Product
        {
            Id = flexy.Id, Name = "Flexy", Kind = ProductKind.Physical,
        }));

        var ticket = await products.SaveAsync(new Product { Name = "Ticket 500", Kind = ProductKind.Physical });
        await products.DeleteAsync(ticket.Id);
        Assert.DoesNotContain(await products.ListAsync(), p => p.Name == "Ticket 500");
    }
}
