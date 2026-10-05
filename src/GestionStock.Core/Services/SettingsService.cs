using GestionStock.Core.Data;
using GestionStock.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace GestionStock.Core.Services;

public class SettingsService
{
    private readonly IDbContextFactory<AppDbContext> _factory;

    public SettingsService(IDbContextFactory<AppDbContext> factory) => _factory = factory;

    public async Task<AppSettings> GetAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.Settings.AsNoTracking().FirstOrDefaultAsync() ?? new AppSettings();
    }

    public async Task SaveAsync(AppSettings settings)
    {
        if (settings.InventoryStart.HasValue && settings.InventoryEnd.HasValue
            && settings.InventoryEnd < settings.InventoryStart)
            throw new BusinessException("La date de fin d'inventaire précède la date de début.");

        var email = settings.CompanyEmail.Trim();
        if (email.Length > 0 && !email.Contains('@'))
            throw new BusinessException("L'adresse e-mail n'est pas valide.");

        await using var db = await _factory.CreateDbContextAsync();
        var current = await db.Settings.FirstOrDefaultAsync();
        if (current == null)
        {
            current = new AppSettings();
            db.Settings.Add(current);
        }
        current.CompanyEmail = email;
        current.InventoryStart = settings.InventoryStart;
        current.InventoryEnd = settings.InventoryEnd;
        await db.SaveChangesAsync();
    }

    public async Task<List<Operator>> GetOperatorsAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.Operators.AsNoTracking().OrderBy(o => o.Id).ToListAsync();
    }

    /// <summary>
    /// Met à jour le routage matériel d'un opérateur : port COM, requête USSD, chiffre de confirmation, mot-clé de
    /// succès et si la confirmation réelle arrive par SMS. Ces « codes » sont fournis par l'opérateur et peuvent
    /// changer ; ils sont donc modifiables ici plutôt que codés en dur.
    /// </summary>
    public async Task SaveOperatorRoutingAsync(int operatorId, string? comPort, string? ussdTemplate,
        string? confirmKeystroke = null, string? successKeyword = null, bool confirmationViaSms = false,
        string? balanceUssdCode = null)
    {
        comPort = string.IsNullOrWhiteSpace(comPort) ? null : comPort.Trim().ToUpperInvariant();
        if (comPort != null && !System.Text.RegularExpressions.Regex.IsMatch(comPort, @"^COM\d{1,3}$"))
            throw new BusinessException("Le port COM doit avoir la forme COM1, COM32, etc.");

        await using var db = await _factory.CreateDbContextAsync();
        var op = await db.Operators.FindAsync(operatorId) ?? throw new BusinessException("Opérateur introuvable.");

        if (comPort != null)
        {
            var other = await db.Operators.Where(o => o.Id != operatorId && o.ComPort == comPort)
                .Select(o => o.Name).FirstOrDefaultAsync();
            if (other != null) throw new BusinessException($"Le port {comPort} est déjà assigné à {other}.");
        }

        op.ComPort = comPort;
        op.UssdTemplate = string.IsNullOrWhiteSpace(ussdTemplate) ? null : ussdTemplate.Trim();
        op.ConfirmKeystroke = string.IsNullOrWhiteSpace(confirmKeystroke) ? null : confirmKeystroke.Trim();
        op.SuccessKeyword = string.IsNullOrWhiteSpace(successKeyword) ? null : successKeyword.Trim();
        op.ConfirmationViaSms = confirmationViaSms;
        op.BalanceUssdCode = string.IsNullOrWhiteSpace(balanceUssdCode) ? null : balanceUssdCode.Trim();
        await db.SaveChangesAsync();
    }
}
