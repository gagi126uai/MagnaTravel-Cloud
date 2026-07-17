using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Reservations;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Cancellation.Integration;

/// <summary>
/// ADR-048 (modelo de estados derivados, 2026-07-17): las 2 caminatas E2E que blindan los bloqueantes
/// B1 (N cancelaciones por reserva) y B2 (vía atómica) del review de arquitectura. Corren contra
/// Postgres real (no InMemory) porque lo que hay que probar es exactamente lo que InMemory no puede
/// demostrar: el índice único de "una sola cancelación ACTIVA por reserva" (que fuerza el escenario
/// realista de B1: la segunda cancelación solo puede coexistir si la primera ya cerró) y que la
/// transacción de <c>ReservaMoneyPersister.PersistAsync</c> deja plata y estado en el MISMO commit.
/// </summary>
[Trait("Category", "Integration")]
public sealed class Adr048ReservaTerminalDerivationE2ETests
    : IClassFixture<PostgresIntegrationFixture>, IAsyncLifetime
{
    private readonly PostgresIntegrationFixture _fixture;

    public Adr048ReservaTerminalDerivationE2ETests(PostgresIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => _fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    // =========================================================================
    // E2E-2 (blinda B1): N cancelaciones de la MISMA reserva + reembolso parcial.
    // La reserva NO cierra hasta que TODAS las devoluciones del operador estén saldadas.
    // =========================================================================

    [Fact]
    public async Task E2E2_DosCancelacionesDeLaMismaReserva_NoCierraHastaQueAmbasEstenSaldadas()
    {
        // Setup: una reserva ya en "Esperando reembolso del operador" (el terminal ya se derivó al
        // cancelar el último servicio — lo que este test blinda es el CIERRE, no esa derivación inicial,
        // que ya cubren Adr020LifecycleTests/ReservaTerminalDerivationTests).
        int reservaId;
        Guid supplierPublicId;
        Guid bc1PublicId, bc2PublicId;
        await using (var seedCtx = _fixture.CreateDbContext())
        {
            var (custId, supId, resId, invId) = await CancellationTestData.SeedBaseAsync(seedCtx);
            reservaId = resId;
            supplierPublicId = (await seedCtx.Suppliers.AsNoTracking().FirstAsync(s => s.Id == supId)).PublicId;

            var reserva = await seedCtx.Reservas.FirstAsync(r => r.Id == resId);
            reserva.Status = EstadoReserva.PendingOperatorRefund;
            await seedCtx.SaveChangesAsync();

            // BC1: YA CERRADO (Closed) por el lado del CLIENTE (su saldo a favor se consumió entero),
            // pero su línea de reembolso del OPERADOR sigue pendiente — ADR-033 desacopla ambos circuitos
            // a propósito. Es el escenario REAL (verificado en el código, comentario de
            // ReservaService.DeriveCancelledMoneyContextAsync) donde 2+ BookingCancellation no-abortados
            // conviven en la misma reserva: el índice único solo prohíbe DOS cancelaciones ACTIVAS
            // simultáneas, y BC1 ya no cuenta como activa (Closed).
            var bc1 = CancellationTestData.NewCancellation(custId, supId, resId, invId, BookingCancellationStatus.Closed);
            bc1.Lines.First().RefundCap = 500m;
            bc1.Lines.First().RefundStatus = BookingCancellationLineRefundStatus.PendingOperatorRefund;
            seedCtx.BookingCancellations.Add(bc1);
            await seedCtx.SaveChangesAsync();
            bc1PublicId = bc1.PublicId;

            // BC2: la cancelación ACTIVA (AwaitingOperatorRefund) de un segundo servicio de la misma
            // reserva, con su PROPIA línea de reembolso todavía pendiente.
            var bc2 = CancellationTestData.NewCancellation(custId, supId, resId, invId, BookingCancellationStatus.AwaitingOperatorRefund);
            bc2.Lines.First().RefundCap = 300m;
            bc2.Lines.First().RefundStatus = BookingCancellationLineRefundStatus.PendingOperatorRefund;
            seedCtx.BookingCancellations.Add(bc2);
            await seedCtx.SaveChangesAsync();
            bc2PublicId = bc2.PublicId;
        }

        using var scope = _fixture.BuildServiceProvider().CreateScope();
        var refundService = scope.ServiceProvider.GetRequiredService<IOperatorRefundService>();

        // El operador manda un reembolso y se imputa TODO contra BC2 (la línea de BC2 queda Settled).
        var refund1 = await refundService.RecordReceivedAsync(
            new RecordOperatorRefundRequest(supplierPublicId, 300m, "ARS", DateTime.UtcNow, "Transfer", "REF-BC2", null),
            "tester", "Tester", CancellationToken.None);
        await refundService.AllocateAsync(
            refund1.PublicId,
            new AllocateRefundRequest(bc2PublicId, 300m, new List<DeductionLineRequest>()),
            "tester", "Tester", CancellationToken.None);

        // ASSERT clave (B1): aunque la línea de BC2 ya está Settled, la reserva SIGUE "Esperando
        // reembolso" porque BC1 (Closed) todavía tiene una línea pendiente. El bug viejo (que solo
        // miraba las líneas de LA BC que disparó el evento) hubiera cerrado la reserva acá — cierre
        // prematuro con plata pendiente invisible.
        await using (var verifyCtx1 = _fixture.CreateDbContext())
        {
            var reservaAfterFirst = await verifyCtx1.Reservas.AsNoTracking().FirstAsync(r => r.Id == reservaId);
            Assert.Equal(EstadoReserva.PendingOperatorRefund, reservaAfterFirst.Status);
        }

        // Ahora se salda TAMBIÉN la deuda de BC1 (reembolso tardío de una cancelación cuyo circuito de
        // crédito al cliente ya había cerrado — circuito legítimo, ADR-033/ADR-041 los desacopla).
        var refund2 = await refundService.RecordReceivedAsync(
            new RecordOperatorRefundRequest(supplierPublicId, 500m, "ARS", DateTime.UtcNow, "Transfer", "REF-BC1", null),
            "tester", "Tester", CancellationToken.None);
        await refundService.AllocateAsync(
            refund2.PublicId,
            new AllocateRefundRequest(bc1PublicId, 500m, new List<DeductionLineRequest>()),
            "tester", "Tester", CancellationToken.None);

        // ASSERT final: recién AHORA, con TODAS las líneas de TODAS las cancelaciones saldadas, la
        // reserva cierra (nivel RESERVA, no por-BC).
        await using (var verifyCtx2 = _fixture.CreateDbContext())
        {
            var reservaFinal = await verifyCtx2.Reservas.AsNoTracking().FirstAsync(r => r.Id == reservaId);
            Assert.Equal(EstadoReserva.Cancelled, reservaFinal.Status);
        }
    }

    // =========================================================================
    // E2E-3 (blinda B2): la vía atómica — la derivación del terminal corre DENTRO de
    // ReservaMoneyPersister.PersistAsync, ANTES de su propio SaveChanges, en el MISMO commit que la plata.
    // =========================================================================

    [Fact]
    public async Task E2E3_PersistAsync_DejaPlataYEstadoEnElMismoCommit_SinInvocarElMotorAparte()
    {
        // Reserva Confirmed con UN único servicio, cancelado A MANO en la entidad (sin pasar por
        // BookingCancellationService: lo que este test quiere blindar es el enganche atómico dentro del
        // persister, no el flujo de negocio completo de cancelación).
        int reservaId;
        await using (var seedCtx = _fixture.CreateDbContext())
        {
            var customer = new Customer { FullName = "Cliente E2E-3", TaxCondition = "Consumidor Final", IsActive = true };
            seedCtx.Customers.Add(customer);
            await seedCtx.SaveChangesAsync();

            var reserva = new Reserva
            {
                NumeroReserva = "F-E2E3",
                Name = "Reserva atomicidad",
                Status = EstadoReserva.Confirmed,
                PayerId = customer.Id,
            };
            seedCtx.Reservas.Add(reserva);
            await seedCtx.SaveChangesAsync();
            reservaId = reserva.Id;

            seedCtx.HotelBookings.Add(new HotelBooking
            {
                ReservaId = reserva.Id,
                Status = "Cancelado", // el UNICO servicio ya esta anulado
                SalePrice = 80_000m,
                Currency = "ARS",
            });
            await seedCtx.SaveChangesAsync();
        }

        // Llamamos SOLO al persister — sin pasar por ReservaAutoStateService ni por
        // ReservaService.UpdateBalanceAsync — para demostrar que la derivación del terminal YA corrió
        // adentro de esta única llamada, en la MISMA SaveChangesAsync que el saldo. Si la vía atómica
        // (B2) no estuviera resuelta, el Status seguiría "Confirmed" hasta que ALGO MÁS corriera el
        // motor aparte — exactamente la corrección diferida que la regla 9 prohíbe.
        await using (var callCtx = _fixture.CreateDbContext())
        {
            await ReservaMoneyPersister.PersistAsync(callCtx, reservaId, CancellationToken.None);
        }

        // Leemos desde una conexión NUEVA (no la que hizo el cambio): si plata y estado quedaron
        // commiteados juntos, los dos se ven correctos de una.
        await using var verifyCtx = _fixture.CreateDbContext();
        var reloaded = await verifyCtx.Reservas.AsNoTracking().SingleAsync(r => r.Id == reservaId);

        Assert.Equal(EstadoReserva.Cancelled, reloaded.Status); // sin ninguna BC con reembolso pendiente -> Anulada
        Assert.Equal(0m, reloaded.ConfirmedSale); // saldo ya recalculado: el servicio cancelado no suma

        // Rastro auditable (regla 10), escrito en la MISMA operación que movió la plata.
        var logEntry = await verifyCtx.ReservaStatusChangeLogs.AsNoTracking()
            .SingleAsync(l => l.ReservaId == reservaId);
        Assert.Equal(EstadoReserva.Confirmed, logEntry.FromStatus);
        Assert.Equal(EstadoReserva.Cancelled, logEntry.ToStatus);
        Assert.Equal("system:auto-state", logEntry.ByUserId);
    }

    // =========================================================================
    // E2E-1 (blinda INV-048-02, pedido explícito del review de seguridad): la transición automática
    // al terminal NUNCA emite comprobantes (NC/ND) — solo cambia el Status y deja rastro.
    // =========================================================================

    [Fact]
    public async Task E2E1_TransicionAutomaticaAlAnularElUltimoServicio_NoEmiteNotasDeCreditoNiDebito()
    {
        // Reserva con DOS servicios vivos (sin pago al operador -> RefundCap=0, así el terminal es
        // directo "Anulada" y no queda un reembolso pendiente de por medio — eso ya lo blinda E2E-2).
        // Cancelamos el PRIMERO (queda uno vivo, la reserva NO transiciona) y DESPUÉS el ÚLTIMO (acá SÍ
        // dispara la transición automática): contamos los comprobantes de la reserva justo antes y justo
        // después de ESA transición puntual.
        int reservaId;
        Guid reservaPublicId, hotel1PublicId, hotel2PublicId;
        await using (var seedCtx = _fixture.CreateDbContext())
        {
            var (custId, supId, resId, invId) = await CancellationTestData.SeedBaseAsync(seedCtx);
            reservaId = resId;
            reservaPublicId = (await seedCtx.Reservas.AsNoTracking().FirstAsync(r => r.Id == resId)).PublicId;

            var hotel1 = new HotelBooking
            {
                ReservaId = resId, SupplierId = supId, Status = "Confirmado",
                NetCost = 100m, SalePrice = 200m, Currency = "ARS",
            };
            var hotel2 = new HotelBooking
            {
                ReservaId = resId, SupplierId = supId, Status = "Confirmado",
                NetCost = 100m, SalePrice = 200m, Currency = "ARS",
            };
            seedCtx.HotelBookings.AddRange(hotel1, hotel2);
            await seedCtx.SaveChangesAsync();
            hotel1PublicId = hotel1.PublicId;
            hotel2PublicId = hotel2.PublicId;
            // Sin SupplierPayment a propósito: el operador no cobró nada por estos servicios, así que
            // RefundCap queda en 0 y no hay reembolso pendiente en juego (no es lo que este test mide).
        }

        using var scope = _fixture.BuildServiceProvider().CreateScope();
        var cancellationService = scope.ServiceProvider.GetRequiredService<IBookingCancellationService>();

        // Cancelamos el PRIMER servicio: todavía queda el segundo vivo, la reserva sigue Confirmed.
        await cancellationService.CancelServiceAsync(
            new CancelServiceRequest(reservaPublicId, "Hotel", hotel1PublicId, "Cliente baja el primer hotel"),
            "tester", "Tester", CancellationToken.None);

        int invoiceCountBeforeTransition;
        await using (var midCtx = _fixture.CreateDbContext())
        {
            var reservaMid = await midCtx.Reservas.AsNoTracking().FirstAsync(r => r.Id == reservaId);
            Assert.Equal(EstadoReserva.Confirmed, reservaMid.Status); // todavía queda un servicio vivo
            invoiceCountBeforeTransition = await midCtx.Invoices.CountAsync(i => i.ReservaId == reservaId);
        }

        // Cancelamos el SEGUNDO (y último) servicio: ACÁ dispara la transición automática al terminal
        // (INV-048-01). Es el momento exacto que este test vigila.
        await cancellationService.CancelServiceAsync(
            new CancelServiceRequest(reservaPublicId, "Hotel", hotel2PublicId, "Cliente baja el segundo hotel"),
            "tester", "Tester", CancellationToken.None);

        await using var verifyCtx = _fixture.CreateDbContext();
        var reservaFinal = await verifyCtx.Reservas.AsNoTracking().FirstAsync(r => r.Id == reservaId);
        Assert.Equal(EstadoReserva.Cancelled, reservaFinal.Status); // transicionó sola

        // ASSERT clave (INV-048-02): la transición automática NO emitió ningún comprobante nuevo — el
        // conteo de facturas/NC/ND de la reserva es EXACTAMENTE el mismo antes y después.
        int invoiceCountAfterTransition = await verifyCtx.Invoices.CountAsync(i => i.ReservaId == reservaId);
        Assert.Equal(invoiceCountBeforeTransition, invoiceCountAfterTransition);

        // El rastro de la transición existe y es del sistema (regla 10) — pero es SOLO cambio de estado,
        // nunca un comprobante.
        var transitionLog = await verifyCtx.ReservaStatusChangeLogs.AsNoTracking()
            .Where(l => l.ReservaId == reservaId && l.ToStatus == EstadoReserva.Cancelled)
            .OrderByDescending(l => l.OccurredAt)
            .FirstAsync();
        Assert.Equal("system:auto-state", transitionLog.ByUserId);
    }

    // =========================================================================
    // Fix del review backend (B-1, 2026-07-17): en CancelServiceAsync el terminal se re-deriva DESPUÉS
    // de crear la línea de cancelación con su RefundCap (no solo antes, en ReservaMoneyPersister).
    // =========================================================================

    [Fact]
    public async Task CancelarUltimoServicioConReembolsoDeOperadorPendiente_QuedaEsperandoReembolso_NoAnuladaDirecto()
    {
        // Blinda B-1 (fix del review backend 2026-07-17): cancelar por SERVICIO el ÚLTIMO servicio vivo
        // de la reserva, cuando ESE MISMO servicio tiene reembolso de operador pendiente (la agencia ya
        // le pagó al operador -> RefundCap>0). Antes del fix, el persister (paso 3 de
        // RunCancellationUnitAsync) derivaba el terminal ANTES de que existiera la línea de cancelación
        // con su RefundCap (recién se crea en el paso 5) -> la reserva mis-derivaba a "Anulada" en vez de
        // "Esperando reembolso del operador", y nunca se auto-corregía (el callback de cierre solo actúa
        // si el Status YA es PendingOperatorRefund).
        int reservaId;
        Guid reservaPublicId, hotelPublicId, supplierPublicId;
        await using (var seedCtx = _fixture.CreateDbContext())
        {
            var (custId, supId, resId, invId) = await CancellationTestData.SeedBaseAsync(seedCtx);
            reservaId = resId;
            reservaPublicId = (await seedCtx.Reservas.AsNoTracking().FirstAsync(r => r.Id == resId)).PublicId;
            supplierPublicId = (await seedCtx.Suppliers.AsNoTracking().FirstAsync(s => s.Id == supId)).PublicId;

            var hotel = new HotelBooking
            {
                ReservaId = resId, SupplierId = supId, Status = "Confirmado", // ÚNICO servicio vivo
                NetCost = 500m, SalePrice = 800m, Currency = "ARS",
            };
            seedCtx.HotelBookings.Add(hotel);
            await seedCtx.SaveChangesAsync();
            hotelPublicId = hotel.PublicId;

            // La agencia YA le pagó al operador el costo neto del hotel -> al cancelar, el operador debe
            // devolver ese reembolso (RefundCap = 500).
            await CancellationTestData.SeedSupplierPaymentAsync(seedCtx, supId, resId, amount: 500m);
        }

        using var scope = _fixture.BuildServiceProvider().CreateScope();
        var cancellationService = scope.ServiceProvider.GetRequiredService<IBookingCancellationService>();

        await cancellationService.CancelServiceAsync(
            new CancelServiceRequest(reservaPublicId, "Hotel", hotelPublicId, "Cliente baja el único hotel"),
            "tester", "Tester", CancellationToken.None);

        // ASSERT clave (B-1): la reserva queda "Esperando reembolso del operador", NO "Anulada" directo
        // — el operador todavía debe devolver los 500 que la agencia le pagó.
        Guid bcPublicId;
        await using (var verifyCtx1 = _fixture.CreateDbContext())
        {
            var reservaAfterCancel = await verifyCtx1.Reservas.AsNoTracking().FirstAsync(r => r.Id == reservaId);
            Assert.Equal(EstadoReserva.PendingOperatorRefund, reservaAfterCancel.Status);

            bcPublicId = (await verifyCtx1.BookingCancellations.AsNoTracking()
                .FirstAsync(bc => bc.ReservaId == reservaId)).PublicId;
        }

        // El operador devuelve el reembolso completo -> RECIÉN ACÁ la reserva cierra a "Anulada".
        var refundService = scope.ServiceProvider.GetRequiredService<IOperatorRefundService>();
        var refund = await refundService.RecordReceivedAsync(
            new RecordOperatorRefundRequest(supplierPublicId, 500m, "ARS", DateTime.UtcNow, "Transfer", "REF-ULTIMO", null),
            "tester", "Tester", CancellationToken.None);
        await refundService.AllocateAsync(
            refund.PublicId,
            new AllocateRefundRequest(bcPublicId, 500m, new List<DeductionLineRequest>()),
            "tester", "Tester", CancellationToken.None);

        await using var verifyCtx2 = _fixture.CreateDbContext();
        var reservaFinal = await verifyCtx2.Reservas.AsNoTracking().FirstAsync(r => r.Id == reservaId);
        Assert.Equal(EstadoReserva.Cancelled, reservaFinal.Status);
    }
}
