using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using TravelApi.Models;

namespace TravelApi.Data;

public class AppDbContext : IdentityDbContext<ApplicationUser>
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Reservation> Reservations => Set<Reservation>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<Tariff> Tariffs => Set<Tariff>();
    public DbSet<TariffValidity> TariffValidities => Set<TariffValidity>();
    public DbSet<Cupo> Cupos => Set<Cupo>();
    public DbSet<CupoAssignment> CupoAssignments => Set<CupoAssignment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Customer>(entity =>
        {
            entity.Property(customer => customer.FullName)
                .HasMaxLength(200)
                .IsRequired();

            entity.Property(customer => customer.Email)
                .HasMaxLength(200);

            entity.Property(customer => customer.DocumentNumber)
                .HasMaxLength(50);

            entity.Property(customer => customer.Address)
                .HasMaxLength(300);
        });

        modelBuilder.Entity<Reservation>(entity =>
        {
            entity.Property(reservation => reservation.ReferenceCode)
                .HasMaxLength(50)
                .IsRequired();

            entity.Property(reservation => reservation.Status)
                .HasMaxLength(50)
                .IsRequired();

            entity.Property(reservation => reservation.ProductType)
                .HasMaxLength(50)
                .IsRequired();

            entity.Property(reservation => reservation.TotalAmount)
                .HasPrecision(12, 2);

            entity.Property(reservation => reservation.BasePrice)
                .HasPrecision(12, 2);

            entity.Property(reservation => reservation.Commission)
                .HasPrecision(12, 2);

            entity.Property(reservation => reservation.SupplierName)
                .HasMaxLength(200);

            entity.HasOne(reservation => reservation.Customer)
                .WithMany(customer => customer.Reservations)
                .HasForeignKey(reservation => reservation.CustomerId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Payment>(entity =>
        {
            entity.Property(payment => payment.Amount)
                .HasPrecision(12, 2);

            entity.Property(payment => payment.Method)
                .HasMaxLength(50)
                .IsRequired();

            entity.Property(payment => payment.Status)
                .HasMaxLength(50)
                .IsRequired();

            entity.HasOne(payment => payment.Reservation)
                .WithMany(reservation => reservation.Payments)
                .HasForeignKey(payment => payment.ReservationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Supplier>(entity =>
        {
            entity.Property(supplier => supplier.Name)
                .HasMaxLength(200)
                .IsRequired();

            entity.Property(supplier => supplier.Email)
                .HasMaxLength(200);

            entity.Property(supplier => supplier.Phone)
                .HasMaxLength(50);
        });

        modelBuilder.Entity<Tariff>(entity =>
        {
            entity.Property(tariff => tariff.Name)
                .HasMaxLength(200)
                .IsRequired();

            entity.Property(tariff => tariff.Description)
                .HasMaxLength(500);

            entity.Property(tariff => tariff.Currency)
                .HasConversion<string>()
                .HasMaxLength(10);

            entity.Property(tariff => tariff.DefaultPrice)
                .HasPrecision(12, 2);

            entity.HasMany(tariff => tariff.Validities)
                .WithOne(validity => validity.Tariff)
                .HasForeignKey(validity => validity.TariffId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TariffValidity>(entity =>
        {
            entity.Property(validity => validity.Price)
                .HasPrecision(12, 2);

            entity.Property(validity => validity.Notes)
                .HasMaxLength(500);
        });

        modelBuilder.Entity<Cupo>(entity =>
        {
            entity.Property(cupo => cupo.Name)
                .HasMaxLength(200)
                .IsRequired();

            entity.Property(cupo => cupo.ProductType)
                .HasMaxLength(50)
                .IsRequired();

            entity.Property(cupo => cupo.RowVersion)
                .IsConcurrencyToken();

            entity.HasMany(cupo => cupo.Assignments)
                .WithOne(assignment => assignment.Cupo)
                .HasForeignKey(assignment => assignment.CupoId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CupoAssignment>(entity =>
        {
            entity.HasOne(assignment => assignment.Reservation)
                .WithMany()
                .HasForeignKey(assignment => assignment.ReservationId)
                .OnDelete(DeleteBehavior.SetNull);
        });
    }
}
