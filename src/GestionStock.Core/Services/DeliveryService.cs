using GestionStock.Core.Data;
using GestionStock.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace GestionStock.Core.Services;

public record DeliveryLineInput(int ProductId, decimal Quantity, decimal UnitPrice, string? RecipientPhone = null);

public record DeliveryInput(int ClientId, DateTime Date, IReadOnlyList<DeliveryLineInput> Lines, decimal AmountPaid);

public class DeliveryService
{
    /// <summary>Un coefficient de vente de crédit virtuel supérieur à 2 est presque sûrement une faute de frappe.</summary>
    public const decimal MaxVirtualCoefficient = 2m;

    private readonly IDbContextFactory<AppDbContext> _factory;

    public DeliveryService(IDbContextFactory<AppDbContext> factory) => _factory = factory;

    public static decimal ComputeLineTotal(decimal quantity, decimal unitPrice)
        => Math.Round(quantity * unitPrice, 2, MidpointRounding.AwayFromZero);

    public async Task<List<DeliveryNote>> ListAsync(int take = 500)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var rows = await db.DeliveryNotes.AsNoTracking()
            .Include(d => d.Client)
            .Include(d => d.Lines).ThenInclude(l => l.Product)
            .OrderByDescending(d => d.Id)
            .Take(take)
            .ToListAsync();
        return rows.OrderByDescending(d => d.Date).ThenByDescending(d => d.Id).ToList();
    }

    /// <summary>Un bon avec son client et ses produits (pour l'impression), ou null s'il n'existe plus.</summary>
    public async Task<DeliveryNote?> GetAsync(int noteId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.DeliveryNotes.AsNoTracking()
            .Include(d => d.Client)
            .Include(d => d.Lines).ThenInclude(l => l.Product)
            .FirstOrDefaultAsync(d => d.Id == noteId);
    }

    /// <summary>Dette actuelle d'un client : reste à payer de ses bons, moins les paiements encaissés.</summary>
    public async Task<decimal> GetClientDebtAsync(int clientId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await DebtOfAsync(db, clientId);
    }

    /// <summary>Dette de chaque client (avec son solde initial), clé = ClientId.</summary>
    public async Task<Dictionary<int, decimal>> GetDebtsAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        var openingBalances = await db.Clients.AsNoTracking().Select(c => new { c.Id, c.OpeningBalance }).ToListAsync();
        var notes = await db.DeliveryNotes.AsNoTracking()
            .Select(n => new { n.ClientId, n.Total, n.AmountPaid }).ToListAsync();
        var payments = await db.ClientPayments.AsNoTracking()
            .Select(p => new { p.ClientId, p.Amount }).ToListAsync();

        var debts = openingBalances.ToDictionary(c => c.Id, c => c.OpeningBalance);
        foreach (var group in notes.GroupBy(n => n.ClientId))
            debts[group.Key] = debts.GetValueOrDefault(group.Key) + group.Sum(n => n.Total - n.AmountPaid);
        foreach (var p in payments)
            debts[p.ClientId] = debts.GetValueOrDefault(p.ClientId) - p.Amount;
        return debts;
    }

    /// <summary>
    /// Tarif de vente courant de chaque produit pour ce client. Fixé automatiquement lors du premier bon,
    /// modifiable ensuite ; la modification ne s'applique qu'aux futurs bons, les bons déjà émis gardent leur propre tarif.
    /// </summary>
    public async Task<Dictionary<int, decimal>> GetLastPricesAsync(int clientId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.ClientProductRates.AsNoTracking().Where(r => r.ClientId == clientId)
            .ToDictionaryAsync(r => r.ProductId, r => r.Rate);
    }

    /// <summary>
    /// Date de référence utilisée pour le retard de paiement de chaque client : la date de son dernier encaissement
    /// (bon payé en tout ou partie, bon d'encaissement, ou paiement enregistré séparément), ou à défaut la date du
    /// plus ancien bon impayé (le retard se compte alors depuis la naissance de la dette).
    /// </summary>
    public async Task<Dictionary<int, DateTime>> GetLastActivityDatesAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        var notes = await db.DeliveryNotes.AsNoTracking()
            .Select(n => new { n.ClientId, n.Date, n.AmountPaid }).ToListAsync();
        var payments = await db.ClientPayments.AsNoTracking()
            .Select(p => new { p.ClientId, p.Date }).ToListAsync();

        var result = new Dictionary<int, DateTime>();
        foreach (var group in notes.GroupBy(n => n.ClientId))
        {
            DateTime? lastPayment = group.Where(n => n.AmountPaid > 0).Select(n => (DateTime?)n.Date).Max();
            var lastSeparatePayment = payments.Where(p => p.ClientId == group.Key).Select(p => (DateTime?)p.Date).Max();
            if (lastSeparatePayment > lastPayment || lastPayment == null) lastPayment = lastSeparatePayment;
            result[group.Key] = lastPayment ?? group.Min(n => n.Date);
        }
        return result;
    }

    /// <summary>
    /// Crée un bon de livraison : vérifie le stock et le plafond du client, retire les quantités du stock
    /// et écrit le journal, le tout dans une transaction. Un bon sans aucune ligne est un « bon d'encaissement » :
    /// il ne contient que le montant encaissé et réduit la dette du client d'autant, sans toucher au stock.
    /// </summary>
    public async Task<DeliveryNote> CreateAsync(DeliveryInput input)
    {
        var isPaymentOnly = input.Lines.Count == 0;

        await using var db = await _factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        var client = await db.Clients.FindAsync(input.ClientId) ?? throw new BusinessException("Sélectionnez un client.");
        var currentDebt = await DebtOfAsync(db, client.Id);
        var note = new DeliveryNote { Date = input.Date, ClientId = client.Id };
        var products = new Dictionary<int, Product>();

        if (isPaymentOnly)
        {
            // Le montant peut dépasser la dette : le solde du client devient négatif (avoir en sa faveur).
            if (input.AmountPaid <= 0)
                throw new BusinessException("Un bon sans produit doit avoir un montant encaissé positif.");
            note.Total = 0;
            note.AmountPaid = input.AmountPaid;
        }
        else
        {
            var productIds = input.Lines.Select(l => l.ProductId).Distinct().ToList();
            products = await db.Products.Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id);

            note.Lines.AddRange(BuildLines(input.Lines, products));
            CheckStock(note.Lines, products, p => p.StockBalance);

            note.Total = note.Lines.Sum(l => l.LineTotal);
            // L'encaissement peut dépasser le total du bon : le surplus réduit la dette, qui peut devenir négative (avoir).
            if (input.AmountPaid < 0)
                throw new BusinessException("Le montant encaissé ne peut pas être négatif.");
            note.AmountPaid = input.AmountPaid;

            // Le plafond ne bloque que si ce bon augmente la dette (un encaissement supérieur au total la réduit).
            if (client.CreditLimit > 0 && note.Remaining > 0)
            {
                var debtAfter = currentDebt + note.Remaining;
                if (debtAfter > client.CreditLimit)
                    throw new BusinessException(
                        $"Plafond de crédit dépassé pour « {client.Name} » : la dette serait de {debtAfter:N2} DA " +
                        $"pour un plafond de {client.CreditLimit:N2} DA. Encaissez un paiement ou réduisez le bon.");
            }
        }

        note.Number = $"TMP-{Guid.NewGuid():N}";
        db.DeliveryNotes.Add(note);
        await db.SaveChangesAsync();

        note.Number = (isPaymentOnly ? "ENC-" : "BL-") + $"{note.Id:D6}";
        if (!isPaymentOnly)
        {
            foreach (var line in note.Lines)
            {
                products[line.ProductId].StockBalance -= line.Quantity;
                db.StockMovements.Add(new StockMovement
                {
                    Date = input.Date,
                    ProductId = line.ProductId,
                    Quantity = -line.Quantity,
                    Kind = StockMovementKind.Sale,
                    DeliveryNoteId = note.Id,
                    Note = note.Number,
                });
                await UpsertRateAsync(db, client.Id, line.ProductId, line.UnitPrice);
            }
        }
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return note;
    }

    /// <summary>
    /// Modifie un bon existant (client, lignes, montant encaissé) en gardant son numéro et sa date.
    /// Le stock des anciennes lignes est rendu puis celui des nouvelles est retiré ; le journal garde la trace des deux.
    /// Un bon d'encaissement reste un bon d'encaissement, un bon de livraison reste un bon de livraison.
    /// </summary>
    public async Task<DeliveryNote> UpdateAsync(int noteId, DeliveryInput input)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        var note = await db.DeliveryNotes.Include(n => n.Lines).FirstOrDefaultAsync(n => n.Id == noteId)
                   ?? throw new BusinessException("Ce bon n'existe plus.");
        var client = await db.Clients.FindAsync(input.ClientId) ?? throw new BusinessException("Sélectionnez un client.");
        var wasPaymentOnly = note.Lines.Count == 0;
        var isPaymentOnly = input.Lines.Count == 0;
        if (wasPaymentOnly && !isPaymentOnly)
            throw new BusinessException("Un bon d'encaissement ne peut pas recevoir de produits : créez plutôt un nouveau bon de livraison.");
        if (!wasPaymentOnly && isPaymentOnly)
            throw new BusinessException("Un bon de livraison doit garder au moins un produit. Pour l'annuler, supprimez-le.");

        var sameClient = note.ClientId == client.Id;
        var debtBefore = await DebtOfAsync(db, client.Id);
        // Dette du client comme si ce bon n'existait pas.
        var debtWithoutNote = debtBefore - (sameClient ? note.Remaining : 0m);

        var productIds = note.Lines.Select(l => l.ProductId).Concat(input.Lines.Select(l => l.ProductId)).Distinct().ToList();
        var products = await db.Products.Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id);

        var newLines = new List<DeliveryLine>();
        decimal total;
        if (isPaymentOnly)
        {
            if (input.AmountPaid <= 0)
                throw new BusinessException("Un bon sans produit doit avoir un montant encaissé positif.");
            total = 0m;
        }
        else
        {
            newLines = BuildLines(input.Lines, products);
            // Le stock disponible compte ce que ce bon avait déjà pris, puisqu'il va être rendu.
            var released = note.Lines.GroupBy(l => l.ProductId).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));
            CheckStock(newLines, products, p => p.StockBalance + released.GetValueOrDefault(p.Id));

            total = newLines.Sum(l => l.LineTotal);
            if (input.AmountPaid < 0)
                throw new BusinessException("Le montant encaissé ne peut pas être négatif.");

            // La dette peut devenir négative (avoir en faveur du client) ; le plafond ne bloque que si la modification l'augmente.
            var debtAfter = debtWithoutNote + total - input.AmountPaid;
            if (client.CreditLimit > 0 && debtAfter > client.CreditLimit && debtAfter > debtBefore)
                throw new BusinessException(
                    $"Plafond de crédit dépassé pour « {client.Name} » : la dette serait de {debtAfter:N2} DA " +
                    $"pour un plafond de {client.CreditLimit:N2} DA.");
        }

        var now = DateTime.Now;
        foreach (var old in note.Lines)
        {
            products[old.ProductId].StockBalance += old.Quantity;
            db.StockMovements.Add(new StockMovement
            {
                Date = now, ProductId = old.ProductId, Quantity = old.Quantity, Kind = StockMovementKind.Adjustment,
                DeliveryNoteId = note.Id, Note = $"Modification {note.Number} : annulation de l'ancienne ligne",
            });
        }
        db.DeliveryLines.RemoveRange(note.Lines);
        note.Lines.Clear();

        foreach (var line in newLines)
        {
            products[line.ProductId].StockBalance -= line.Quantity;
            note.Lines.Add(line);
            db.StockMovements.Add(new StockMovement
            {
                Date = now, ProductId = line.ProductId, Quantity = -line.Quantity, Kind = StockMovementKind.Sale,
                DeliveryNoteId = note.Id, Note = $"{note.Number} (modifié)",
            });
        }

        note.ClientId = client.Id;
        note.Total = total;
        note.AmountPaid = input.AmountPaid;
        await db.SaveChangesAsync();

        // Si ce bon est le plus récent du client pour un produit, son coefficient devient le tarif proposé aux prochains bons.
        foreach (var line in newLines.GroupBy(l => l.ProductId).Select(g => g.Last()))
        {
            var hasNewer = await db.DeliveryLines.AnyAsync(l => l.ProductId == line.ProductId && db.DeliveryNotes.Any(n =>
                n.Id == l.DeliveryNoteId && n.ClientId == client.Id && (n.Date > note.Date || (n.Date == note.Date && n.Id > note.Id))));
            if (!hasNewer) await UpsertRateAsync(db, client.Id, line.ProductId, line.UnitPrice);
        }
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return note;
    }

    /// <summary>
    /// Numéro de puce utilisé sur le bon le plus récent de ce client, pour chaque produit (clé = ProductId),
    /// afin de le proposer par défaut dans le prochain bon.
    /// </summary>
    public async Task<Dictionary<int, string>> GetLastRecipientsAsync(int clientId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var lines = await db.DeliveryLines.AsNoTracking()
            .Where(l => l.RecipientPhone != null)
            .Join(db.DeliveryNotes.Where(n => n.ClientId == clientId), l => l.DeliveryNoteId, n => n.Id,
                (l, n) => new { l.ProductId, l.RecipientPhone, n.Date, NoteId = n.Id })
            .ToListAsync();
        return lines.GroupBy(l => l.ProductId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(l => l.Date).ThenByDescending(l => l.NoteId).First().RecipientPhone!);
    }

    /// <summary>Supprime un bon : le stock de ses lignes est rendu (tracé dans le journal) et la dette du client corrigée.</summary>
    public async Task DeleteAsync(int noteId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        // La dette du client peut devenir négative (avoir) si des encaissements avaient déjà réglé ce bon.
        var note = await db.DeliveryNotes.Include(n => n.Lines).FirstOrDefaultAsync(n => n.Id == noteId);
        if (note == null) return;

        var productIds = note.Lines.Select(l => l.ProductId).Distinct().ToList();
        var products = await db.Products.Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id);
        var now = DateTime.Now;
        foreach (var line in note.Lines)
        {
            products[line.ProductId].StockBalance += line.Quantity;
            db.StockMovements.Add(new StockMovement
            {
                Date = now, ProductId = line.ProductId, Quantity = line.Quantity, Kind = StockMovementKind.Adjustment,
                Note = $"Suppression {note.Number}",
            });
        }

        db.DeliveryNotes.Remove(note);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    /// <summary>Valide les lignes saisies et construit les lignes du bon (sans toucher au stock).</summary>
    private static List<DeliveryLine> BuildLines(IReadOnlyList<DeliveryLineInput> inputs, Dictionary<int, Product> products)
    {
        var lines = new List<DeliveryLine>();
        foreach (var line in inputs)
        {
            if (!products.TryGetValue(line.ProductId, out var product))
                throw new BusinessException("Un des produits du bon n'existe plus.");
            if (!product.IsActive)
                throw new BusinessException($"Le produit « {product.Name} » est désactivé.");
            if (line.Quantity <= 0)
                throw new BusinessException($"La quantité de « {product.Name} » doit être positive.");
            if (line.UnitPrice <= 0)
                throw new BusinessException($"Le prix ou coefficient de « {product.Name} » doit être positif.");
            if (product.Kind == ProductKind.Physical && line.Quantity != decimal.Truncate(line.Quantity))
                throw new BusinessException($"La quantité de « {product.Name} » doit être un nombre entier.");
            if (product.Kind == ProductKind.VirtualCredit && line.UnitPrice > MaxVirtualCoefficient)
                throw new BusinessException(
                    $"Le coefficient de « {product.Name} » ({line.UnitPrice}) est trop élevé : saisissez par exemple 0.98.");

            lines.Add(new DeliveryLine
            {
                ProductId = product.Id,
                Quantity = line.Quantity,
                UnitPrice = line.UnitPrice,
                LineTotal = ComputeLineTotal(line.Quantity, line.UnitPrice),
                RecipientPhone = string.IsNullOrWhiteSpace(line.RecipientPhone) ? null : line.RecipientPhone.Trim(),
            });
        }
        return lines;
    }

    /// <summary>Vérifie le stock par produit, toutes lignes confondues.</summary>
    private static void CheckStock(IEnumerable<DeliveryLine> lines, Dictionary<int, Product> products, Func<Product, decimal> available)
    {
        foreach (var group in lines.GroupBy(l => l.ProductId))
        {
            var product = products[group.Key];
            var requested = group.Sum(l => l.Quantity);
            var stock = available(product);
            if (requested > stock)
                throw new BusinessException(
                    $"Stock insuffisant pour « {product.Name} » : disponible {stock:N2}, demandé {requested:N2}.");
        }
    }

    private static async Task UpsertRateAsync(AppDbContext db, int clientId, int productId, decimal rate)
    {
        // On regarde d'abord les tarifs déjà ajoutés dans cette transaction : un bon peut contenir plusieurs lignes du même
        // produit, et le tarif de la ligne précédente n'est pas encore enregistré en base.
        var existing = db.ClientProductRates.Local.FirstOrDefault(r => r.ClientId == clientId && r.ProductId == productId)
                       ?? await db.ClientProductRates.FirstOrDefaultAsync(r => r.ClientId == clientId && r.ProductId == productId);
        if (existing == null) db.ClientProductRates.Add(new ClientProductRate { ClientId = clientId, ProductId = productId, Rate = rate });
        else existing.Rate = rate;
    }

    public async Task<ClientPayment> AddPaymentAsync(int clientId, DateTime date, decimal amount, string? note)
    {
        if (amount <= 0) throw new BusinessException("Le montant du paiement doit être positif.");

        await using var db = await _factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        if (!await db.Clients.AnyAsync(c => c.Id == clientId)) throw new BusinessException("Client introuvable.");
        var debt = await DebtOfAsync(db, clientId);
        if (amount > debt)
            throw new BusinessException($"Le paiement ({amount:N2} DA) dépasse la dette du client ({debt:N2} DA).");

        var payment = new ClientPayment
        {
            ClientId = clientId,
            Date = date,
            Amount = amount,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
        };
        db.ClientPayments.Add(payment);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return payment;
    }

    public async Task<List<ClientPayment>> ListPaymentsAsync(int clientId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var rows = await db.ClientPayments.AsNoTracking().Where(p => p.ClientId == clientId).ToListAsync();
        return rows.OrderByDescending(p => p.Date).ThenByDescending(p => p.Id).ToList();
    }

    private static async Task<decimal> DebtOfAsync(AppDbContext db, int clientId)
    {
        var openingBalance = await db.Clients.AsNoTracking().Where(c => c.Id == clientId)
            .Select(c => c.OpeningBalance).FirstOrDefaultAsync();
        var notes = await db.DeliveryNotes.AsNoTracking().Where(n => n.ClientId == clientId)
            .Select(n => new { n.Total, n.AmountPaid }).ToListAsync();
        var payments = await db.ClientPayments.AsNoTracking().Where(p => p.ClientId == clientId)
            .Select(p => p.Amount).ToListAsync();
        return openingBalance + notes.Sum(n => n.Total - n.AmountPaid) - payments.Sum();
    }
}
