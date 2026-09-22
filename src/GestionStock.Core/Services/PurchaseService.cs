using GestionStock.Core.Data;
using GestionStock.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace GestionStock.Core.Services;

public record PurchaseLineInput(int ProductId, decimal Quantity, decimal UnitCost);

public record PurchaseInput(int SupplierId, DateTime Date, IReadOnlyList<PurchaseLineInput> Lines, decimal AmountPaid);

public class PurchaseService
{
    /// <summary>Un coefficient d'achat de crédit virtuel supérieur à 2 est presque sûrement une faute de frappe (9725 au lieu de 0.9725).</summary>
    public const decimal MaxVirtualCoefficient = 2m;

    private readonly IDbContextFactory<AppDbContext> _factory;

    public PurchaseService(IDbContextFactory<AppDbContext> factory) => _factory = factory;

    public static decimal ComputeLineTotal(decimal quantity, decimal unitCost)
        => Math.Round(quantity * unitCost, 2, MidpointRounding.AwayFromZero);

    public async Task<List<PurchaseOrder>> ListAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        var rows = await db.PurchaseOrders.AsNoTracking()
            .Include(p => p.Supplier)
            .Include(p => p.Lines).ThenInclude(l => l.Product)
            .ToListAsync();
        return rows.OrderByDescending(p => p.Date).ThenByDescending(p => p.Id).ToList();
    }

    /// <summary>Crée un bon d'achat, ajoute les quantités au stock et écrit le journal, le tout dans une transaction.</summary>
    public async Task<PurchaseOrder> CreateAsync(PurchaseInput input)
    {
        if (input.Lines.Count == 0) throw new BusinessException("Ajoutez au moins un produit au bon d'achat.");

        await using var db = await _factory.CreateDbContextAsync();
        if (!await db.Suppliers.AnyAsync(s => s.Id == input.SupplierId))
            throw new BusinessException("Sélectionnez un fournisseur.");

        var productIds = input.Lines.Select(l => l.ProductId).Distinct().ToList();
        var products = await db.Products.Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id);

        var order = new PurchaseOrder { Date = input.Date, SupplierId = input.SupplierId };
        var movements = new List<(StockMovement Movement, PurchaseLine Line)>();

        foreach (var line in input.Lines)
        {
            if (!products.TryGetValue(line.ProductId, out var product))
                throw new BusinessException("Un des produits du bon n'existe plus.");
            if (!product.IsActive)
                throw new BusinessException($"Le produit « {product.Name} » est désactivé.");
            if (line.Quantity <= 0)
                throw new BusinessException($"La quantité de « {product.Name} » doit être positive.");
            if (line.UnitCost <= 0)
                throw new BusinessException($"Le prix ou coefficient de « {product.Name} » doit être positif.");
            if (product.Kind == ProductKind.Physical && line.Quantity != decimal.Truncate(line.Quantity))
                throw new BusinessException($"La quantité de « {product.Name} » doit être un nombre entier.");
            if (product.Kind == ProductKind.VirtualCredit && line.UnitCost > MaxVirtualCoefficient)
                throw new BusinessException(
                    $"Le coefficient de « {product.Name} » ({line.UnitCost}) est trop élevé : saisissez par exemple 0.9725.");

            var purchaseLine = new PurchaseLine
            {
                ProductId = product.Id,
                Quantity = line.Quantity,
                UnitCost = line.UnitCost,
                LineTotal = ComputeLineTotal(line.Quantity, line.UnitCost),
            };
            order.Lines.Add(purchaseLine);
            product.StockBalance += line.Quantity;
            movements.Add((new StockMovement
            {
                Date = input.Date,
                ProductId = product.Id,
                Quantity = line.Quantity,
                Kind = StockMovementKind.Purchase,
            }, purchaseLine));
        }

        order.Total = order.Lines.Sum(l => l.LineTotal);
        if (input.AmountPaid < 0 || input.AmountPaid > order.Total)
            throw new BusinessException("Le montant payé doit être compris entre 0 et le total du bon.");
        order.AmountPaid = input.AmountPaid;

        await using var tx = await db.Database.BeginTransactionAsync();
        order.Number = $"TMP-{Guid.NewGuid():N}";
        db.PurchaseOrders.Add(order);
        await db.SaveChangesAsync();

        order.Number = $"BA-{order.Id:D6}";
        foreach (var (movement, _) in movements)
        {
            movement.PurchaseOrderId = order.Id;
            movement.Note = order.Number;
            db.StockMovements.Add(movement);
        }
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return order;
    }
}
