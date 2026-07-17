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
/// ADR-048 (modelo de estados derivados, 2026-07-17): las caminatas E2E que blindan los bloqueantes
/// B1 (N cancelaciones por reserva) y B2 (vía atómica) del review de arquitectura, más el fix real de
/// B-1 (re-derivar el terminal DESPUÉS de crear la línea de cancelación con su RefundCap). Corren contra
/// Postgres real (no InMemory) porque lo que hay que probar es exactamente lo que InMemory no puede
/// demostrar: el índice único de "una sola cancelación ACTIVA por reserva", las guardas INV-093/INV-126
/// de <c>OperatorRefundService</c>, y que la transacción de <c>ReservaMoneyPersister.PersistAsync</c>
/// deja plata y estado en el MISMO commit.
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
    // E2E-2 (blinda B1): N cancelaciones de la MISMA reserva. La reserva NO cierra prematuramente
    // mientras CUALQUIERA de ellas todavía tenga una devolución del operador pendiente.
    // =========================================================================

    [Fact]
    public async Task E2E2_DosCancelacionesDeLaMismaReserva_NoCierraMientrasUnaSigaPendiente()
    {
        // Setup: una reserva ya en "Esperando reembolso del operador" (el terminal ya se derivó al
        // cancelar el último servicio — lo que este test blinda es el CIERRE, no esa derivación inicial,
        // que ya cubren Adr020LifecycleTests/ReservaTerminalDerivationTests).
        int reservaId;
        Guid supplierPublicId;
        Guid bc2PublicId;
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
            //
            // OJO (hallazgo del CI, 2026-07-17): una vez que un BC llega a Closed, YA NO puede recibir
            // más imputaciones — OperatorRefundService.EnsureBookingCancellationCanReceiveOperatorRefund
            // (INV-093) exige {AwaitingOperatorRefund, ClientCreditApplied}. Esto es una representación
            // REAL y PERMANENTE del riesgo R1 ("stranding") que el review de seguridad ya aceptó como no
            // bloqueante (mono-usuario, mentira en dirección conservadora, sin pérdida de plata): una vez
            // que BC1 cerró por el lado del cliente sin que el operador pagara, ESA deuda queda varada
            // para siempre salvo una reconciliación administrativa (fuera de alcance de esta tanda). Por
            // eso este test NO intenta "saldar BC1" — sería simular algo que el sistema de hoy no permite
            // hacer por la vía pública. Lo que SÍ hay que probar (y es la esencia de B1) es que la reserva
            // NUNCA cierra mientras esa deuda siga ahí, aunque OTRA cancelación de la misma reserva (BC2)
            // se salde por completo.
            var bc1 = CancellationTestData.NewCancellation(custId, supId, resId, invId, BookingCancellationStatus.Closed);
            bc1.Lines.First().RefundCap = 500m;
            bc1.Lines.First().RefundStatus = BookingCancellationLineRefundStatus.PendingOperatorRefund;
            seedCtx.BookingCancellations.Add(bc1);
            await seedCtx.SaveChangesAsync();

            // BC2: la cancelación ACTIVA (AwaitingOperatorRefund) de un segundo servicio de la misma
            // reserva, con su PROPIA línea de reembolso todavía pendiente. Esta SÍ está en un estado
            // válido para recibir una imputación real via OperatorRefundService.AllocateAsync.
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
        await using var verifyCtx = _fixture.CreateDbContext();
        var reservaFinal = await verifyCtx.Reservas.AsNoTracking().FirstAsync(r => r.Id == reservaId);
        Assert.Equal(EstadoReserva.PendingOperatorRefund, reservaFinal.Status);
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
            var supplier = new Supplier { Name = "Operador E2E-3", IsActive = true, TaxCondition = "IVA_RESP_INSCRIPTO" };
            seedCtx.Customers.Add(customer);
            seedCtx.Suppliers.Add(supplier);
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

            // FK real a Suppliers: sin esto Postgres rechaza el INSERT (InMemory no valida FKs, por eso
            // este bug no se vio hasta que el CI corrió contra Postgres real).
            seedCtx.HotelBookings.Add(new HotelBooking
            {
                ReservaId = reserva.Id,
                SupplierId = supplier.Id,
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
        // "Esperando reembolso del operador".
        int reservaId;
        Guid reservaPublicId, hotelPublicId, supplierPublicId;
        int originalInvoiceId;
        await using (var seedCtx = _fixture.CreateDbContext())
        {
            var (custId, supId, resId, invId) = await CancellationTestData.SeedBaseAsync(seedCtx);
            reservaId = resId;
            originalInvoiceId = invId;
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

        var provider = _fixture.BuildServiceProvider();

        // ---------- Paso 1: cancelar el servicio (crea la BC en Drafted + línea con RefundCap=500) ----------
        using (var scope = provider.CreateScope())
        {
            var cancellationService = scope.ServiceProvider.GetRequiredService<IBookingCancellationService>();
            await cancellationService.CancelServiceAsync(
                new CancelServiceRequest(reservaPublicId, "Hotel", hotelPublicId, "Cliente baja el único hotel"),
                "tester", "Tester", CancellationToken.None);
        }

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

        // ---------- Paso 2: avanzar la BC por el camino LEGÍTIMO hasta AwaitingOperatorRefund ----------
        // OperatorRefundService.AllocateAsync exige (INV-093) que la BC esté en {AwaitingOperatorRefund,
        // ClientCreditApplied} — recién creada por CancelServiceAsync queda en Drafted. El camino real es
        // Confirm (emite/encola la NC) + el callback post-CAE (en producción lo dispara el job de AFIP al
        // recibir el CAE real; acá, igual que CancellationFlowE2ETests, se simula creando la NC a mano y
        // llamando directo al bridge que ese job invocaría).
        using (var scope = provider.CreateScope())
        {
            var cancellationService = scope.ServiceProvider.GetRequiredService<IBookingCancellationService>();
            var confirmed = await cancellationService.ConfirmAsync(
                bcPublicId,
                BuildValidConfirm(),
                userId: "tester", userName: "Tester",
                requesterIsAdmin: false, ct: CancellationToken.None);
            Assert.Equal("AwaitingFiscalConfirmation", confirmed.Status);
        }

        int creditNoteInvoiceId = await CreateNcInvoiceAsync(
            reservaId, originalInvoiceId: originalInvoiceId, tipoNc: 3, numeroComprobante: 2);

        using (var scope = provider.CreateScope())
        {
            var bridge = scope.ServiceProvider.GetRequiredService<IInvoiceAnnulmentBcBridge>();
            await bridge.OnArcaSucceededAsync(
                originatingInvoiceId: originalInvoiceId,
                creditNoteInvoiceId: creditNoteInvoiceId,
                ct: CancellationToken.None);
        }

        await using (var verifyCtx2 = _fixture.CreateDbContext())
        {
            var bc = await verifyCtx2.BookingCancellations.AsNoTracking().FirstAsync(b => b.PublicId == bcPublicId);
            Assert.Equal(BookingCancellationStatus.AwaitingOperatorRefund, bc.Status);
            // La reserva sigue esperando el reembolso — el callback post-CAE no la mueve (ya estaba ahí).
            var reservaStillPending = await verifyCtx2.Reservas.AsNoTracking().FirstAsync(r => r.Id == reservaId);
            Assert.Equal(EstadoReserva.PendingOperatorRefund, reservaStillPending.Status);
        }

        // ---------- Paso 3: el operador devuelve el reembolso completo -> RECIÉN ACÁ la reserva cierra ----------
        using (var scope = provider.CreateScope())
        {
            var refundService = scope.ServiceProvider.GetRequiredService<IOperatorRefundService>();
            var refund = await refundService.RecordReceivedAsync(
                new RecordOperatorRefundRequest(supplierPublicId, 500m, "ARS", DateTime.UtcNow, "Transfer", "REF-ULTIMO", null),
                "tester", "Tester", CancellationToken.None);
            await refundService.AllocateAsync(
                refund.PublicId,
                new AllocateRefundRequest(bcPublicId, 500m, new List<DeductionLineRequest>()),
                "tester", "Tester", CancellationToken.None);
        }

        await using var verifyCtxFinal = _fixture.CreateDbContext();
        var reservaFinal = await verifyCtxFinal.Reservas.AsNoTracking().FirstAsync(r => r.Id == reservaId);
        Assert.Equal(EstadoReserva.Cancelled, reservaFinal.Status);
    }

    // =========================================================================
    // Helpers (mismo patrón que CancellationFlowE2ETests, para simular "AFIP devolvió CAE" sin Hangfire).
    // =========================================================================

    private static ConfirmCancellationRequest BuildValidConfirm(
        string agencyCondition = "Monotributo",
        string supplierCondition = "IVA_RESP_INSCRIPTO",
        string customerCondition = "Consumidor Final")
        => new(
            SnapshotData: new FiscalSnapshotData(
                CurrencyAtEvent: "ARS",
                ExchangeRateAtOriginalInvoice: 1m,
                Source: ExchangeRateSource.Manual,
                ManualJustification: "Test E2E B-1",
                AgencyTaxConditionAtEvent: agencyCondition,
                SupplierTaxConditionAtEvent: supplierCondition,
                CustomerTaxConditionAtEvent: customerCondition),
            IsAdminOverride: false,
            OverrideReason: null,
            ApprovalRequestPublicId: null);

    /// <summary>
    /// Crea una NC (Invoice tipo 3/8/13 con CAE) linkeada a la factura original y la persiste. Simulación
    /// del side effect que en producción ejecuta AfipService cuando AFIP devuelve CAE para una anulación.
    /// </summary>
    private async Task<int> CreateNcInvoiceAsync(
        int reservaId, int originalInvoiceId, int tipoNc, int numeroComprobante)
    {
        await using var ctx = _fixture.CreateDbContext();
        var nc = new Invoice
        {
            TipoComprobante = tipoNc,
            PuntoDeVenta = 1,
            NumeroComprobante = numeroComprobante,
            CAE = "73000000000099",
            Resultado = "A",
            ImporteTotal = 1000m,
            ImporteNeto = 826.45m,
            ImporteIva = 173.55m,
            ReservaId = reservaId,
            OriginalInvoiceId = originalInvoiceId,
            CreatedAt = DateTime.UtcNow,
        };
        ctx.Invoices.Add(nc);
        await ctx.SaveChangesAsync();
        return nc.Id;
    }
}
