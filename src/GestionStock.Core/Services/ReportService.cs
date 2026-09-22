using GestionStock.Core.Data;
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
    decimal Total);

/// <summary>Rassemble achats, ventes et encaissements en un flux unique pour le rapport et l'audit.</summary>
public class ReportService
{
    private readonly IDbContextFactory<AppDbContext> _factory;

    public ReportService(IDbContextFactory<AppDbContext> factory) => _factory = factory;

    public async Task<List<OperationRow>> GetOperationsAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        var rows = new List<OperationRow>();

        var purchases = await db.PurchaseOrders.AsNoTracking()
            .Include(p => p.Supplier).Include(p => p.Lines).ThenInclude(l => l.Product).ToListAsync();
        foreach (var order in purchases)
            foreach (var line in order.Lines)
                rows.Add(new OperationRow(order.Date, "Achat", order.Number, order.Supplier?.CompanyName ?? "",
                    line.Product?.Name, line.Product?.ColorHex, line.Quantity, line.UnitCost, line.LineTotal));

        var deliveries = await db.DeliveryNotes.AsNoTracking()
            .Include(n => n.Client).Include(n => n.Lines).ThenInclude(l => l.Product).ToListAsync();
        foreach (var note in deliveries)
        {
            if (note.Lines.Count == 0)
                rows.Add(new OperationRow(note.Date, "Encaissement", note.Number, note.Client?.Name ?? "",
                    null, null, null, null, note.AmountPaid));
            else
                foreach (var line in note.Lines)
                    rows.Add(new OperationRow(note.Date, "Vente", note.Number, note.Client?.Name ?? "",
                        line.Product?.Name, line.Product?.ColorHex, line.Quantity, line.UnitPrice, line.LineTotal));
        }

        var payments = await db.ClientPayments.AsNoTracking().Include(p => p.Client).ToListAsync();
        foreach (var payment in payments)
            rows.Add(new OperationRow(payment.Date, "Encaissement", $"REG-{payment.Id:D6}", payment.Client?.Name ?? "",
                null, null, null, null, payment.Amount));

        return rows.OrderByDescending(r => r.Date).ToList();
    }
}
