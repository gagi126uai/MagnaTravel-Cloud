using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Auditoria de negocio 2026-06-12 (item 6, "viajó y debe"): una reserva EN VIAJE (Traveling) con
/// saldo impago no debe desaparecer de la alerta de viajes urgentes apenas arranca el viaje. Antes
/// el prefiltro StartDate >= hoy la excluia. Estos tests fijan el universo nuevo y protegen contra
/// regresion del caso futuro (que ya andaba).
/// </summary>
public class AlertTraveledAndOwingTests
{
    [Fact]
    public async Task UrgentTrips_IncludesTravelingReservationWithBalance_EvenAfterTripStarted()
    {
        using var context = new AppDbContext(NewDbOptions());
        // En viaje: arranco hace 3 dias, todavia no termino, y debe.
        context.Reservas.Add(TravelingReservaWithBalance(
            id: 1,
            startOffsetDays: -3,
            endOffsetDays: 4,
            balance: 500m));
        await context.SaveChangesAsync();

        var statuses = await GetUrgentStatusesAsync(context);

        Assert.Contains(EstadoReserva.Traveling, statuses);
    }

    [Fact]
    public async Task UrgentTrips_ExcludesTravelingReservationWithoutBalance()
    {
        using var context = new AppDbContext(NewDbOptions());
        // En viaje, ya empezo, pero SALDADA (balance 0): no es deuda, no debe figurar.
        context.Reservas.Add(TravelingReservaWithBalance(
            id: 1,
            startOffsetDays: -3,
            endOffsetDays: 4,
            balance: 0m));
        await context.SaveChangesAsync();

        var statuses = await GetUrgentStatusesAsync(context);

        Assert.DoesNotContain(EstadoReserva.Traveling, statuses);
    }

    [Fact]
    public async Task UrgentTrips_StillIncludesFutureReservationWithBalance_Regression()
    {
        using var context = new AppDbContext(NewDbOptions());
        // Caso historico que YA andaba: salida futura dentro de la ventana, con saldo.
        context.Reservas.Add(new Reserva
        {
            Id = 1,
            PublicId = Guid.NewGuid(),
            NumeroReserva = "R-1",
            Name = "Futura con deuda",
            Status = EstadoReserva.Confirmed,
            Balance = 300m,
            StartDate = DateTime.UtcNow.Date.AddDays(5)
        });
        await context.SaveChangesAsync();

        var statuses = await GetUrgentStatusesAsync(context);

        Assert.Contains(EstadoReserva.Confirmed, statuses);
    }

    [Fact]
    public async Task UrgentTrips_ExcludesTravelingReservationAlreadyEnded()
    {
        using var context = new AppDbContext(NewDbOptions());
        // En viaje pero el viaje ya termino (EndDate en el pasado): no es un viaje en curso, no figura
        // por la rama (B). (En la practica el lifecycle ya la habria movido a ToSettle, pero fijamos
        // el limite del filtro.)
        context.Reservas.Add(TravelingReservaWithBalance(
            id: 1,
            startOffsetDays: -10,
            endOffsetDays: -2,
            balance: 500m));
        await context.SaveChangesAsync();

        var statuses = await GetUrgentStatusesAsync(context);

        Assert.DoesNotContain(EstadoReserva.Traveling, statuses);
    }

    // ===== helpers =====

    private static Reserva TravelingReservaWithBalance(int id, int startOffsetDays, int endOffsetDays, decimal balance) => new()
    {
        Id = id,
        PublicId = Guid.NewGuid(),
        NumeroReserva = $"R-{id}",
        Name = $"Reserva {id}",
        Status = EstadoReserva.Traveling,
        Balance = balance,
        StartDate = DateTime.UtcNow.Date.AddDays(startOffsetDays),
        EndDate = DateTime.UtcNow.Date.AddDays(endOffsetDays)
    };

    private static async Task<List<string>> GetUrgentStatusesAsync(AppDbContext context)
    {
        var service = new AlertService(
            context,
            SettingsMock().Object,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AlertService>.Instance);

        dynamic result = await service.GetAlertsAsync(
            new AlertCallerContext("admin-test", IsAdmin: true),
            CancellationToken.None);

        return EnumerateStatuses(result.UrgentTrips);
    }

    private static List<string> EnumerateStatuses(object urgentTrips)
    {
        var result = new List<string>();
        foreach (var item in (IEnumerable)urgentTrips)
        {
            var statusProp = item.GetType().GetProperty("Status");
            if (statusProp?.GetValue(item) is string value)
                result.Add(value);
        }
        return result;
    }

    private static DbContextOptions<AppDbContext> NewDbOptions() =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(x => x.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

    private static Mock<IOperationalFinanceSettingsService> SettingsMock()
    {
        var mock = new Mock<IOperationalFinanceSettingsService>();
        mock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings
            {
                UpcomingUnpaidReservationAlertDays = 30
            });
        return mock;
    }
}
