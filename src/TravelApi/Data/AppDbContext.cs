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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
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
    }
}
