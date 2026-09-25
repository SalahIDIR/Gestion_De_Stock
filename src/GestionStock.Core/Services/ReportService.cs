using GestionStock.Core.Data;
using GestionStock.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace GestionStock.Core.Services;

/// <summary>Une ligne du rapport des opérations : un achat, une vente ou un encaissement.</summary>
public record OperationRow(
    DateTime Date,
    string Type,
    string Number,
    string Tiers,
    string? ProductName,
    string? ProductColorHex,
    decimal? Quantity,
    decimal? Rate,
    decimal Total,
    /// <summary>Type du produit de la ligne (null pour un encaissement).</summary>
    ProductKind? ProductKind = null,
    /// <summary>Stock du produit juste après cette opération (null pour un encaissement).</summary>
    decimal? StockAfter = null);

/// <summary>Rassemble achats, ventes et encaissements en un flux unique pour le rapport et l'audit.</summary>
public class ReportService
{
    private readonly IDbContextFactory<AppDbContext> _factory;

    public ReportService(IDbContextFactory<AppDbContext> factory) => _factory = factory;

    public async Task<List<OperationRow>> GetOperationsAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        var rows = new List<OperationRow>();

        // Stock restant après chaque mouvement : on rejoue le journal de stock de chaque produit dans l'ordre
        // chronologique (date puis ordre d'enregistrement), corrections d'inventaire comprises.
        var movements = await db.StockMovements.AsNoTracking().OrderBy(m => m.Id).ToListAsync();
        var balanceAfter = new Dictionary<int, decimal>();
        foreach (var product in movements.GroupBy(m => m.ProductId))
        {
            var running = 0m;
            foreach (var m in product.OrderBy(m => m.Date).ThenBy(m => m.Id))
            {
                running += m.Quantity;
                balanceAfter[m.Id] = running;
            }
        }
        // Chaque ligne de bon a créé un mouvement, dans le même ordre : la n-ième ligne d'un produit dans un bon
        // correspond au n-ième mouvement de ce produit pour ce bon.
        var purchaseBalances = movements.Where(m => m.PurchaseOrderId != null)
            .ToLookup(m => (m.PurchaseOrderId!.Value, m.ProductId), m => balanceAfter[m.Id]);
        var saleBalances = movements.Where(m => m.DeliveryNoteId != null)
            .ToLookup(m => (m.DeliveryNoteId!.Value, m.ProductId), m => balanceAfter[m.Id]);

        static decimal? StockAfterLine(ILookup<(int, int), decimal> balances, int noteId, int productId, int occurrence)
        {
            var list = balances[(noteId, productId)].ToList();
            return occurrence < list.Count ? list[occurrence] : null;
        }

        var purchases = await db.PurchaseOrders.AsNoTracking()
            .Include(p => p.Supplier).Include(p => p.Lines).ThenInclude(l => l.Product).ToListAsync();
        foreach (var order in purchases)
        {
            var seen = new Dictionary<int, int>();
            foreach (var line in order.Lines.OrderBy(l => l.Id))
            {
                var occurrence = seen.GetValueOrDefault(line.ProductId);
                seen[line.ProductId] = occurrence + 1;
                rows.Add(new OperationRow(order.Date, "Achat", order.Number, order.Supplier?.CompanyName ?? "",
                    line.Product?.Name, line.Product?.ColorHex, line.Quantity, line.UnitCost, line.LineTotal, line.Product?.Kind,
                    StockAfterLine(purchaseBalances, order.Id, line.ProductId, occurrence)));
            }
        }

        var deliveries = await db.DeliveryNotes.AsNoTracking()
            .Include(n => n.Client).Include(n => n.Lines).ThenInclude(l => l.Product).ToListAsync();
        foreach (var note in deliveries)
        {
            if (note.Lines.Count == 0)
            {
                rows.Add(new OperationRow(note.Date, "Encaissement", note.Number, note.Client?.Name ?? "",
                    null, null, null, null, note.AmountPaid));
            }
            else
            {
                var seen = new Dictionary<int, int>();
                foreach (var line in note.Lines.OrderBy(l => l.Id))
                {
                    var occurrence = seen.GetValueOrDefault(line.ProductId);
                    seen[line.ProductId] = occurrence + 1;
                    rows.Add(new OperationRow(note.Date, "Vente", note.Number, note.Client?.Name ?? "",
                        line.Product?.Name, line.Product?.ColorHex, line.Quantity, line.UnitPrice, line.LineTotal, line.Product?.Kind,
                        StockAfterLine(saleBalances, note.Id, line.ProductId, occurrence)));
                }

                // L'argent encaissé en même temps qu'une vente est de l'argent réellement entré en caisse :
                // il apparaît comme une ligne « Encaissement » juste après les lignes de la vente, avec le même numéro.
                if (note.AmountPaid > 0)
                    rows.Add(new OperationRow(note.Date, "Encaissement", note.Number, note.Client?.Name ?? "",
                        null, null, null, null, note.AmountPaid));
            }
        }

        var payments = await db.ClientPayments.AsNoTracking().Include(p => p.Client).ToListAsync();
        foreach (var payment in payments)
            rows.Add(new OperationRow(payment.Date, "Encaissement", $"REG-{payment.Id:D6}", payment.Client?.Name ?? "",
                null, null, null, null, payment.Amount));

        return rows.OrderByDescending(r => r.Date).ToList();
    }

    /// <summary>Achats et ventes de crédit virtuel uniquement (Flexy, Storm, Erselli…), sans encaissements ni produits physiques.</summary>
    public async Task<List<OperationRow>> GetVirtualCreditTransactionsAsync()
        => (await GetOperationsAsync()).Where(r => r.ProductKind == ProductKind.VirtualCredit).ToList();
}
