using MassTransit;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using TravelApi.Domain.Entities;

namespace TravelApi.Infrastructure.Persistence;

public class ReservationsDbContext : AppDbContext
{
    public ReservationsDbContext(DbContextOptions<ReservationsDbContext> options, IHttpContextAccessor? httpContextAccessor = null)
        : base(ChangeOptionsType(options), httpContextAccessor)
    {
    }

    private static DbContextOptions<AppDbContext> ChangeOptionsType(DbContextOptions<ReservationsDbContext> options)
    {
        // En EF Core, podemos crear un nuevo builder a partir de las opciones existentes para cambiar el tipo genérico
        return new DbContextOptionsBuilder<AppDbContext>(options).Options;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        
        // Mapeo EXPLÍCITO de las entidades que pertenecen a este microservicio
        // El resto (Customers, Suppliers, Users) se quedarán en el esquema 'public' heredado de AppDbContext
        
        var reservationEntities = new[]
        {
            typeof(Reserva),
            typeof(ReservaAttachment),
            typeof(Passenger),
            typeof(ServicioReserva),
            typeof(FlightSegment),
            typeof(HotelBooking),
            typeof(TransferBooking),
            typeof(PackageBooking),
            typeof(Payment),
            typeof(PaymentReceipt)
        };

        foreach (var type in reservationEntities)
        {
            var entity = modelBuilder.Entity(type);
            var tableName = entity.Metadata.GetTableName() ?? type.Name;
            entity.ToTable(tableName, "reservas");
        }

        // Añadir Inbox/Outbox de MassTransit a este contexto (también en schema reservas)
        modelBuilder.AddInboxStateEntity().ToTable("InboxState", "reservas");
        modelBuilder.AddOutboxMessageEntity().ToTable("OutboxMessage", "reservas");
        modelBuilder.AddOutboxStateEntity().ToTable("OutboxState", "reservas");

        // Configuraciones específicas de Reservas
        modelBuilder.Entity<Reserva>()
            .HasIndex(r => r.PublicId)
            .IsUnique();

        modelBuilder.Entity<Reserva>().Ignore(r => r.SourceQuote);

        modelBuilder.Entity<Payment>()
            .Property(p => p.Amount)
            .HasColumnType("decimal(18,2)");
    }
}
