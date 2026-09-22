using GestionStock.Core.Data;
using GestionStock.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace GestionStock.Core.Services;

public class AuthService
{
    public const int MinPasswordLength = 6;

    private readonly IDbContextFactory<AppDbContext> _factory;

    public AuthService(IDbContextFactory<AppDbContext> factory) => _factory = factory;

    public async Task<bool> HasUsersAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.Users.AnyAsync();
    }

    /// <summary>Crée le compte administrateur au premier lancement.</summary>
    public async Task<User> CreateAdminAsync(string username, string password)
    {
        username = username.Trim();
        if (username.Length == 0) throw new BusinessException("Le nom d'utilisateur est obligatoire.");
        CheckPassword(password);

        await using var db = await _factory.CreateDbContextAsync();
        if (await db.Users.AnyAsync()) throw new BusinessException("Un compte existe déjà.");

        var user = new User { Username = username, PasswordHash = PasswordHasher.Hash(password) };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    /// <summary>Retourne l'utilisateur si les identifiants sont valides, sinon null.</summary>
    public async Task<User?> LoginAsync(string username, string password)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Username == username.Trim());
        return user != null && PasswordHasher.Verify(password, user.PasswordHash) ? user : null;
    }

    public async Task<bool> VerifyPasswordAsync(int userId, string password)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var user = await db.Users.FindAsync(userId);
        return user != null && PasswordHasher.Verify(password, user.PasswordHash);
    }

    public async Task ChangePasswordAsync(int userId, string currentPassword, string newPassword)
    {
        CheckPassword(newPassword);
        await using var db = await _factory.CreateDbContextAsync();
        var user = await db.Users.FindAsync(userId) ?? throw new BusinessException("Utilisateur introuvable.");
        if (!PasswordHasher.Verify(currentPassword, user.PasswordHash))
            throw new BusinessException("Le mot de passe actuel est incorrect.");
        user.PasswordHash = PasswordHasher.Hash(newPassword);
        await db.SaveChangesAsync();
    }

    private static void CheckPassword(string password)
    {
        if (password.Length < MinPasswordLength)
            throw new BusinessException($"Le mot de passe doit contenir au moins {MinPasswordLength} caractères.");
    }
}
