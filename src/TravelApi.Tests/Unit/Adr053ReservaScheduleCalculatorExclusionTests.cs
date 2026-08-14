using Microsoft.EntityFrameworkCore;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-053 D1 (2026-08-13): <see cref="ReservaScheduleCalculator.ComputeAsync"/> reemplaza a ADR-019 R8 —
/// el MIN/MAX de la reserva EXCLUYE servicios anulados. Un test por cada uno de los 6 tipos de servicio:
/// se siembra un servicio VIGENTE (con fechas angostas) y un servicio CANCELADO (con fechas mucho mas
/// anchas, para que su inclusión sería DETECTABLE si el filtro no funcionara) y se verifica que la
/// ventana calculada es SOLO la del vigente. Y el caso borde: si el ÚNICO servicio de un tipo está
/// cancelado, la reserva queda con <c>(null, null)</c>.
/// </summary>
public class Adr053ReservaScheduleCalculatorExclusionTests
{
    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static async Task<(AppDbContext Context, int ReservaId, Supplier Supplier)> SeedReservaAsync()
    {
        var context = CreateContext();
        var supplier = new Supplier { Name = "Operador Test" };
        var reserva = new Reserva { NumeroReserva = $"F-ADR053-{Guid.NewGuid():N}"[..12], Name = "Reserva exclusion", Status = EstadoReserva.InManagement };
        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();
        return (context, reserva.Id, supplier);
    }

