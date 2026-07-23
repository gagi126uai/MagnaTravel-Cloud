using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using TravelApi.Infrastructure.Services.Reservations;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// N1 (review 2026-07-23, nit T-7): el job nocturno <see cref="ReservaLifecycleAutomationService.AutoRepairTravelingDatesAsync"/>
/// rellena EndDate=null de reservas En viaje calculandolo desde los servicios. Caso borde real: una reserva En
/// viaje con EndDate null Y una correccion manual previa (<see cref="Reserva.DatesManuallySet"/>) — sin el
/// filtro nuevo, el job pisaria esa correccion con el recompute automatico, el MISMO bug que el fix de fechas
/// manuales (2026-07-23) cerro para el guardado de servicios.
/// </summary>
public class AutoRepairTravelingDatesManuallySetTests
{
    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static ReservaLifecycleAutomationService NewJob(AppDbContext context)
    {
        var settingsMock = new Mock<IOperationalFinanceSettingsService>();
        settingsMock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings());

        var engine = new ReservaAutoStateService(context, NullLogger<ReservaAutoStateService>.Instance);
        return new ReservaLifecycleAutomationService(
            context, NullLogger<ReservaLifecycleAutomationService>.Instance, settingsMock.Object, engine);
    }

    /// <summary>
    /// Siembra una reserva En viaje con EndDate null y UN hotel (para que el calculador tenga de donde
    /// inferir la fecha). El caller decide si la reserva queda marcada como corregida a mano.
    /// </summary>
    private static async Task<Reserva> SeedTravelingWithNullEndDateAsync(AppDbContext context, bool datesManuallySet)
    {
        var supplier = new Supplier { Name = "Operador Test" };
        var reserva = new Reserva
        {
            NumeroReserva = $"F-N1-{Guid.NewGuid():N}"[..10],
            Name = "Reserva en viaje sin EndDate",
            Status = EstadoReserva.Traveling,
            StartDate = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = null,
            DatesManuallySet = datesManuallySet,
        };
        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();

        context.HotelBookings.Add(new HotelBooking
        {
            ReservaId = reserva.Id,
            SupplierId = supplier.Id,
            HotelName = "Hotel Test",
            CheckIn = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
            CheckOut = new DateTime(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc),
            Status = "Confirmado",
        });
        await context.SaveChangesAsync();

        return reserva;
    }

    /// <summary>CASO CENTRAL del nit: con DatesManuallySet=true, el job NO toca la fecha (queda null).</summary>
    [Fact]
    public async Task ConCorreccionManual_NoRepara_EndDateSigueNull()
    {
        await using var context = NewContext();
        var reserva = await SeedTravelingWithNullEndDateAsync(context, datesManuallySet: true);
        var job = NewJob(context);

        var repaired = await job.AutoRepairTravelingDatesAsync(CancellationToken.None);

        Assert.Equal(0, repaired);
        var reloaded = await context.Reservas.AsNoTracking().SingleAsync(r => r.Id == reserva.Id);
        Assert.Null(reloaded.EndDate);
        Assert.True(reloaded.DatesManuallySet);
    }

    /// <summary>REGRESION: sin correccion manual (el caso de siempre), el job sigue reparando igual.</summary>
    [Fact]
    public async Task SinCorreccionManual_SigueReparandoComoSiempre()
    {
        await using var context = NewContext();
        var reserva = await SeedTravelingWithNullEndDateAsync(context, datesManuallySet: false);
        var job = NewJob(context);

        var repaired = await job.AutoRepairTravelingDatesAsync(CancellationToken.None);

        Assert.Equal(1, repaired);
        var reloaded = await context.Reservas.AsNoTracking().SingleAsync(r => r.Id == reserva.Id);
        Assert.Equal(new DateTime(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc), reloaded.EndDate);
    }
}
