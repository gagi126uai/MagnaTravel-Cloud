using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Reservations;
using TravelApi.Infrastructure.Services;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Integration;

/// <summary>
/// ADR-053 (2026-08-13, D5) — corre el SQL CRUDO REAL del backfill (las 2 constantes de
/// <see cref="Adr053BackfillSql"/>, las MISMAS que usa la migración <c>Adr053_M1_TripWindowRecalculatedAndPromisedDates</c>
/// — no una copia) contra datos sembrados en Postgres y compara el resultado contra
/// <see cref="ReservaScheduleCalculator.ComputeAsync"/> en vivo (el mismo cálculo en C#, ya probado por
/// separado en <see cref="Unit.Adr053ReservaScheduleCalculatorExclusionTests"/>/
/// <see cref="Unit.WorkflowStatusHelperEquivalenceTests"/>).
///
/// <para><b>ADVERTENCIA (M1 del review, repetida a propósito)</b>: este test es un ORÁCULO de consistencia
/// SQL↔C#, NO una prueba de que el predicado sea correcto para el negocio. Si el lado C# también estuviera
/// mal, este test daría VERDE comparando dos versiones igualmente equivocadas entre sí. La cobertura real
/// del negocio la dan los tests unitarios contra <c>WorkflowStatusHelper</c> — un VERDE acá nunca debe
/// leerse como "el predicado es correcto", solo como "el SQL no divergió del C# que ya se probó aparte".</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class Adr053BackfillSqlIntegrationTests : IClassFixture<PostgresIntegrationFixture>, IAsyncLifetime
{
    private readonly PostgresIntegrationFixture _fixture;

    public Adr053BackfillSqlIntegrationTests(PostgresIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => _fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly DateTime VigenteStart = new(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime VigenteEnd = new(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime CanceladoStart = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime CanceladoEnd = new(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Rama 1: reserva con servicios de los 6 tipos, algunos VIGENTES y otros CANCELADOS con distintas
    /// variantes de texto (incluido "Cancelada" femenino) — su ventana calculada por el SQL crudo del
    /// backfill tiene que coincidir EXACTO con <c>ComputeAsync</c> en C#, y el log tiene que registrar el
    /// cambio (la reserva arranca con StartDate/EndDate en null, antes del backfill).
    /// </summary>
    [Fact]
    public async Task Backfill_ReservaConServiciosMixtos_CoincideConElCalculoEnVivo_YQuedaEnElLog()
    {
        int reservaId;
        await using (var seedCtx = _fixture.CreateDbContext())
        {
            var customer = new Customer { FullName = "Cliente ADR053-1", TaxCondition = "Consumidor Final", IsActive = true };
            var supplier = new Supplier { Name = "Operador ADR053-1", IsActive = true, TaxCondition = "IVA_RESP_INSCRIPTO" };
            seedCtx.Customers.Add(customer);
            seedCtx.Suppliers.Add(supplier);
            await seedCtx.SaveChangesAsync();

            var reserva = new Reserva
            {
                NumeroReserva = "F-ADR053-BF-1", Name = "Backfill mixto", Status = EstadoReserva.InManagement,
                PayerId = customer.Id, StartDate = null, EndDate = null,
            };
            seedCtx.Reservas.Add(reserva);
            await seedCtx.SaveChangesAsync();
            reservaId = reserva.Id;

            seedCtx.HotelBookings.Add(new HotelBooking
            {
                ReservaId = reservaId, SupplierId = supplier.Id, HotelName = "Vigente",
                CheckIn = VigenteStart, CheckOut = VigenteEnd, Status = "Confirmado",
            });
            seedCtx.HotelBookings.Add(new HotelBooking
            {
                ReservaId = reservaId, SupplierId = supplier.Id, HotelName = "Cancelada (femenino)",
                CheckIn = CanceladoStart, CheckOut = CanceladoEnd, Status = "Cancelada",
            });
            seedCtx.FlightSegments.Add(new FlightSegment
            {
                ReservaId = reservaId, SupplierId = supplier.Id, Status = "UN",
                DepartureTime = CanceladoStart, ArrivalTime = CanceladoEnd,
            });
            await seedCtx.SaveChangesAsync();
        }

        // ORACULO: el calculo EN VIVO (C#, ya probado aparte contra WorkflowStatusHelper).
        DateTime? liveStart, liveEnd;
        await using (var oracleCtx = _fixture.CreateDbContext())
        {
            (liveStart, liveEnd) = await ReservaScheduleCalculator.ComputeAsync(oracleCtx, reservaId, CancellationToken.None);
        }

        // Correr el SQL CRUDO REAL del backfill (las mismas 2 constantes que usa la migracion), en orden
        // (INSERT del log primero, lee el valor VIEJO antes de que el UPDATE lo pise).
        await using (var backfillCtx = _fixture.CreateDbContext())
        {
            await backfillCtx.Database.ExecuteSqlRawAsync(Adr053BackfillSql.InsertBackfillLog);
            await backfillCtx.Database.ExecuteSqlRawAsync(Adr053BackfillSql.UpdateTravelFilesWindow);
        }

        await using var verifyCtx = _fixture.CreateDbContext();
        var backfilled = await verifyCtx.Reservas.AsNoTracking().SingleAsync(r => r.Id == reservaId);
        Assert.Equal(liveStart, backfilled.StartDate);
        Assert.Equal(liveEnd, backfilled.EndDate);
        Assert.Equal(VigenteStart, backfilled.StartDate); // valor concreto esperado: solo el hotel vigente cuenta
        Assert.Equal(VigenteEnd, backfilled.EndDate);

        var logRow = await verifyCtx.Adr053TripWindowBackfillLogs.AsNoTracking().SingleAsync(l => l.ReservaId == reservaId);
        Assert.Null(logRow.OldStartDate);
        Assert.Null(logRow.OldEndDate);
        Assert.Equal(VigenteStart, logRow.NewStartDate);
        Assert.Equal(VigenteEnd, logRow.NewEndDate);
    }

    /// <summary>
    /// Rama 2: reserva SIN ningún servicio vigente (el único hotel está cancelado) — tiene que quedar
    /// NULL/NULL, coincidiendo con el cálculo en vivo, y CON fila en el log (cambió: antes tenía un valor
    /// heredado del viejo criterio con-cancelados).
    /// </summary>
    [Fact]
    public async Task Backfill_ReservaSinServiciosVigentes_QuedaNullNull_YQuedaEnElLog()
    {
        int reservaId;
        await using (var seedCtx = _fixture.CreateDbContext())
        {
            var customer = new Customer { FullName = "Cliente ADR053-2", TaxCondition = "Consumidor Final", IsActive = true };
            var supplier = new Supplier { Name = "Operador ADR053-2", IsActive = true, TaxCondition = "IVA_RESP_INSCRIPTO" };
            seedCtx.Customers.Add(customer);
            seedCtx.Suppliers.Add(supplier);
            await seedCtx.SaveChangesAsync();

            var reserva = new Reserva
            {
                NumeroReserva = "F-ADR053-BF-2", Name = "Backfill sin vigentes", Status = EstadoReserva.InManagement,
                PayerId = customer.Id,
                // Simula el criterio VIEJO (con cancelados): la cabecera tenia un valor heredado del
                // hotel cancelado, que el backfill tiene que CORREGIR a null/null.
                StartDate = CanceladoStart, EndDate = CanceladoEnd,
            };
            seedCtx.Reservas.Add(reserva);
            await seedCtx.SaveChangesAsync();
            reservaId = reserva.Id;

            seedCtx.HotelBookings.Add(new HotelBooking
            {
                ReservaId = reservaId, SupplierId = supplier.Id, HotelName = "Solo cancelado",
                CheckIn = CanceladoStart, CheckOut = CanceladoEnd, Status = "CANCELADO",
            });
            await seedCtx.SaveChangesAsync();
        }

        await using (var backfillCtx = _fixture.CreateDbContext())
        {
            await backfillCtx.Database.ExecuteSqlRawAsync(Adr053BackfillSql.InsertBackfillLog);
            await backfillCtx.Database.ExecuteSqlRawAsync(Adr053BackfillSql.UpdateTravelFilesWindow);
        }

        await using var verifyCtx = _fixture.CreateDbContext();
        var backfilled = await verifyCtx.Reservas.AsNoTracking().SingleAsync(r => r.Id == reservaId);
        Assert.Null(backfilled.StartDate);
        Assert.Null(backfilled.EndDate);

        var logRow = await verifyCtx.Adr053TripWindowBackfillLogs.AsNoTracking().SingleAsync(l => l.ReservaId == reservaId);
        Assert.Equal(CanceladoStart, logRow.OldStartDate);
        Assert.Equal(CanceladoEnd, logRow.OldEndDate);
        Assert.Null(logRow.NewStartDate);
        Assert.Null(logRow.NewEndDate);
    }

    /// <summary>
    /// Rama 3: reserva cuyo StartDate/EndDate persistido YA coincide con el cálculo nuevo (nunca tuvo
    /// servicios cancelados de por medio) — el backfill NO debe insertar fila en el log (solo se registran
    /// las reservas cuyo valor CAMBIÓ).
    /// </summary>
    [Fact]
    public async Task Backfill_ReservaSinCambios_NoInsertaFilaEnElLog()
    {
        int reservaId;
        await using (var seedCtx = _fixture.CreateDbContext())
        {
            var customer = new Customer { FullName = "Cliente ADR053-3", TaxCondition = "Consumidor Final", IsActive = true };
            var supplier = new Supplier { Name = "Operador ADR053-3", IsActive = true, TaxCondition = "IVA_RESP_INSCRIPTO" };
            seedCtx.Customers.Add(customer);
            seedCtx.Suppliers.Add(supplier);
            await seedCtx.SaveChangesAsync();

            var reserva = new Reserva
            {
                NumeroReserva = "F-ADR053-BF-3", Name = "Backfill sin cambios", Status = EstadoReserva.InManagement,
                PayerId = customer.Id, StartDate = VigenteStart, EndDate = VigenteEnd,
            };
            seedCtx.Reservas.Add(reserva);
            await seedCtx.SaveChangesAsync();
            reservaId = reserva.Id;

            seedCtx.HotelBookings.Add(new HotelBooking
            {
                ReservaId = reservaId, SupplierId = supplier.Id, HotelName = "Vigente",
                CheckIn = VigenteStart, CheckOut = VigenteEnd, Status = "Confirmado",
            });
            await seedCtx.SaveChangesAsync();
        }

        await using (var backfillCtx = _fixture.CreateDbContext())
        {
            await backfillCtx.Database.ExecuteSqlRawAsync(Adr053BackfillSql.InsertBackfillLog);
            await backfillCtx.Database.ExecuteSqlRawAsync(Adr053BackfillSql.UpdateTravelFilesWindow);
        }

        await using var verifyCtx = _fixture.CreateDbContext();
        var backfilled = await verifyCtx.Reservas.AsNoTracking().SingleAsync(r => r.Id == reservaId);
        Assert.Equal(VigenteStart, backfilled.StartDate);
        Assert.Equal(VigenteEnd, backfilled.EndDate);

        var logCount = await verifyCtx.Adr053TripWindowBackfillLogs.CountAsync(l => l.ReservaId == reservaId);
        Assert.Equal(0, logCount);
    }
}
