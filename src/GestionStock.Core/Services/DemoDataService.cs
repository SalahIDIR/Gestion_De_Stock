using GestionStock.Core.Data;
using GestionStock.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace GestionStock.Core.Services;

public record DemoDataResult(int Clients, int Suppliers, int Purchases, int Deliveries, int Encashments);

/// <summary>
/// Remplit la base avec des données de démonstration (clients, fournisseurs, bons d'achat, bons de livraison,
/// encaissements). Tout passe par les services métier : stock, journal, dettes et numéros restent cohérents.
/// </summary>
public class DemoDataService
{
    /// <summary>Préfixe des références fournisseurs de démo, sert aussi à savoir si la démo a déjà été générée.</summary>
    public const string SupplierReferencePrefix = "DEMO-F";

    private static readonly string[] FirstNames =
    {
        "Mohamed", "Ahmed", "Yacine", "Karim", "Amine", "Sofiane", "Bilal", "Walid", "Nassim", "Riad", "Hichem", "Samir",
        "Farid", "Mourad", "Djamel", "Rachid", "Abdelkader", "Ilyes", "Mehdi", "Anis", "Zakaria", "Oussama", "Hamza", "Khaled",
        "Fatima", "Amina", "Sara", "Nadia", "Meriem", "Yasmine", "Lina", "Khadidja", "Imane", "Souad", "Nour", "Rania",
    };

    private static readonly string[] LastNames =
    {
        "Benali", "Bouzid", "Haddad", "Mansouri", "Brahimi", "Belkacem", "Saidi", "Khelifi", "Cherif", "Boudiaf", "Meziane",
        "Hamidi", "Rahmani", "Djebbar", "Ziani", "Amrani", "Guendouz", "Lahlou", "Bensalem", "Taleb", "Ouali", "Kaci",
        "Hadjadj", "Belaid", "Ferhat", "Mebarki", "Slimani", "Zerrouki", "Boukhari", "Aissaoui", "Chaouch", "Derradji",
    };

    private static readonly string[] ShopKinds =
    {
        "Taxiphone", "Alimentation générale", "Superette", "Kiosque", "Librairie", "Cyber", "Boutique mobile", "Tabac-journaux",
    };

    private static readonly string[] Cities =
    {
        "Alger", "Oran", "Constantine", "Blida", "Sétif", "Annaba", "Batna", "Tizi Ouzou", "Béjaïa", "Tlemcen", "Boumerdès",
        "Médéa", "Chlef", "Mostaganem", "Bouira", "Jijel", "Skikda", "Biskra", "Djelfa", "Tipaza",
    };

    private static readonly string[] Streets =
    {
        "Rue Didouche Mourad", "Cité 500 logements", "Rue Larbi Ben M'hidi", "Boulevard de l'ALN", "Cité des Martyrs",
        "Rue Hassiba Ben Bouali", "Cité El Badr", "Rue Emir Abdelkader", "Quartier Belle-Vue", "Cité 1er Novembre",
    };

    /// <summary>Préfixe mobile de chaque opérateur de départ.</summary>
    private static readonly Dictionary<string, string> OperatorPrefixes = new()
    {
        ["Djezzy"] = "07", ["Ooredoo"] = "05", ["Mobilis"] = "06",
    };

    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly ClientService _clients;
    private readonly SupplierService _suppliers;
    private readonly PurchaseService _purchases;
    private readonly DeliveryService _deliveries;

    public DemoDataService(IDbContextFactory<AppDbContext> factory, ClientService clients, SupplierService suppliers,
        PurchaseService purchases, DeliveryService deliveries)
    {
        _factory = factory;
        _clients = clients;
        _suppliers = suppliers;
        _purchases = purchases;
        _deliveries = deliveries;
    }

