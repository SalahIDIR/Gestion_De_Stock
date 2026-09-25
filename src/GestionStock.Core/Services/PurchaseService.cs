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

    /// <summary>Un bon d'achat avec son fournisseur et ses produits (pour l'impression), ou null s'il n'existe plus.</summary>
    public async Task<PurchaseOrder?> GetAsync(int orderId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.PurchaseOrders.AsNoTracking()
            .Include(p => p.Supplier)
            .Include(p => p.Lines).ThenInclude(l => l.Product)
            .FirstOrDefaultAsync(p => p.Id == orderId);
    }

    /// <summary>
    /// Tarif d'achat courant de chaque produit pour ce fournisseur. Fixé automatiquement lors du premier bon,
    /// modifiable ensuite ; la modification ne s'applique qu'aux futurs bons.
    /// </summary>
    public async Task<Dictionary<int, decimal>> GetLastPricesAsync(int supplierId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.SupplierProductRates.AsNoTracking().Where(r => r.SupplierId == supplierId)
            .ToDictionaryAsync(r => r.ProductId, r => r.Rate);
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

        foreach (var line in BuildLines(input.Lines, products))
        {
            order.Lines.Add(line);
            products[line.ProductId].StockBalance += line.Quantity;
            movements.Add((new StockMovement
            {
                Date = input.Date,
                ProductId = line.ProductId,
                Quantity = line.Quantity,
                Kind = StockMovementKind.Purchase,
            }, line));
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
        foreach (var (movement, line) in movements)
        {
            movement.PurchaseOrderId = order.Id;
            movement.Note = order.Number;
            db.StockMovements.Add(movement);
            await UpsertRateAsync(db, input.SupplierId, line.ProductId, line.UnitCost);
        }
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return order;
    }

    /// <summary>
    /// Modifie un bon d'achat existant (fournisseur, lignes, montant payé) en gardant son numéro et sa date.
    /// Le stock des anciennes lignes est retiré puis celui des nouvelles ajouté ; le journal garde la trace des deux.
    /// Refusé si le stock deviendrait négatif (marchandise de ce bon déjà vendue).
    /// </summary>
    public async Task<PurchaseOrder> UpdateAsync(int orderId, PurchaseInput input)
    {
        if (input.Lines.Count == 0) throw new BusinessException("Ajoutez au moins un produit au bon d'achat.");

        await using var db = await _factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        var order = await db.PurchaseOrders.Include(p => p.Lines).FirstOrDefaultAsync(p => p.Id == orderId)
                    ?? throw new BusinessException("Ce bon n'existe plus.");
        if (!await db.Suppliers.AnyAsync(s => s.Id == input.SupplierId))
            throw new BusinessException("Sélectionnez un fournisseur.");

        var productIds = order.Lines.Select(l => l.ProductId).Concat(input.Lines.Select(l => l.ProductId)).Distinct().ToList();
        var products = await db.Products.Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id);

        var newLines = BuildLines(input.Lines, products);
        var total = newLines.Sum(l => l.LineTotal);
        if (input.AmountPaid < 0 || input.AmountPaid > total)
            throw new BusinessException("Le montant payé doit être compris entre 0 et le total du bon.");
        CheckStockAfterRemoval(order, products, newLines);

        var now = DateTime.Now;
        foreach (var old in order.Lines)
        {
            products[old.ProductId].StockBalance -= old.Quantity;
            db.StockMovements.Add(new StockMovement
            {
                Date = now, ProductId = old.ProductId, Quantity = -old.Quantity, Kind = StockMovementKind.Adjustment,
                PurchaseOrderId = order.Id, Note = $"Modification {order.Number} : annulation de l'ancienne ligne",
            });
        }
        db.PurchaseLines.RemoveRange(order.Lines);
        order.Lines.Clear();

        foreach (var line in newLines)
        {
            products[line.ProductId].StockBalance += line.Quantity;
            order.Lines.Add(line);
            db.StockMovements.Add(new StockMovement
            {
                Date = now, ProductId = line.ProductId, Quantity = line.Quantity, Kind = StockMovementKind.Purchase,
                PurchaseOrderId = order.Id, Note = $"{order.Number} (modifié)",
            });
        }

        order.SupplierId = input.SupplierId;
        order.Total = total;
        order.AmountPaid = input.AmountPaid;
        await db.SaveChangesAsync();

        // Si ce bon est le plus récent du fournisseur pour un produit, son coefficient devient le tarif proposé aux prochains bons.
        foreach (var line in newLines.GroupBy(l => l.ProductId).Select(g => g.Last()))
        {
            var hasNewer = await db.PurchaseLines.AnyAsync(l => l.ProductId == line.ProductId && db.PurchaseOrders.Any(o =>
                o.Id == l.PurchaseOrderId && o.SupplierId == input.SupplierId && (o.Date > order.Date || (o.Date == order.Date && o.Id > order.Id))));
            if (!hasNewer) await UpsertRateAsync(db, input.SupplierId, line.ProductId, line.UnitCost);
        }
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return order;
    }

    /// <summary>Supprime un bon d'achat : ses quantités sont retirées du stock (tracé dans le journal).</summary>
    public async Task DeleteAsync(int orderId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        var order = await db.PurchaseOrders.Include(p => p.Lines).FirstOrDefaultAsync(p => p.Id == orderId);
        if (order == null) return;

        var productIds = order.Lines.Select(l => l.ProductId).Distinct().ToList();
        var products = await db.Products.Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id);
        CheckStockAfterRemoval(order, products, []);

        var now = DateTime.Now;
        foreach (var line in order.Lines)
        {
            products[line.ProductId].StockBalance -= line.Quantity;
            db.StockMovements.Add(new StockMovement
            {
                Date = now, ProductId = line.ProductId, Quantity = -line.Quantity, Kind = StockMovementKind.Adjustment,
                Note = $"Suppression {order.Number}",
            });
        }

        db.PurchaseOrders.Remove(order);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    /// <summary>Refuse si, une fois les lignes de ce bon remplacées par <paramref name="newLines"/>, un stock deviendrait négatif.</summary>
    private static void CheckStockAfterRemoval(PurchaseOrder order, Dictionary<int, Product> products, List<PurchaseLine> newLines)
    {
        foreach (var product in products.Values)
        {
            var after = product.StockBalance
                        - order.Lines.Where(l => l.ProductId == product.Id).Sum(l => l.Quantity)
                        + newLines.Where(l => l.ProductId == product.Id).Sum(l => l.Quantity);
            if (after < 0)
                throw new BusinessException(
                    $"Stock insuffisant pour « {product.Name} » : une partie de ce bon a déjà été vendue " +
                    $"(le stock deviendrait {after:N2}).");
        }
    }

    /// <summary>Valide les lignes saisies et construit les lignes du bon (sans toucher au stock).</summary>
    private static List<PurchaseLine> BuildLines(IReadOnlyList<PurchaseLineInput> inputs, Dictionary<int, Product> products)
    {
        var lines = new List<PurchaseLine>();
        foreach (var line in inputs)
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

            lines.Add(new PurchaseLine
            {
                ProductId = product.Id,
                Quantity = line.Quantity,
                UnitCost = line.UnitCost,
                LineTotal = ComputeLineTotal(line.Quantity, line.UnitCost),
            });
        }
        return lines;
    }

    private static async Task UpsertRateAsync(AppDbContext db, int supplierId, int productId, decimal rate)
    {
        // Même précaution que pour les ventes : plusieurs lignes du même produit dans un bon d'achat.
        var existing = db.SupplierProductRates.Local.FirstOrDefault(r => r.SupplierId == supplierId && r.ProductId == productId)
                       ?? await db.SupplierProductRates.FirstOrDefaultAsync(r => r.SupplierId == supplierId && r.ProductId == productId);
        if (existing == null) db.SupplierProductRates.Add(new SupplierProductRate { SupplierId = supplierId, ProductId = productId, Rate = rate });
        else existing.Rate = rate;
    }
}