    // Ventana VIGENTE angosta (la que debería ganar) vs ventana CANCELADA mucho mas ancha (la que NO
    // debería contar). Si el filtro fallara, el MIN/MAX terminaría reflejando la ventana ancha.
    private static readonly DateTime VigenteStart = new(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime VigenteEnd = new(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime CanceladoStart = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime CanceladoEnd = new(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Hotel_ServicioCanceladoNoCuenta_SoloElVigenteDefineLaVentana()
    {
        var (context, reservaId, supplier) = await SeedReservaAsync();
        context.HotelBookings.AddRange(
            new HotelBooking { ReservaId = reservaId, SupplierId = supplier.Id, HotelName = "Vigente", CheckIn = VigenteStart, CheckOut = VigenteEnd, Status = "Confirmado" },
            new HotelBooking { ReservaId = reservaId, SupplierId = supplier.Id, HotelName = "Cancelada", CheckIn = CanceladoStart, CheckOut = CanceladoEnd, Status = "Cancelada" });
        await context.SaveChangesAsync();

        var (start, end) = await ReservaScheduleCalculator.ComputeAsync(context, reservaId);

        Assert.Equal(VigenteStart, start);
        Assert.Equal(VigenteEnd, end);
    }

    [Fact]
    public async Task Hotel_UnicoServicioCancelado_QuedaSinFecha()
    {
        var (context, reservaId, supplier) = await SeedReservaAsync();
        context.HotelBookings.Add(new HotelBooking { ReservaId = reservaId, SupplierId = supplier.Id, HotelName = "Solo cancelado", CheckIn = CanceladoStart, CheckOut = CanceladoEnd, Status = "CANCELADO" });
        await context.SaveChangesAsync();

        var (start, end) = await ReservaScheduleCalculator.ComputeAsync(context, reservaId);

        Assert.Null(start);
        Assert.Null(end);
    }

    [Fact]
    public async Task Vuelo_ServicioCanceladoNoCuenta_SoloElVigenteDefineLaVentana()
    {
        var (context, reservaId, supplier) = await SeedReservaAsync();
        context.FlightSegments.AddRange(
            new FlightSegment { ReservaId = reservaId, SupplierId = supplier.Id, DepartureTime = VigenteStart, ArrivalTime = VigenteEnd, Status = "HK" },
            new FlightSegment { ReservaId = reservaId, SupplierId = supplier.Id, DepartureTime = CanceladoStart, ArrivalTime = CanceladoEnd, Status = "UN" });
        await context.SaveChangesAsync();

        var (start, end) = await ReservaScheduleCalculator.ComputeAsync(context, reservaId);

        Assert.Equal(VigenteStart, start);
        Assert.Equal(VigenteEnd, end);
    }

    [Fact]
    public async Task Vuelo_UnicoServicioCancelado_QuedaSinFecha()
    {
        var (context, reservaId, supplier) = await SeedReservaAsync();
        context.FlightSegments.Add(new FlightSegment { ReservaId = reservaId, SupplierId = supplier.Id, DepartureTime = CanceladoStart, ArrivalTime = CanceladoEnd, Status = "HX" });
        await context.SaveChangesAsync();

        var (start, end) = await ReservaScheduleCalculator.ComputeAsync(context, reservaId);

        Assert.Null(start);
        Assert.Null(end);
    }

    [Fact]
    public async Task Traslado_ServicioCanceladoNoCuenta_SoloElVigenteDefineLaVentana()
    {
        var (context, reservaId, supplier) = await SeedReservaAsync();
        context.TransferBookings.AddRange(
            new TransferBooking { ReservaId = reservaId, SupplierId = supplier.Id, PickupDateTime = VigenteStart, ReturnDateTime = VigenteEnd, Status = "Confirmado" },
            new TransferBooking { ReservaId = reservaId, SupplierId = supplier.Id, PickupDateTime = CanceladoStart, ReturnDateTime = CanceladoEnd, Status = "Cancelado" });
        await context.SaveChangesAsync();

        var (start, end) = await ReservaScheduleCalculator.ComputeAsync(context, reservaId);

        Assert.Equal(VigenteStart, start);
        Assert.Equal(VigenteEnd, end);
    }

    [Fact]
    public async Task Traslado_UnicoServicioCancelado_QuedaSinFecha()
    {
        var (context, reservaId, supplier) = await SeedReservaAsync();
        context.TransferBookings.Add(new TransferBooking { ReservaId = reservaId, SupplierId = supplier.Id, PickupDateTime = CanceladoStart, ReturnDateTime = CanceladoEnd, Status = "Cancelado" });
        await context.SaveChangesAsync();

        var (start, end) = await ReservaScheduleCalculator.ComputeAsync(context, reservaId);

        Assert.Null(start);
        Assert.Null(end);
    }

    [Fact]
    public async Task Paquete_ServicioCanceladoNoCuenta_SoloElVigenteDefineLaVentana()
    {
        var (context, reservaId, supplier) = await SeedReservaAsync();
        context.PackageBookings.AddRange(
            new PackageBooking { ReservaId = reservaId, SupplierId = supplier.Id, PackageName = "Vigente", StartDate = VigenteStart, EndDate = VigenteEnd, Status = "Confirmado" },
            new PackageBooking { ReservaId = reservaId, SupplierId = supplier.Id, PackageName = "Cancelado", StartDate = CanceladoStart, EndDate = CanceladoEnd, Status = "Cancelado" });
        await context.SaveChangesAsync();

        var (start, end) = await ReservaScheduleCalculator.ComputeAsync(context, reservaId);

        Assert.Equal(VigenteStart, start);
        Assert.Equal(VigenteEnd, end);
    }

    [Fact]
    public async Task Paquete_UnicoServicioCancelado_QuedaSinFecha()
    {
        var (context, reservaId, supplier) = await SeedReservaAsync();
        context.PackageBookings.Add(new PackageBooking { ReservaId = reservaId, SupplierId = supplier.Id, PackageName = "Solo cancelado", StartDate = CanceladoStart, EndDate = CanceladoEnd, Status = "Cancelado" });
        await context.SaveChangesAsync();

        var (start, end) = await ReservaScheduleCalculator.ComputeAsync(context, reservaId);

        Assert.Null(start);
        Assert.Null(end);
    }

    [Fact]
    public async Task Asistencia_ServicioCanceladoNoCuenta_SoloElVigenteDefineLaVentana()
    {
        var (context, reservaId, supplier) = await SeedReservaAsync();
        context.AssistanceBookings.AddRange(
            new AssistanceBooking { ReservaId = reservaId, SupplierId = supplier.Id, ValidFrom = VigenteStart, ValidTo = VigenteEnd, Status = "Confirmado" },
            new AssistanceBooking { ReservaId = reservaId, SupplierId = supplier.Id, ValidFrom = CanceladoStart, ValidTo = CanceladoEnd, Status = "Cancelado" });
        await context.SaveChangesAsync();

        var (start, end) = await ReservaScheduleCalculator.ComputeAsync(context, reservaId);

        Assert.Equal(VigenteStart, start);
        Assert.Equal(VigenteEnd, end);
    }

    [Fact]
    public async Task Asistencia_UnicoServicioCancelado_QuedaSinFecha()
    {
        var (context, reservaId, supplier) = await SeedReservaAsync();
        context.AssistanceBookings.Add(new AssistanceBooking { ReservaId = reservaId, SupplierId = supplier.Id, ValidFrom = CanceladoStart, ValidTo = CanceladoEnd, Status = "Cancelado" });
        await context.SaveChangesAsync();

        var (start, end) = await ReservaScheduleCalculator.ComputeAsync(context, reservaId);

        Assert.Null(start);
        Assert.Null(end);
    }

    [Fact]
    public async Task Generico_ServicioCanceladoNoCuenta_SoloElVigenteDefineLaVentana()
    {
        var (context, reservaId, _) = await SeedReservaAsync();
        context.Servicios.AddRange(
            new ServicioReserva { ReservaId = reservaId, ServiceType = "Excursion", DepartureDate = VigenteStart, ReturnDate = VigenteEnd, Status = "Confirmado", CreatedAt = DateTime.UtcNow },
            new ServicioReserva { ReservaId = reservaId, ServiceType = "Excursion", DepartureDate = CanceladoStart, ReturnDate = CanceladoEnd, Status = "Cancelado", CreatedAt = DateTime.UtcNow });
        await context.SaveChangesAsync();

        var (start, end) = await ReservaScheduleCalculator.ComputeAsync(context, reservaId);

        Assert.Equal(VigenteStart, start);
        Assert.Equal(VigenteEnd, end);
    }

    [Fact]
    public async Task Generico_UnicoServicioCancelado_QuedaSinFecha()
    {
        var (context, reservaId, _) = await SeedReservaAsync();
        context.Servicios.Add(new ServicioReserva { ReservaId = reservaId, ServiceType = "Excursion", DepartureDate = CanceladoStart, ReturnDate = CanceladoEnd, Status = "Cancelado", CreatedAt = DateTime.UtcNow });
        await context.SaveChangesAsync();

        var (start, end) = await ReservaScheduleCalculator.ComputeAsync(context, reservaId);

        Assert.Null(start);
        Assert.Null(end);
    }
}
