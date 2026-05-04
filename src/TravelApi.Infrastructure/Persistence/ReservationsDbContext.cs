using MassTransit;
using Microsoft.EntityFrameworkCore;
using TravelApi.Domain.Entities;

namespace TravelApi.Infrastructure.Persistence;

public class ReservationsDbContext : DbContext
{
    public ReservationsDbContext(DbContextOptions<ReservationsDbContext> options)
        : base(options)
    {
    }

    public DbSet<Reserva> Reservas { get; set; } = null!;
    public DbSet<ReservaAttachment> ReservaAttachments { get; set; } = null!;
    public DbSet<Passenger> Passengers { get; set; } = null!;
    public DbSet<ServicioReserva> ServiciosReserva { get; set; } = null!;
    public DbSet<FlightSegment> FlightSegments { get; set; } = null!;
    public DbSet<HotelBooking> HotelBookings { get; set; } = null!;
    public DbSet<TransferBooking> TransferBookings { get; set; } = null!;
    public DbSet<PackageBooking> PackageBookings { get; set; } = null!;
    public DbSet<Payment> Payments { get; set; } = null!;
    public DbSet<PaymentReceipt> PaymentReceipts { get; set; } = null!;
    public DbSet<PassengerServiceAssignment> PassengerServiceAssignments { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // MassTransit Outbox (same as AppDbContext)
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();

        // ===================================================================
        // CRITICAL: Mirror all table/column mappings from AppDbContext.
        // The microservice shares the same database — table names and column
        // names MUST match exactly or EF Core generates wrong SQL.
        // ===================================================================

        // Reserva → "TravelFiles" (legacy table name)
        modelBuilder.Entity<Reserva>(entity =>
        {
            entity.ToTable("TravelFiles");
            entity.Property(f => f.NumeroReserva).HasColumnName("FileNumber");
            entity.HasIndex(r => r.PublicId).IsUnique();
            entity.Ignore(r => r.SourceQuote);

            entity.HasMany(r => r.Passengers)
                .WithOne(p => p.Reserva)
                .HasForeignKey(p => p.ReservaId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Passenger — ReservaId → "TravelFileId"
        modelBuilder.Entity<Passenger>(entity =>
        {
            entity.Property(p => p.ReservaId).HasColumnName("TravelFileId");
        });

        // ServicioReserva → "Reservations" (legacy table name)
        modelBuilder.Entity<ServicioReserva>(entity =>
        {
            entity.ToTable("Reservations");
            entity.Property(r => r.ReservaId).HasColumnName("TravelFileId");
        });

        // Payment — ReservaId → "TravelFileId", ServicioReservaId → "ReservationId"
        modelBuilder.Entity<Payment>(entity =>
        {
            entity.Property(p => p.ReservaId).HasColumnName("TravelFileId");
            entity.Property(p => p.ServicioReservaId).HasColumnName("ReservationId");
            entity.Property(p => p.Amount).HasColumnType("decimal(18,2)");
            entity.HasQueryFilter(p => !p.IsDeleted);
        });

        // FlightSegment — ReservaId → "TravelFileId", ServicioReservaId → "ReservationId"
        modelBuilder.Entity<FlightSegment>(entity =>
        {
            entity.Property(s => s.ReservaId).HasColumnName("TravelFileId");
            entity.Property(s => s.ServicioReservaId).HasColumnName("ReservationId");
        });

        // HotelBooking — ReservaId → "TravelFileId"
        modelBuilder.Entity<HotelBooking>(entity =>
        {
            entity.Property(h => h.ReservaId).HasColumnName("TravelFileId");
        });

        // TransferBooking — ReservaId → "TravelFileId"
        modelBuilder.Entity<TransferBooking>(entity =>
        {
            entity.Property(t => t.ReservaId).HasColumnName("TravelFileId");
        });

        // PackageBooking — ReservaId → "TravelFileId"
        modelBuilder.Entity<PackageBooking>(entity =>
        {
            entity.Property(p => p.ReservaId).HasColumnName("TravelFileId");
        });

        // Invoice — ReservaId → "TravelFileId"
        modelBuilder.Entity<Invoice>(entity =>
        {
            entity.Property(i => i.ReservaId).HasColumnName("TravelFileId");
        });

        // ReservaAttachment
        modelBuilder.Entity<ReservaAttachment>(entity =>
        {
            entity.ToTable("ReservaAttachments");
        });

        // PaymentReceipt
        modelBuilder.Entity<PaymentReceipt>(entity =>
        {
            entity.Property(r => r.Amount).HasPrecision(18, 2);
        });

        // SupplierPayment — ReservaId → "TravelFileId"
        modelBuilder.Entity<SupplierPayment>(entity =>
        {
            entity.Property(p => p.ReservaId).HasColumnName("TravelFileId");
            entity.Property(p => p.ServicioReservaId).HasColumnName("ReservationId");
        });

        // PassengerServiceAssignment — mismo mapeo que AppDbContext (tabla compartida)
        modelBuilder.Entity<PassengerServiceAssignment>(entity =>
        {
            entity.ToTable("PassengerServiceAssignments");
            entity.HasIndex(a => a.PublicId).IsUnique();
            entity.HasOne(a => a.Passenger)
                  .WithMany()
                  .HasForeignKey(a => a.PassengerId)
                  .OnDelete(DeleteBehavior.Cascade);
        });
    }
}

