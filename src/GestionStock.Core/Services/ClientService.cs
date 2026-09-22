using GestionStock.Core.Data;
using GestionStock.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace GestionStock.Core.Services;

public class ClientService
{
    private readonly IDbContextFactory<AppDbContext> _factory;

    public ClientService(IDbContextFactory<AppDbContext> factory) => _factory = factory;

    public async Task<List<Client>> ListAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.Clients.AsNoTracking()
            .Include(c => c.Chips).ThenInclude(chip => chip.Operator)
            .OrderBy(c => c.Name).ToListAsync();
    }

    /// <summary>Enregistre le client et remplace l'ensemble de ses puces (une par opérateur au plus).</summary>
    public async Task<Client> SaveAsync(Client input)
    {
        var name = input.Name.Trim();
        if (name.Length == 0) throw new BusinessException("Le nom du client est obligatoire.");
        if (input.CreditLimit < 0) throw new BusinessException("Le plafond de crédit ne peut pas être négatif.");

        var chips = input.Chips
            .Where(c => !string.IsNullOrWhiteSpace(c.PhoneNumber))
            .Select(c => (c.OperatorId, Phone: c.PhoneNumber.Trim()))
            .ToList();
        if (chips.GroupBy(c => c.OperatorId).Any(g => g.Count() > 1))
            throw new BusinessException("Un client ne peut avoir qu'une puce par opérateur.");
        if (chips.Any(c => !c.Phone.All(char.IsDigit)))
            throw new BusinessException("Les numéros de puce ne doivent contenir que des chiffres.");

        await using var db = await _factory.CreateDbContextAsync();
        Client entity;
        if (input.Id == 0)
        {
            entity = new Client();
            db.Clients.Add(entity);
        }
        else
        {
            entity = await db.Clients.Include(c => c.Chips).FirstOrDefaultAsync(c => c.Id == input.Id)
                     ?? throw new BusinessException("Client introuvable.");
        }

        entity.Name = name;
        entity.Address = Clean(input.Address);
        entity.City = Clean(input.City);
        entity.Phone = Clean(input.Phone);
        entity.CreditLimit = input.CreditLimit;

        entity.Chips.Clear();
        foreach (var (operatorId, phone) in chips)
            entity.Chips.Add(new ClientChip { OperatorId = operatorId, PhoneNumber = phone });

        await db.SaveChangesAsync();
        return entity;
    }

    public async Task DeleteAsync(int id)
    {
        await using var db = await _factory.CreateDbContextAsync();
        if (await db.DeliveryNotes.AnyAsync(d => d.ClientId == id) || await db.ClientPayments.AnyAsync(p => p.ClientId == id))
            throw new BusinessException("Ce client a des bons de livraison ou des paiements : il ne peut pas être supprimé.");
        var entity = await db.Clients.FindAsync(id);
        if (entity == null) return;
        db.Clients.Remove(entity);
        await db.SaveChangesAsync();
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