    public async Task<bool> IsAlreadyGeneratedAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.Suppliers.AnyAsync(s => s.Reference.StartsWith(SupplierReferencePrefix));
    }

    public async Task<DemoDataResult> GenerateAsync(int clientCount = 300, int days = 90)
    {
        if (await IsAlreadyGeneratedAsync())
            throw new BusinessException("Les données de démonstration ont déjà été générées.");

        var rng = new Random(2026);
        List<Operator> operators;
        List<Product> products;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            operators = await db.Operators.AsNoTracking().ToListAsync();
            products = await db.Products.AsNoTracking().Where(p => p.IsActive).ToListAsync();
        }
        if (products.Count == 0) throw new BusinessException("Aucun produit actif : impossible de générer des opérations.");

        // Fournisseurs
        var suppliers = new List<Supplier>();
        string[] supplierNames = { "Sarl Djazair Telecom", "Eurl Flexy Distribution", "Sarl Mobil Services Est",
            "Eurl Atlas Recharge", "Sarl Sahel Communication" };
        for (var i = 0; i < supplierNames.Length; i++)
        {
            suppliers.Add(await _suppliers.SaveAsync(new Supplier
            {
                Reference = $"{SupplierReferencePrefix}{i + 1:D2}",
                CompanyName = supplierNames[i],
                Address = $"{rng.Next(1, 120)} {Pick(rng, Streets)}, {Pick(rng, Cities)}",
                Phone1 = RandomPhone(rng, Pick(rng, OperatorPrefixes.Values.ToArray())),
                Phone2 = rng.Next(2) == 0 ? $"0{rng.Next(21, 49)}{rng.Next(100000, 999999)}" : null,
            }));
        }

        // Clients, avec 1 à 3 puces chacun
        var clients = new List<Client>();
        var usedNames = new HashSet<string>();
        while (clients.Count < clientCount)
        {
            var person = $"{Pick(rng, FirstNames)} {Pick(rng, LastNames)}";
            var name = rng.Next(3) == 0 ? $"{Pick(rng, ShopKinds)} {person}" : person;
            if (!usedNames.Add(name)) continue;

            var client = new Client
            {
                Name = name,
                City = Pick(rng, Cities),
                Address = $"{rng.Next(1, 200)} {Pick(rng, Streets)}",
                Phone = RandomPhone(rng, Pick(rng, OperatorPrefixes.Values.ToArray())),
                CreditLimit = rng.Next(5) == 0 ? rng.Next(5, 31) * 10_000m : 0m,
            };
            foreach (var op in operators.OrderBy(_ => rng.Next()).Take(rng.Next(1, operators.Count + 1)))
            {
                var prefix = OperatorPrefixes.GetValueOrDefault(op.Name, "07");
                client.Chips.Add(new ClientChip { OperatorId = op.Id, PhoneNumber = RandomPhone(rng, prefix), Slot = 1 });
                if (rng.Next(6) == 0)
                    client.Chips.Add(new ClientChip { OperatorId = op.Id, PhoneNumber = RandomPhone(rng, prefix), Slot = 2 });
            }
            clients.Add(await _clients.SaveAsync(client));
        }

        // Opérations, jour par jour, du plus ancien au plus récent
        var stock = products.ToDictionary(p => p.Id, p => p.StockBalance);
        var debts = clients.ToDictionary(c => c.Id, _ => 0m);
        int purchaseCount = 0, deliveryCount = 0, encashmentCount = 0;
        var start = DateTime.Today.AddDays(-days);

        async Task RestockAsync(DateTime date, Product? only = null)
        {
            var supplier = Pick(rng, suppliers);
            var lines = (only != null ? new List<Product> { only } : products.Where(_ => rng.Next(3) > 0).DefaultIfEmpty(products[0]).ToList())
                .Select(p => p.Kind == ProductKind.VirtualCredit
                    ? new PurchaseLineInput(p.Id, rng.Next(20, 81) * 25_000m, 0.9700m + rng.Next(0, 6) * 0.0005m)
                    : new PurchaseLineInput(p.Id, rng.Next(20, 61) * 10m, 470m + rng.Next(0, 3) * 5m))
                .ToList();
            var total = lines.Sum(l => PurchaseService.ComputeLineTotal(l.Quantity, l.UnitCost));
            var paid = rng.Next(4) switch { 0 => 0m, 1 => RoundHundreds(total * rng.Next(3, 8) / 10m), _ => total };
            await _purchases.CreateAsync(new PurchaseInput(supplier.Id, date.AddHours(8).AddMinutes(rng.Next(0, 60)), lines, paid));
            foreach (var l in lines) stock[l.ProductId] += l.Quantity;
            purchaseCount++;
        }

        await RestockAsync(start);
        for (var day = 0; day <= days; day++)
        {
            var date = start.AddDays(day);
            if (day > 0 && rng.Next(7) == 0) await RestockAsync(date);

            var opsToday = date.DayOfWeek == DayOfWeek.Friday ? rng.Next(0, 3) : rng.Next(3, 9);
            for (var n = 0; n < opsToday; n++)
            {
                var time = date.AddHours(rng.Next(9, 20)).AddMinutes(rng.Next(0, 60));
                var client = clients[(int)Math.Floor(Math.Pow(rng.NextDouble(), 1.6) * clients.Count)];

                // Encaissement d'une partie de la dette
                if (debts[client.Id] > 1000 && rng.Next(5) == 0)
                {
                    var amount = Math.Max(500m, RoundHundreds(debts[client.Id] * rng.Next(3, 11) / 10m));
                    amount = Math.Min(amount, debts[client.Id]);
                    await _deliveries.CreateAsync(new DeliveryInput(client.Id, time, Array.Empty<DeliveryLineInput>(), amount));
                    debts[client.Id] -= amount;
                    encashmentCount++;
                    continue;
                }

                var lines = new List<DeliveryLineInput>();
                foreach (var product in products.OrderBy(_ => rng.Next()).Take(rng.Next(3) == 0 ? 2 : 1))
                {
                    decimal qty, price;
                    string? phone = null;
                    if (product.Kind == ProductKind.VirtualCredit)
                    {
                        qty = rng.Next(2, 81) * 500m;
                        price = 0.9800m + rng.Next(0, 5) * 0.0025m;
                        phone = client.Chips.Count == 0 ? null : client.Chips[rng.Next(client.Chips.Count)].PhoneNumber;
                    }
                    else
                    {
                        qty = rng.Next(1, 21) * 5m;
                        price = 500m;
                    }
                    if (stock[product.Id] < qty) await RestockAsync(time.Date, product);
                    lines.Add(new DeliveryLineInput(product.Id, qty, price, phone));
                }

                var total = lines.Sum(l => DeliveryService.ComputeLineTotal(l.Quantity, l.UnitPrice));
                var paid = rng.Next(10) switch
                {
                    < 5 => total,
                    < 8 => Math.Min(total, RoundHundreds(total * rng.Next(2, 9) / 10m)),
                    _ => 0m,
                };
                if (client.CreditLimit > 0 && debts[client.Id] + total - paid > client.CreditLimit) paid = total;

                await _deliveries.CreateAsync(new DeliveryInput(client.Id, time, lines, paid));
                foreach (var l in lines) stock[l.ProductId] -= l.Quantity;
                debts[client.Id] += total - paid;
                deliveryCount++;
            }
        }

        return new DemoDataResult(clients.Count, suppliers.Count, purchaseCount, deliveryCount, encashmentCount);
    }

    /// <summary>Arrondi à la centaine de DA (Math.Round sur decimal n'accepte pas de décimales négatives).</summary>
    private static decimal RoundHundreds(decimal value) => Math.Round(value / 100m, MidpointRounding.AwayFromZero) * 100m;

    private static T Pick<T>(Random rng, IReadOnlyList<T> items) => items[rng.Next(items.Count)];

    private static string RandomPhone(Random rng, string prefix) => prefix + rng.Next(10_000_000, 100_000_000);
}
