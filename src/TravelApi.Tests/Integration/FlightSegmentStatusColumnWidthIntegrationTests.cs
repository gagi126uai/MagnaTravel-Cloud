using Microsoft.EntityFrameworkCore;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Integration;

/// <summary>
/// Regresion 2026-07-23 (bug real confirmado en PROD con logs): dar de alta un vuelo tiraba 500
/// SIEMPRE. Causa raiz: <c>AppDbContext</c> mapeaba <c>FlightSegment.Status</c> con
/// <c>HasMaxLength(2)</c> por un error de tipeo (todos los demas servicios usan 50). Cuando la
/// reserva esta en estado Presupuesto, <c>BookingService.CreateFlightAsync</c> fuerza
/// <c>flight.Status = "Solicitado"</c> (10 caracteres) — Postgres rechaza el INSERT con
/// <c>22001: value too long for type character varying(2)</c>, EF lo envuelve en
/// <see cref="DbUpdateException"/> y el controller lo mapea a 500.
///
/// <para><b>Por que este test necesita Postgres real (no InMemory)</b>: el proveedor InMemory de EF
/// Core NO valida <c>HasMaxLength</c> — con InMemory este test hubiera pasado ANTES del fix y
/// nunca hubiera detectado el bug. Solo un motor SQL real (Postgres, igual que produccion) aplica
/// el limite de la columna. Por eso corre bajo <see cref="PostgresIntegrationFixture"/> (Testcontainers)
/// y NO bajo <c>CustomWebApplicationFactory</c> (que siempre usa InMemory).</para>
///
/// <para>Complementa <c>FlightSegmentsControllerCreateTests</c> (HTTP, InMemory): ese test pinea el
/// pipeline completo (auth + binding + mapping) pero no puede pinear el limite de columna. Este test
/// pinea la columna. Los dos juntos cubren el bug de punta a punta.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class FlightSegmentStatusColumnWidthIntegrationTests : IClassFixture<PostgresIntegrationFixture>, IAsyncLifetime
{
    private readonly PostgresIntegrationFixture _fixture;

    public FlightSegmentStatusColumnWidthIntegrationTests(PostgresIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => _fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task SaveFlightSegment_WithSolicitadoStatus_DoesNotThrow_PostgresColumnFitsTheFullWord()
    {
        // ARRANGE: mismo escenario que dispara el bug real — una reserva en Presupuesto (Budget),
        // que es el estado en el que BookingService.CreateFlightAsync fuerza
        // flight.Status = "Solicitado" (ver ReservaCapacityRules.ShouldForceSolicitadoStatusAsync).
        await using var ctx = _fixture.CreateDbContext();

        var supplier = new Supplier { Name = "Aerolineas Test", IsActive = true };
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = $"F-FLT-{Guid.NewGuid():N}"[..14],
            Name = "Reserva con vuelo en presupuesto",
            Status = EstadoReserva.Budget,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var flight = new FlightSegment
        {
            ReservaId = reserva.Id,
            SupplierId = supplier.Id,
            DepartureTime = DateTime.UtcNow.AddDays(10),
            SalePrice = 500m,
            NetCost = 400m,
            // El literal exacto que BookingService escribe cuando la reserva esta en Presupuesto.
            // Antes del fix, esto rebotaba en Postgres con 22001 (value too long for varchar(2)).
            Status = "Solicitado",
        };
        ctx.FlightSegments.Add(flight);

        // ACT + ASSERT: el SaveChanges NO debe tirar DbUpdateException. Si alguien vuelve a
        // angostar la columna (a mano o via una migracion futura), este test lo va a agarrar
        // porque corre contra Postgres real, no InMemory.
        await ctx.SaveChangesAsync();

        var reloaded = await ctx.FlightSegments
            .AsNoTracking()
            .SingleAsync(f => f.Id == flight.Id);
        Assert.Equal("Solicitado", reloaded.Status);
    }
}
