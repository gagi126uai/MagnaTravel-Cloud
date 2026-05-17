using Microsoft.EntityFrameworkCore;
using Npgsql;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Exceptions;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Cancellation.Integration;

/// <summary>
/// FC1 (review BR3 / BR4, 2026-05-14): valida el comportamiento del
/// <see cref="Infrastructure.Persistence.BusinessInvariantInterceptor"/> en casos
/// donde la BD lanza errores que NO son CHECK violations.
///
/// El interceptor solo debe traducir <c>SqlState='23514'</c> (check_violation).
/// Cualquier otro SqlState (unique 23505, FK 23503, etc.) debe propagarse intacto
/// para que el caller pueda manejarlos diferente — por ejemplo, una collision
/// UNIQUE en <c>OriginatingInvoiceId</c> en el flujo de reapertura de cancelaciones
/// se traduce a un 409 con mensaje distinto.
/// </summary>
[Trait("Category", "Integration")]
public sealed class InterceptorBehaviorTests : IClassFixture<PostgresIntegrationFixture>, IAsyncLifetime
{
    private readonly PostgresIntegrationFixture _fixture;

    public InterceptorBehaviorTests(PostgresIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => _fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task OriginatingInvoiceId_DuplicateAcrossBookingCancellations_ReRaisesUniqueViolation_23505()
    {
        // ARRANGE: dos BookingCancellation que apuntan a la misma OriginatingInvoiceId
        // violan el UNIQUE INDEX IX_BookingCancellations_OriginatingInvoiceId
        // (review BR4: una factura A no puede tener 2 NCs huerfanas).
        //
        // Este test consolida la validacion funcional del UNIQUE (10.12 del plan)
        // con la validacion de comportamiento del interceptor (10.11):
        //  - El UNIQUE existe y se respeta.
        //  - El interceptor NO transforma el 23505 — la excepcion sigue siendo
        //    DbUpdateException (con PostgresException SqlState 23505 dentro).
        //  - El test es UN escenario porque cubrir 11+12 separados seria duplicacion.
        await using var ctx = _fixture.CreateDbContext();
        var (custId, supId, _, invId) = await CancellationTestData.SeedBaseAsync(ctx);

        // Necesitamos DOS reservas distintas para evitar que el UNIQUE de
        // ReservaId (INV-081) dispare antes que el de OriginatingInvoiceId.
        // Cada BC tiene su Reserva, pero ambas apuntan a la MISMA factura original.
        var reserva2 = new Reserva
        {
            NumeroReserva = $"F-INT-{Guid.NewGuid().ToString("N")[..8]}",
            Name = "Segunda reserva",
            Status = EstadoReserva.Confirmed,
            PayerId = custId,
        };
        ctx.Reservas.Add(reserva2);
        await ctx.SaveChangesAsync();

        var reserva1Id = (await ctx.Reservas.FirstAsync(r => r.Id != reserva2.Id)).Id;

        var bc1 = CancellationTestData.NewCancellation(custId, supId, reserva1Id, invId);
        ctx.BookingCancellations.Add(bc1);
        await ctx.SaveChangesAsync();

        var bc2 = CancellationTestData.NewCancellation(custId, supId, reserva2.Id, invId);
        ctx.BookingCancellations.Add(bc2);

        // ACT + ASSERT: 23505 viaja en el InnerException como PostgresException.
        // El interceptor NO debe transformarlo a BusinessInvariantViolationException.
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());

        // Buscar el PostgresException en la cadena para verificar el SqlState.
        // EF Core envuelve la excepcion Npgsql en DbUpdateException; el inner es
        // tipicamente la PostgresException directa, pero usamos una busqueda
        // defensiva por si EF agrega un nivel en versiones futuras.
        Exception? current = ex.InnerException;
        PostgresException? pgEx = null;
        for (var depth = 0; depth < 5 && current is not null; depth++)
        {
            if (current is PostgresException pg)
            {
                pgEx = pg;
                break;
            }
            current = current.InnerException;
        }

        Assert.NotNull(pgEx);
        // 23505 = unique_violation (PostgreSQL Error Codes Appendix A). Distinto
        // de 23514 = check_violation. Si el interceptor tradujera erroneamente,
        // habriamos recibido BusinessInvariantViolationException y este Assert
        // nunca correria.
        Assert.Equal("23505", pgEx!.SqlState);

        // Verificacion adicional: el ConstraintName menciona el indice del UNIQUE.
        // Tolerante a mayusculas/minusculas porque Postgres normaliza algunos.
        Assert.Contains("OriginatingInvoiceId", pgEx.ConstraintName ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BusinessInvariantViolationException_PropagatesUnchanged_WhenAlreadyMappedByInterceptor()
    {
        // ARRANGE: este test documenta una propiedad del interceptor — su unica
        // entrada es <see cref="Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor.SaveChangesFailedAsync"/>.
        // Una excepcion BusinessInvariantViolationException lanzada por la BD se
        // captura, traduce y relanza UNA sola vez; al re-tirarse no vuelve a
        // pasar por el interceptor (EF no re-engancha la nueva excepcion).
        //
        // Validacion practica: cuando el CHECK dispara, la excepcion que llega al
        // caller es BusinessInvariantViolationException con InnerException
        // conteniendo el PostgresException original — sin doble envoltura.
        await using var ctx = _fixture.CreateDbContext();
        var (_, supId, _, _) = await CancellationTestData.SeedBaseAsync(ctx);

        var refund = new OperatorRefundReceived
        {
            SupplierId = supId,
            ReceivedAt = DateTime.UtcNow,
            ReceivedAmount = 100m,
            AllocatedAmount = 200m, // viola chk_OperatorRefundsReceived_allocated_not_exceeds
            Method = "Transfer",
            Currency = "ARS",
            ExchangeRateAtReceipt = 1m,
            ReceivedByUserId = "tester",
            ReceivedByUserName = "Tester",
        };
        ctx.OperatorRefundReceived.Add(refund);

        // ACT.
        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(
            () => ctx.SaveChangesAsync());

        // ASSERT: una sola envoltura. La InnerException existe (es la DbUpdateException
        // original) y NO es otra BusinessInvariantViolationException — eso indicaria
        // doble mapeo.
        Assert.NotNull(ex.InnerException);
        Assert.IsNotType<BusinessInvariantViolationException>(ex.InnerException);
        Assert.Equal("INV-084", ex.InvariantCode);
    }
}
