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

    public DbSet<Reserva> Reservaciones { get; set; } = null!;
    public DbSet<ReservaAttachment> ReservaAttachments { get; set; } = null!;
    public DbSet<Passenger> Passengers { get; set; } = null!;
    public DbSet<ServicioReserva> ServiciosReserva { get; set; } = null!;
    public DbSet<FlightSegment> FlightSegments { get; set; } = null!;
    public DbSet<HotelBooking> HotelBookings { get; set; } = null!;
    public DbSet<TransferBooking> TransferBookings { get; set; } = null!;
    public DbSet<PackageBooking> PackageBookings { get; set; } = null!;
    public DbSet<Payment> Payments { get; set; } = null!;
    public DbSet<PaymentReceipt> PaymentReceipts { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        
        // Configuración estricta de schema para el microservicio de reservas
        modelBuilder.HasDefaultSchema("reservas");

        // Añadir Inbox/Outbox de MassTransit a este contexto
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();

        // Aplicamos las mismas configuraciones de llaves foráneas y tipos
        // Podemos usar las entidades del mismo dominio si están encapsuladas correctamente, 
        // o reusar la config general.
        modelBuilder.Entity<Reserva>()
            .HasIndex(r => r.PublicId)
            .IsUnique();

        modelBuilder.Entity<Reserva>()
            .HasIndex(r => r.PublicId)
            .IsUnique();

        modelBuilder.Entity<Reserva>()
            .HasIndex(r => r.PublicId)
            .IsUnique();

        modelBuilder.Entity<Reserva>().Ignore(r => r.SourceQuote);

        modelBuilder.Entity<Reserva>()
            .HasMany(r => r.Passengers)
            .WithOne(p => p.Reserva)
            .HasForeignKey(p => p.ReservaId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Payment>()
            .Property(p => p.Amount)
            .HasColumnType("decimal(18,2)");
    }
}
