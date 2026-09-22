using GestionStock.Core.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GestionStock.Tests;

/// <summary>Base SQLite en mémoire, créée avec les vraies migrations et les données de départ.</summary>
public sealed class TestDb : IDbContextFactory<AppDbContext>, IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly DbContextOptions<AppDbContext> _options;

    private TestDb()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
    }

    public static async Task<TestDb> CreateAsync()
    {
        var db = new TestDb();
        await DatabaseInitializer.InitializeAsync(db);
        return db;
    }

    public AppDbContext CreateDbContext() => new(_options);

    public void Dispose() => _connection.Dispose();
}
