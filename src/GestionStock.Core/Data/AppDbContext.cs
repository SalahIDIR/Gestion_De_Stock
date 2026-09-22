using GestionStock.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace GestionStock.Core.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<AppSettings> Settings => Set<AppSettings>();
    public DbSet<Operator> Operators => Set<Operator>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<Client> Clients => Set<Client>();
    public DbSet<ClientChip> ClientChips => Set<ClientChip>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<PurchaseOrder> PurchaseOrders => Set<PurchaseOrder>();
    public DbSet<PurchaseLine> PurchaseLines => Set<PurchaseLine>();
    public DbSet<DeliveryNote> DeliveryNotes => Set<DeliveryNote>();
    public DbSet<DeliveryLine> DeliveryLines => Set<DeliveryLine>();
    public DbSet<ClientPayment> ClientPayments => Set<ClientPayment>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>().HasIndex(u => u.Username).IsUnique();
        b.Entity<Operator>().HasIndex(o => o.Name).IsUnique();
        b.Entity<Supplier>().HasIndex(s => s.Reference).IsUnique();
        b.Entity<PurchaseOrder>().HasIndex(p => p.Number).IsUnique();

        b.Entity<Client>()
            .HasMany(c => c.Chips).WithOne(c => c.Client).HasForeignKey(c => c.ClientId)
            .OnDelete(DeleteBehavior.Cascade);
        b.Entity<ClientChip>().HasIndex(c => new { c.ClientId, c.OperatorId }).IsUnique();

        b.Entity<PurchaseOrder>()
            .HasMany(p => p.Lines).WithOne().HasForeignKey(l => l.PurchaseOrderId)
            .OnDelete(DeleteBehavior.Cascade);
        b.Entity<PurchaseOrder>().Ignore(p => p.Remaining);

        // Un fournisseur, un client ou un produit déjà utilisé ne peut pas être supprimé : l'historique doit rester cohérent.
        b.Entity<PurchaseOrder>().HasOne(p => p.Supplier).WithMany().HasForeignKey(p => p.SupplierId)
            .OnDelete(DeleteBehavior.Restrict);
        b.Entity<PurchaseLine>().HasOne(l => l.Product).WithMany().HasForeignKey(l => l.ProductId)
            .OnDelete(DeleteBehavior.Restrict);
        b.Entity<StockMovement>().HasOne(m => m.Product).WithMany().HasForeignKey(m => m.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        b.Entity<DeliveryNote>().HasIndex(d => d.Number).IsUnique();
        b.Entity<DeliveryNote>().Ignore(d => d.Remaining);
        b.Entity<DeliveryNote>()
            .HasMany(d => d.Lines).WithOne().HasForeignKey(l => l.DeliveryNoteId)
            .OnDelete(DeleteBehavior.Cascade);
        b.Entity<DeliveryNote>().HasOne(d => d.Client).WithMany().HasForeignKey(d => d.ClientId)
            .OnDelete(DeleteBehavior.Restrict);
        b.Entity<DeliveryLine>().HasOne(l => l.Product).WithMany().HasForeignKey(l => l.ProductId)
            .OnDelete(DeleteBehavior.Restrict);
        b.Entity<ClientPayment>().HasOne(p => p.Client).WithMany().HasForeignKey(p => p.ClientId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
