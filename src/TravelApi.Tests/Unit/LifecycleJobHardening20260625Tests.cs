using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
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
/// Endurecimientos del job nocturno de ciclo de vida (2026-06-25). Cubre los tres arreglos:
///
/// <list type="number">
///   <item><b>ARREGLO 1 (saldo rancio)</b>: el job NO promueve a "En viaje" ni cierra si el saldo SUBIO entre
///     la consulta inicial de la fase y el commit (un cajero borro/edito un cobro en el medio). Se re-lee el
///     Balance FRESCO de la base justo antes de aplicar y se re-valida la condicion de plata.</item>
///   <item><b>ARREGLO 3 (fila veneno)</b>: una fase que explota NO tumba la corrida entera — las demas fases
///     siguen corriendo esa noche. Y dentro de una fase, una fila que ya no esta (borrada) se saltea sin
///     abortar el resto.</item>
///   <item><b>ARREGLO 2 (concurrencia)</b>: el metodo que invoca Hangfire (<c>RunDailyAsync</c>) lleva el guard
///     <c>[DisableConcurrentExecution]</c> para que dos corridas (manual + programada) no se solapen.</item>
/// </list>
/// </summary>
public class LifecycleJobHardening20260625Tests
{
    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static ReservaLifecycleAutomationService NewJob(
        AppDbContext context, IOperationalFinanceSettingsService? settingsOverride = null)
    {
        IOperationalFinanceSettingsService settings;
        if (settingsOverride is not null)
        {
            settings = settingsOverride;
        }
        else
        {
            var mock = new Mock<IOperationalFinanceSettingsService>();
            mock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OperationalFinanceSettings());
            settings = mock.Object;
        }

