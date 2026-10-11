using Core24.Web.Features.Catalog;
using Core24.Web.Features.Orders;
using Core24.Web.Features.Payments;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Core24.Web.Data;

/// <summary>A customer or admin login. Extends the built-in Identity user so fields can be added later.</summary>
public class AppUser : IdentityUser
{
}

/// <summary>
/// The whole database. Inherits the Identity tables (users, roles, logins) and adds our own.
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options) : IdentityDbContext<AppUser>(options)
{
    public DbSet<Mod> Mods => Set<Mod>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<OrderFile> OrderFiles => Set<OrderFile>();
    public DbSet<Payment> Payments => Set<Payment>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder); // Identity's own table setup

        builder.Entity<Mod>(e =>
        {
            e.Property(m => m.Name).HasMaxLength(120);
            e.Property(m => m.Description).HasMaxLength(500);
        });

        builder.Entity<Order>(e =>
        {
            e.HasIndex(o => o.AccessToken).IsUnique();
            e.HasIndex(o => o.CustomerEmailNormalized);
            e.HasIndex(o => o.Status);
            e.HasIndex(o => o.CreatedAtUtc);

            // Enums as text ("Paid" instead of 3): readable when you open the database.
            e.Property(o => o.Status).HasConversion<string>().HasMaxLength(20);

            e.Property(o => o.AccessToken).HasMaxLength(64);
            e.Property(o => o.CustomerName).HasMaxLength(100);
            e.Property(o => o.CustomerEmail).HasMaxLength(254);
            e.Property(o => o.CustomerEmailNormalized).HasMaxLength(254);
            e.Property(o => o.CustomerPhone).HasMaxLength(40);
            e.Property(o => o.VehicleMake).HasMaxLength(40);
            e.Property(o => o.VehicleModel).HasMaxLength(100);
            e.Property(o => o.Vin).HasMaxLength(17);
            e.Property(o => o.CustomerNote).HasMaxLength(2000);
            e.Property(o => o.Currency).HasMaxLength(3);

            // Deleting a user keeps their orders; they just become guest orders again.
            e.HasOne(o => o.User).WithMany().HasForeignKey(o => o.UserId).OnDelete(DeleteBehavior.SetNull);
            e.HasMany(o => o.Items).WithOne().HasForeignKey(i => i.OrderId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(o => o.Files).WithOne(f => f.Order).HasForeignKey(f => f.OrderId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(o => o.Payments).WithOne().HasForeignKey(p => p.OrderId).OnDelete(DeleteBehavior.Cascade);

            // Computed in C#, not columns.
            e.Ignore(o => o.Reference);
            e.Ignore(o => o.OriginalFiles);
            e.Ignore(o => o.ModifiedFiles);
        });

        builder.Entity<OrderItem>(e => e.Property(i => i.ModName).HasMaxLength(120));

        builder.Entity<OrderFile>(e =>
        {
            e.Property(f => f.Kind).HasConversion<string>().HasMaxLength(20);
            e.Property(f => f.FileName).HasMaxLength(255);
            e.Property(f => f.StorageKey).HasMaxLength(255);
            e.Property(f => f.Md5).HasMaxLength(32);
            e.Property(f => f.Vin).HasMaxLength(17);
            e.Property(f => f.EcuType).HasMaxLength(20);
            e.Property(f => f.MemoryArea).HasMaxLength(40);
            e.Property(f => f.ReadMethod).HasMaxLength(20);
        });

        builder.Entity<Payment>(e =>
        {
            e.HasIndex(p => p.SessionId).IsUnique();
            e.Property(p => p.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(p => p.Provider).HasMaxLength(20);
            e.Property(p => p.SessionId).HasMaxLength(255);
            e.Property(p => p.CheckoutUrl).HasMaxLength(2000);
            e.Property(p => p.Currency).HasMaxLength(3);
        });
    }
}

public static class Roles
{
    public const string Admin = "Admin";
}
