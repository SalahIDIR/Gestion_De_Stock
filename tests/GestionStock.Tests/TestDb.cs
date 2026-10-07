using GestionStock.Core.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GestionStock.Tests;

/// <summary>
/// Base SQLite de test, créée avec les vraies migrations et les données de départ : en mémoire par défaut, ou dans
/// un fichier temporaire quand le test a besoin d'accès simultanés (une connexion en mémoire n'en supporte pas).
/// </summary>
public sealed class TestDb : IDbContextFactory<AppDbContext>, IDisposable
{
    private readonly SqliteConnection? _connection;
    private readonly string? _filePath;
    private readonly DbContextOptions<AppDbContext> _options;

    private TestDb(string? filePath)
    {
        if (filePath == null)
        {
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();
            _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        }
        else
        {
            _filePath = filePath;
            _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={filePath};Pooling=False").Options;
        }
    }

    public static Task<TestDb> CreateAsync() => InitializeAsync(new TestDb(null));

    public static Task<TestDb> CreateOnDiskAsync()
        => InitializeAsync(new TestDb(Path.Combine(Path.GetTempPath(), $"gestionstock-test-{Guid.NewGuid():N}.db")));

    private static async Task<TestDb> InitializeAsync(TestDb db)
    {
        await DatabaseInitializer.InitializeAsync(db);
        return db;
    }

    public AppDbContext CreateDbContext() => new(_options);

    public void Dispose()
    {
        _connection?.Dispose();
        if (_filePath != null && File.Exists(_filePath)) File.Delete(_filePath);
    }
}
