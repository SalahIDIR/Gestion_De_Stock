using GestionStock.Core.Data;
using GestionStock.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace GestionStock.Core.Services;

public record DeliveryLineInput(int ProductId, decimal Quantity, decimal UnitPrice);

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

    /// <summary>Dette actuelle d'un client : reste à payer de ses bons, moins les paiements encaissés.</summary>
    public async Task<decimal> GetClientDebtAsync(int clientId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await DebtOfAsync(db, clientId);
    }

    /// <summary>Dette de chaque client ayant des bons ou paiements, clé = ClientId.</summary>
    public async Task<Dictionary<int, decimal>> GetDebtsAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        var notes = await db.DeliveryNotes.AsNoTracking()
            .Select(n => new { n.ClientId, n.Total, n.AmountPaid }).ToListAsync();
        var payments = await db.ClientPayments.AsNoTracking()
            .Select(p => new { p.ClientId, p.Amount }).ToListAsync();

        var debts = notes.GroupBy(n => n.ClientId).ToDictionary(g => g.Key, g => g.Sum(n => n.Total - n.AmountPaid));
        foreach (var p in payments)
            debts[p.ClientId] = debts.GetValueOrDefault(p.ClientId) - p.Amount;
        return debts;
    }

    /// <summary>Dernier coefficient ou prix de vente appliqué à ce client pour chaque produit, clé = ProductId.</summary>
    public async Task<Dictionary<int, decimal>> GetLastPricesAsync(int clientId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var rows = await db.DeliveryLines.AsNoTracking()
            .Where(l => db.DeliveryNotes.Any(n => n.Id == l.DeliveryNoteId && n.ClientId == clientId))
            .Select(l => new { l.ProductId, l.UnitPrice, l.DeliveryNoteId })
            .ToListAsync();
        return rows.GroupBy(r => r.ProductId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.DeliveryNoteId).First().UnitPrice);
    }

    /// <summary>
    /// Crée un bon de livraison : vérifie le stock et le plafond du client, retire les quantités du stock
    /// et écrit le journal, le tout dans une transaction.
    /// </summary>
    public async Task<DeliveryNote> CreateAsync(DeliveryInput input)
    {
        if (input.Lines.Count == 0) throw new BusinessException("Ajoutez au moins un produit au bon de livraison.");

        await using var db = await _factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        var client = await db.Clients.FindAsync(input.ClientId) ?? throw new BusinessException("Sélectionnez un client.");

        var productIds = input.Lines.Select(l => l.ProductId).Distinct().ToList();
        var products = await db.Products.Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id);

        var note = new DeliveryNote { Date = input.Date, ClientId = client.Id };

        foreach (var line in input.Lines)
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

            note.Lines.Add(new DeliveryLine
            {
                ProductId = product.Id,
                Quantity = line.Quantity,
                UnitPrice = line.UnitPrice,
                LineTotal = ComputeLineTotal(line.Quantity, line.UnitPrice),
            });
        }

        // Le stock est vérifié par produit, toutes lignes confondues.
        foreach (var group in note.Lines.GroupBy(l => l.ProductId))
        {
            var product = products[group.Key];
            var requested = group.Sum(l => l.Quantity);
            if (requested > product.StockBalance)
                throw new BusinessException(
                    $"Stock insuffisant pour « {product.Name} » : disponible {product.StockBalance:N2}, demandé {requested:N2}.");
        }

        note.Total = note.Lines.Sum(l => l.LineTotal);
        if (input.AmountPaid < 0 || input.AmountPaid > note.Total)
            throw new BusinessException("Le montant encaissé doit être compris entre 0 et le total du bon.");
        note.AmountPaid = input.AmountPaid;

        if (client.CreditLimit > 0)
        {
            var debtAfter = await DebtOfAsync(db, client.Id) + note.Remaining;
            if (debtAfter > client.CreditLimit)
                throw new BusinessException(
                    $"Plafond de crédit dépassé pour « {client.Name} » : la dette serait de {debtAfter:N2} DA " +
                    $"pour un plafond de {client.CreditLimit:N2} DA. Encaissez un paiement ou réduisez le bon.");
        }

        note.Number = $"TMP-{Guid.NewGuid():N}";
        db.DeliveryNotes.Add(note);
        await db.SaveChangesAsync();

        note.Number = $"BL-{note.Id:D6}";
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
        }
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return note;
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
        var notes = await db.DeliveryNotes.AsNoTracking().Where(n => n.ClientId == clientId)
            .Select(n => new { n.Total, n.AmountPaid }).ToListAsync();
        var payments = await db.ClientPayments.AsNoTracking().Where(p => p.ClientId == clientId)
            .Select(p => p.Amount).ToListAsync();
        return notes.Sum(n => n.Total - n.AmountPaid) - payments.Sum();
    }
}