        var engine = new ReservaAutoStateService(context, NullLogger<ReservaAutoStateService>.Instance);
        return new ReservaLifecycleAutomationService(
            context, NullLogger<ReservaLifecycleAutomationService>.Instance, settings, engine);
    }

    // ============================================================================================
    // ARREGLO 1 — saldo rancio: re-lectura del Balance FRESCO antes de aplicar la transicion.
    // ============================================================================================
    //
    // Por que via reflection: con InMemory, la consulta de candidatos y la re-lectura "fresca" leen la MISMA
    // fila del store dentro de un mismo contexto, asi que no se puede simular la carrera "el saldo subio en el
    // medio" pasando por el metodo de fase publico (ambas lecturas verian el mismo valor). Llamando directo a
    // ApplyTransitionsAsync le entregamos una PlannedTransition cuya entidad RASTREADA tiene Balance=0 (el valor
    // viejo, como lo habria cargado la fase) mientras la fila del store ya tiene Balance>0 (lo que dejo el
    // cajero). Eso es exactamente "saldo rancio vs fresco".

    private static Task<int> InvokeApplyTransitionsAsync(
        ReservaLifecycleAutomationService job, System.Collections.IList plannedList, string operation)
    {
        var method = typeof(ReservaLifecycleAutomationService)
            .GetMethod("ApplyTransitionsAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (Task<int>)method.Invoke(job, new object[] { plannedList, operation, CancellationToken.None })!;
    }

    private static System.Collections.IList NewPlannedList() =>
        (System.Collections.IList)Activator.CreateInstance(
            typeof(List<>).MakeGenericType(PlannedTransitionType()))!;

    private static Type PlannedTransitionType() =>
        typeof(ReservaLifecycleAutomationService)
            .GetNestedType("PlannedTransition", BindingFlags.NonPublic)!;

    private static Type MoneyGateType() =>
        typeof(ReservaLifecycleAutomationService)
            .GetNestedType("MoneyGate", BindingFlags.NonPublic)!;

    /// <summary>
    /// Construye una PlannedTransition (record privado) por reflexion. <paramref name="moneyGate"/> es el nombre
    /// del valor del enum MoneyGate ("ClientFullyPaid", "BalanceNonPositive", "None").
    /// </summary>
    private static object NewPlanned(
        Reserva reserva, string fromStatus, string toStatus, bool stampClosedAt, bool writeForwardLog,
        string? reason, string moneyGate)
    {
        var moneyGateValue = Enum.Parse(MoneyGateType(), moneyGate);
        var ctor = PlannedTransitionType().GetConstructors().Single();
        return ctor.Invoke(new object?[]
        {
            reserva, fromStatus, toStatus, stampClosedAt, writeForwardLog, reason, moneyGateValue
        });
    }

    [Fact]
    public async Task ApplyTransitions_ConfirmedToTraveling_DoesNotPromote_WhenFreshBalanceWentPositive()
    {
        await using var context = NewContext();

        // La fila del store quedo CON DEUDA (Balance 100): un cajero borro un cobro despues de que la fase
        // levanto los candidatos.
        var stored = new Reserva
        {
            Id = 1, NumeroReserva = "R-STALE-1", Name = "Saldo rancio",
            Status = EstadoReserva.Confirmed, Balance = 100m,
            StartDate = DateTime.UtcNow.Date
        };
        context.Reservas.Add(stored);
        await context.SaveChangesAsync();

        var job = NewJob(context);

        // La PlannedTransition lleva la entidad RASTREADA con el Balance VIEJO (0), como la habria cargado la
        // fase antes de que el cajero tocara la plata. El gate ClientFullyPaid debe re-leer el saldo FRESCO
        // (100) y NO promover.
        stored.Balance = 0m; // simula el valor rancio que tenia el objeto en memoria al planificar
        var planned = NewPlannedList();
        planned.Add(NewPlanned(stored, EstadoReserva.Confirmed, EstadoReserva.Traveling,
            stampClosedAt: false, writeForwardLog: true,
            reason: "Inicio de viaje", moneyGate: "ClientFullyPaid"));

        var applied = await InvokeApplyTransitionsAsync(job, planned, "Test_StaleConfirmed");

        Assert.Equal(0, applied);
        // La fila del store sigue Confirmed (no se promovio con numeros viejos).
        var fresh = await context.Reservas.AsNoTracking().SingleAsync(r => r.Id == 1);
        Assert.Equal(EstadoReserva.Confirmed, fresh.Status);
        // No se escribio rastro auditable de un avance que nunca paso.
        Assert.Empty(context.ReservaStatusChangeLogs.Where(l => l.ReservaId == 1));
    }

    [Fact]
    public async Task ApplyTransitions_TravelingToClosed_DoesNotClose_WhenFreshBalanceWentPositive()
    {
        await using var context = NewContext();

        var stored = new Reserva
        {
            Id = 1, NumeroReserva = "R-STALE-2", Name = "Cierre con deuda rancia",
            Status = EstadoReserva.Traveling, Balance = 250m,
            EndDate = DateTime.UtcNow.Date.AddDays(-1)
        };
        context.Reservas.Add(stored);
        await context.SaveChangesAsync();

        var job = NewJob(context);

        stored.Balance = 0m; // valor rancio en memoria
        var planned = NewPlannedList();
        planned.Add(NewPlanned(stored, EstadoReserva.Traveling, EstadoReserva.Closed,
            stampClosedAt: true, writeForwardLog: true,
            reason: "Fin de viaje", moneyGate: "BalanceNonPositive"));

        var applied = await InvokeApplyTransitionsAsync(job, planned, "Test_StaleClose");

        Assert.Equal(0, applied);
        var fresh = await context.Reservas.AsNoTracking().SingleAsync(r => r.Id == 1);
        Assert.Equal(EstadoReserva.Traveling, fresh.Status); // NO se cerro con deuda
        Assert.Null(fresh.ClosedAt);
    }

    [Fact]
    public async Task ApplyTransitions_ConfirmedToTraveling_Promotes_WhenFreshBalanceStillPaid()
    {
        // Control positivo: si el saldo FRESCO sigue saldado, el gate pasa y la reserva SI se promueve.
        await using var context = NewContext();

        var stored = new Reserva
        {
            Id = 1, NumeroReserva = "R-OK-1", Name = "Saldada de verdad",
            Status = EstadoReserva.Confirmed, Balance = 0m,
            StartDate = DateTime.UtcNow.Date
        };
        context.Reservas.Add(stored);
        await context.SaveChangesAsync();

        var job = NewJob(context);

        var planned = NewPlannedList();
        planned.Add(NewPlanned(stored, EstadoReserva.Confirmed, EstadoReserva.Traveling,
            stampClosedAt: false, writeForwardLog: true,
            reason: "Inicio de viaje", moneyGate: "ClientFullyPaid"));

        var applied = await InvokeApplyTransitionsAsync(job, planned, "Test_OkConfirmed");

        Assert.Equal(1, applied);
        var fresh = await context.Reservas.AsNoTracking().SingleAsync(r => r.Id == 1);
        Assert.Equal(EstadoReserva.Traveling, fresh.Status);
    }

    // ============================================================================================
    // ARREGLO 3 — fila veneno: una que se saltea no debe frenar a las demas (a nivel item y a nivel fase).
    // ============================================================================================

    [Fact]
    public async Task ApplyTransitions_SkipsDeletedRow_ButProcessesTheRest()
    {
        // Dos planificadas: una cuya fila fue BORRADA del store entre el plan y el commit (se saltea por el
        // null-guard) y otra sana que SI debe procesarse. Prueba que el loop continua tras saltear.
        await using var context = NewContext();

        var ghost = new Reserva
        {
            Id = 1, NumeroReserva = "R-GHOST", Name = "Borrada en el medio",
            Status = EstadoReserva.Confirmed, Balance = 0m, StartDate = DateTime.UtcNow.Date
        };
        var healthy = new Reserva
        {
            Id = 2, NumeroReserva = "R-HEALTHY", Name = "Sana",
            Status = EstadoReserva.Confirmed, Balance = 0m, StartDate = DateTime.UtcNow.Date
        };
        context.Reservas.AddRange(ghost, healthy);
        await context.SaveChangesAsync();

        var job = NewJob(context);

        // Borramos la fantasma del store (como si otra transaccion la hubiera eliminado) pero la dejamos en el
        // plan. Detach para que el re-fetch AsNoTracking devuelva null.
        context.Reservas.Remove(ghost);
        await context.SaveChangesAsync();
        context.Entry(ghost).State = EntityState.Detached;

        var planned = NewPlannedList();
        planned.Add(NewPlanned(ghost, EstadoReserva.Confirmed, EstadoReserva.Traveling,
            stampClosedAt: false, writeForwardLog: true, reason: "x", moneyGate: "ClientFullyPaid"));
        planned.Add(NewPlanned(healthy, EstadoReserva.Confirmed, EstadoReserva.Traveling,
            stampClosedAt: false, writeForwardLog: true, reason: "y", moneyGate: "ClientFullyPaid"));

        var applied = await InvokeApplyTransitionsAsync(job, planned, "Test_GhostSkip");

        Assert.Equal(1, applied); // solo la sana
        var fresh = await context.Reservas.AsNoTracking().SingleAsync(r => r.Id == 2);
        Assert.Equal(EstadoReserva.Traveling, fresh.Status);
    }

    [Fact]
    public async Task RunDaily_OnePhaseThrows_OtherPhasesStillRun()
    {
        // ARREGLO 3 a nivel FASE: si una fase explota (aca la de caducidad G6, porque el settings tira al pedir
        // los dias), las DEMAS fases siguen corriendo esa misma noche. Verificamos que la promocion
        // Confirmed->Traveling se aplica igual.
        await using var context = NewContext();

        var reserva = new Reserva
        {
            Id = 1, NumeroReserva = "R-PHASE", Name = "Debe viajar igual",
            Status = EstadoReserva.Confirmed, Balance = 0m, StartDate = DateTime.UtcNow.Date
        };
        context.Reservas.Add(reserva);
        // Un servicio confirmado: el job exige al menos uno para promover (HasAnyServiceAsync) y que no haya
        // inconsistencia de capacidad.
        context.HotelBookings.Add(new HotelBooking
        {
            Id = 10, ReservaId = 1, HotelName = "H", Status = WorkflowStatuses.Confirmado, ConfirmedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        // settings que EXPLOTA al pedir la entidad -> la fase G6 (AutoExpireStalePreSale) lanza.
        var poisonSettings = new Mock<IOperationalFinanceSettingsService>();
        poisonSettings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
                      .ThrowsAsync(new InvalidOperationException("settings caido (fase veneno)"));

        var job = NewJob(context, poisonSettings.Object);

        // No debe propagar: la corrida completa termina y devuelve el resultado de las fases que SI corrieron.
        var result = await job.RunDailyDetailedAsync(CancellationToken.None);

        Assert.Equal(0, result.Expired);   // la fase G6 fallo -> 0 caducadas (no rompio nada)
        Assert.Equal(1, result.Promoted);  // la promocion SI corrio pese a la fase caida
        var fresh = await context.Reservas.AsNoTracking().SingleAsync(r => r.Id == 1);
        Assert.Equal(EstadoReserva.Traveling, fresh.Status);
    }

    // ============================================================================================
    // ARREGLO 2 — guard de concurrencia: el metodo que invoca Hangfire lleva [DisableConcurrentExecution].
    // ============================================================================================

    [Fact]
    public void RunDailyAsync_HasDisableConcurrentExecutionGuard()
    {
        var method = typeof(ReservaLifecycleAutomationService).GetMethod(nameof(ReservaLifecycleAutomationService.RunDailyAsync))!;
        var attr = method.GetCustomAttribute<DisableConcurrentExecutionAttribute>();
        Assert.NotNull(attr); // sin el, dos corridas (manual + cron) podrian solaparse
    }
}
