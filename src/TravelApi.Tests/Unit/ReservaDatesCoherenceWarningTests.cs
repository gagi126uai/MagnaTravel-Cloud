using AutoMapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Contracts.Reservations;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Application.Mappings;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// FIX #27 (Tanda 3 del barrido de PROD, 2026-07-23): corregir las fechas de la reserva A MANO
/// (<c>ReservaService.UpdateDatesAsync</c>) las guarda tal cual las mando el usuario, SIN compararlas
/// contra los servicios cargados — si alguien corrigio la salida pero se olvido de correr tambien la
/// vuelta, la reserva quedaba con una cabecera que no reflejaba el itinerario real, sin ningun aviso.
///
/// <para>Estos tests fijan el aviso NUEVO (P-20, NO bloqueante): el guardado sigue funcionando igual
/// (las fechas se guardan siempre), pero si la ventana de cabecera no CONTIENE el rango real de los
/// servicios, el resultado trae <see cref="ReservaDto.Warning"/> con el texto fijo criollo (T-6).</para>
/// </summary>
public class ReservaDatesCoherenceWarningTests
{
    private const string ExpectedWarningText =
        "Ojo: las fechas que guardaste no coinciden con las de los servicios cargados. Revisá que sea lo que querés.";

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    private static IMapper CreateMapper()
        => new MapperConfiguration(config => config.AddProfile<MappingProfile>()).CreateMapper();

    private static UserManager<ApplicationUser> BuildUserManager()
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        return new UserManager<ApplicationUser>(
            store.Object, null!, null!,
            Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(),
            null!, null!, null!, null!);
    }

    private static ReservaService CreateReservaService(AppDbContext context, IMapper mapper)
    {
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings());
        return new ReservaService(context, mapper, settings.Object, BuildUserManager(), NullLogger<ReservaService>.Instance);
    }

    [Fact]
    public async Task Reserva_sin_servicios_cargados_no_tiene_nada_contra_que_comparar_SinAviso()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var reserva = new Reserva { Id = 1, NumeroReserva = "F-CW-1", Name = "Reserva sin servicios", Status = EstadoReserva.InManagement };
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();

        var service = CreateReservaService(context, mapper);
        var dto = await service.UpdateDatesAsync(
            reserva.Id.ToString(),
            new UpdateReservaDatesRequest(
                StartDate: new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
                EndDate: new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc)));

        Assert.Null(dto.Warning);
    }

    [Fact]
    public async Task Ventana_de_cabecera_cubre_las_fechas_reales_del_hotel_SinAviso()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var supplier = new Supplier { Id = 1, Name = "Operador Test" };
        var reserva = new Reserva { Id = 1, NumeroReserva = "F-CW-2", Name = "Reserva coherente", Status = EstadoReserva.InManagement };
        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        context.HotelBookings.Add(new HotelBooking
        {
            ReservaId = reserva.Id,
            SupplierId = supplier.Id,
            HotelName = "Hotel",
            City = "Bariloche",
            CheckIn = new DateTime(2026, 9, 5, 0, 0, 0, DateTimeKind.Utc),
            CheckOut = new DateTime(2026, 9, 8, 0, 0, 0, DateTimeKind.Utc),
        });
        await context.SaveChangesAsync();

        var service = CreateReservaService(context, mapper);
        var dto = await service.UpdateDatesAsync(
            reserva.Id.ToString(),
            // La ventana manual es MAS ANCHA que el hotel (incluye margen de traslado): sigue siendo coherente.
            new UpdateReservaDatesRequest(
                StartDate: new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
                EndDate: new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc)));

        Assert.Null(dto.Warning);
    }

    [Fact]
    public async Task Corrigio_solo_la_salida_y_se_olvido_de_la_vuelta_TraeAviso_YGuardaIgual()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var supplier = new Supplier { Id = 1, Name = "Operador Test" };
        var reserva = new Reserva { Id = 1, NumeroReserva = "F-CW-3", Name = "Reserva incoherente", Status = EstadoReserva.InManagement };
        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        context.HotelBookings.Add(new HotelBooking
        {
            ReservaId = reserva.Id,
            SupplierId = supplier.Id,
            HotelName = "Hotel",
            City = "Bariloche",
            CheckIn = new DateTime(2026, 9, 5, 0, 0, 0, DateTimeKind.Utc),
            CheckOut = new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc),
        });
        await context.SaveChangesAsync();

        var service = CreateReservaService(context, mapper);
        var manualStart = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        // El usuario corrigio la salida pero dejo la vuelta ANTES del checkout real del hotel (20/9).
        var manualEndBeforeRealCheckout = new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc);
        var dto = await service.UpdateDatesAsync(
            reserva.Id.ToString(),
            new UpdateReservaDatesRequest(StartDate: manualStart, EndDate: manualEndBeforeRealCheckout));

        // ASSERT CLAVE: el aviso aparece, PERO la fecha manual se guarda igual (P-20, nunca bloquea).
        Assert.Equal(ExpectedWarningText, dto.Warning);
        var reloaded = await context.Reservas.AsNoTracking().SingleAsync();
        Assert.Equal(manualStart, reloaded.StartDate);
        Assert.Equal(manualEndBeforeRealCheckout, reloaded.EndDate);
    }

    [Fact]
    public async Task Borro_la_fecha_de_salida_a_mano_habiendo_hotel_cargado_TraeAviso()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var supplier = new Supplier { Id = 1, Name = "Operador Test" };
        var reserva = new Reserva
        {
            Id = 1,
            NumeroReserva = "F-CW-4",
            Name = "Reserva sin fecha de salida",
            Status = EstadoReserva.InManagement,
            StartDate = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc),
        };
        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        context.HotelBookings.Add(new HotelBooking
        {
            ReservaId = reserva.Id,
            SupplierId = supplier.Id,
            HotelName = "Hotel",
            City = "Bariloche",
            CheckIn = new DateTime(2026, 9, 5, 0, 0, 0, DateTimeKind.Utc),
            CheckOut = new DateTime(2026, 9, 8, 0, 0, 0, DateTimeKind.Utc),
        });
        await context.SaveChangesAsync();

        var service = CreateReservaService(context, mapper);
        var dto = await service.UpdateDatesAsync(
            reserva.Id.ToString(),
            new UpdateReservaDatesRequest(StartDate: null, EndDate: null, ClearStartDate: true));

        Assert.Equal(ExpectedWarningText, dto.Warning);
        var reloaded = await context.Reservas.AsNoTracking().SingleAsync();
        Assert.Null(reloaded.StartDate);
    }
}
