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

    // Core Entities - Retail ERP
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<TravelFile> TravelFiles => Set<TravelFile>();
    public DbSet<Passenger> Passengers => Set<Passenger>();
    public DbSet<Reservation> Reservations => Set<Reservation>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<FlightSegment> FlightSegments => Set<FlightSegment>();
    
    // Sprint 4: Egresos y Configuración
    public DbSet<SupplierPayment> SupplierPayments => Set<SupplierPayment>();
    public DbSet<AgencySettings> AgencySettings => Set<AgencySettings>();
    public DbSet<CommissionRule> CommissionRules => Set<CommissionRule>();
    
    // Sprint 5: Servicios específicos y Tarifario
    public DbSet<HotelBooking> HotelBookings => Set<HotelBooking>();
    public DbSet<TransferBooking> TransferBookings => Set<TransferBooking>();
    public DbSet<PackageBooking> PackageBookings => Set<PackageBooking>();
    public DbSet<Rate> Rates => Set<Rate>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Supplier
        modelBuilder.Entity<Supplier>(entity =>
        {
            entity.Property(s => s.Name).IsRequired().HasMaxLength(100);
            entity.Property(s => s.ContactName).HasMaxLength(100);
            entity.Property(s => s.Email).HasMaxLength(100);
            entity.Property(s => s.Phone).HasMaxLength(50);
        });

        // Customer
        modelBuilder.Entity<Customer>(entity =>
        {
            entity.Property(c => c.FullName).HasMaxLength(200).IsRequired();
            entity.Property(c => c.Email).HasMaxLength(200);
            entity.Property(c => c.DocumentNumber).HasMaxLength(50);
            entity.Property(c => c.Address).HasMaxLength(300);
        });

        // TravelFile
        modelBuilder.Entity<TravelFile>(entity =>
        {
            entity.Property(f => f.FileNumber).HasMaxLength(50).IsRequired();
            entity.Property(f => f.Name).HasMaxLength(200).IsRequired();
            entity.Property(f => f.Status).HasMaxLength(50).IsRequired();

            entity.HasOne(f => f.Payer)
                  .WithMany()
                  .HasForeignKey(f => f.PayerId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasMany(f => f.Reservations)
                  .WithOne(r => r.TravelFile)
                  .HasForeignKey(r => r.TravelFileId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasMany(f => f.Passengers)
                  .WithOne(p => p.TravelFile)
                  .HasForeignKey(p => p.TravelFileId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(f => f.Payments)
                  .WithOne(p => p.TravelFile)
                  .HasForeignKey(p => p.TravelFileId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        // Passenger
        modelBuilder.Entity<Passenger>(entity =>
        {
            entity.Property(p => p.FullName).HasMaxLength(200).IsRequired();
            entity.Property(p => p.DocumentType).HasMaxLength(20);
            entity.Property(p => p.DocumentNumber).HasMaxLength(50);
            entity.Property(p => p.Nationality).HasMaxLength(50);
            entity.Property(p => p.Phone).HasMaxLength(50);
            entity.Property(p => p.Email).HasMaxLength(200);
            entity.Property(p => p.Gender).HasMaxLength(10);
        });

        // Reservation (Service)
        modelBuilder.Entity<Reservation>(entity =>
        {
            entity.Property(r => r.Status).HasMaxLength(50).IsRequired();
            entity.Property(r => r.SupplierName).HasMaxLength(200);

            entity.Property(r => r.NetCost).HasPrecision(12, 2);
            entity.Property(r => r.SalePrice).HasPrecision(12, 2);
            entity.Property(r => r.Commission).HasPrecision(12, 2);
            entity.Property(r => r.Tax).HasPrecision(12, 2);

            entity.HasOne(r => r.Customer)
                  .WithMany(c => c.Reservations)
                  .HasForeignKey(r => r.CustomerId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(r => r.Supplier)
                  .WithMany()
                  .HasForeignKey(r => r.SupplierId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        // Payment
        modelBuilder.Entity<Payment>(entity =>
        {
            entity.Property(p => p.Amount).HasPrecision(12, 2);
            entity.Property(p => p.Method).HasMaxLength(50).IsRequired();
            entity.Property(p => p.Status).HasMaxLength(50).IsRequired();

            entity.HasOne(p => p.Reservation)
                  .WithMany(r => r.Payments)
                  .HasForeignKey(p => p.ReservationId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // FlightSegment
        modelBuilder.Entity<FlightSegment>(entity =>
        {
            entity.Property(s => s.AirlineCode).HasMaxLength(3).IsRequired();
            entity.Property(s => s.FlightNumber).HasMaxLength(10).IsRequired();
            entity.Property(s => s.Origin).HasMaxLength(3).IsRequired();
            entity.Property(s => s.Destination).HasMaxLength(3).IsRequired();
            entity.Property(s => s.Status).HasMaxLength(2);

            entity.HasOne(s => s.Reservation)
                  .WithMany(r => r.Segments)
                  .HasForeignKey(s => s.ReservationId)
                  .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
