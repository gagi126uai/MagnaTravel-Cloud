using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using TravelApi.Application.DTOs;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Cuenta del operador (2026-07-03, spec docs/ux/2026-07-03-cuenta-operador-reembolsos-multa.md): desglose
/// "pagado − multa = te devuelven" del read-model de reembolsos pendientes. Cubre:
/// <list type="bullet">
///   <item>Invariante <c>EstimatedAmount == PaidToOperator − PenaltyRetained − AmountReceived</c> (incl. residuo).</item>
///   <item>Los 4 casos de <c>ZeroRefundReason</c> (NothingPaidToOperator / PenaltyCoversAll / FullyRefunded / null).</item>
///   <item>Enmascarado con y sin permiso (montos a 0; motivo + flag siguen visibles).</item>
///   <item><c>PenaltyPendingConfirmation</c> por estado de la multa del BC.</item>
///   <item><b>RESTOS</b>: <c>DeriveRowStatus</c>, <c>CanRegisterRefund</c> y CUADRE POR CONSTRUCCION de la
///     solapa "Reembolsos" contra el "me tiene que devolver" del extracto.</item>
/// </list>
/// </summary>
public class Adr042OperatorRefundDesgloseTests
{
    private static BookingCancellationLine Line(decimal refundCap, decimal? penaltyAmount, decimal received = 0m, string currency = "USD")
        => new()
        {
            SupplierId = 1,
            ServiceTable = CancellableServiceTable.Hotel,
            ServiceId = 1,
            Scope = BookingCancellationLineScope.Full,
            Currency = currency,
            RefundCap = refundCap,
            PenaltyAmount = penaltyAmount,
            // ADR-044 T2 Addendum: eje CAJA. Este helper representa el camino legacy simple (Fee+Retenida
            // confirmada): coincide con PenaltyAmount (misma regla del backfill T2c).
            RetainedDeductionAmount = penaltyAmount ?? 0m,
            ReceivedRefundAmount = received,
        };

    // ===== Invariante estimado = pagado − multa =====

    [Fact]
    public void Invariante_estimado_igual_pagado_menos_multa()
    {
        // capBeforePenalty 500, multa 100 -> RefundCap 400 (=500-100), PenaltyAmount 100. Recibido 0.
        var dto = OperatorRefundReadModelService.BuildEstimatedForCurrency(
            "USD", new List<BookingCancellationLine> { Line(refundCap: 400m, penaltyAmount: 100m) }, canSeeCost: true);

        Assert.Equal(500m, dto.PaidToOperator);
        Assert.Equal(100m, dto.PenaltyRetained);
        Assert.Equal(0m, dto.AmountReceived);
        Assert.Equal(400m, dto.EstimatedAmount);
        // El invariante que la pantalla necesita para cuadrar "Pagaste − Multa − Ya te devolvió = te devuelven".
        Assert.Equal(dto.PaidToOperator - dto.PenaltyRetained - dto.AmountReceived, dto.EstimatedAmount);
        Assert.Null(dto.ZeroRefundReason);
    }

    [Fact]
    public void Invariante_varias_lineas_misma_moneda_agrega()
    {
        var dto = OperatorRefundReadModelService.BuildEstimatedForCurrency(
            "USD",
            new List<BookingCancellationLine> { Line(300m, 50m), Line(200m, 0m) },
            canSeeCost: true);

        Assert.Equal(550m, dto.PaidToOperator);   // (300+50) + (200+0)
        Assert.Equal(50m, dto.PenaltyRetained);
        Assert.Equal(0m, dto.AmountReceived);
        Assert.Equal(500m, dto.EstimatedAmount);   // 300 + 200
        Assert.Equal(dto.PaidToOperator - dto.PenaltyRetained - dto.AmountReceived, dto.EstimatedAmount);
    }

    // ===== ZeroRefundReason: los 3 casos =====

    [Fact]
    public void ZeroReason_NothingPaidToOperator_cuando_no_se_pago_nada()
    {
        var dto = OperatorRefundReadModelService.BuildEstimatedForCurrency(
            "USD", new List<BookingCancellationLine> { Line(refundCap: 0m, penaltyAmount: 0m) }, canSeeCost: true);

        Assert.Equal(0m, dto.EstimatedAmount);
        Assert.Equal(0m, dto.PaidToOperator);
        Assert.Equal(nameof(OperatorRefundZeroReason.NothingPaidToOperator), dto.ZeroRefundReason);
    }

    [Fact]
    public void ZeroReason_PenaltyCoversAll_cuando_la_multa_se_comio_todo()
    {
        // La multa se topea al cap: RefundCap 0, PenaltyAmount 300 (== capBeforePenalty). Se pago pero no vuelve nada.
        var dto = OperatorRefundReadModelService.BuildEstimatedForCurrency(
            "USD", new List<BookingCancellationLine> { Line(refundCap: 0m, penaltyAmount: 300m) }, canSeeCost: true);

        Assert.Equal(0m, dto.EstimatedAmount);
        Assert.Equal(300m, dto.PaidToOperator);
        Assert.Equal(300m, dto.PenaltyRetained);
        Assert.Equal(nameof(OperatorRefundZeroReason.PenaltyCoversAll), dto.ZeroRefundReason);
    }

    [Fact]
    public void Residuo_reembolso_parcial_cierra_el_invariante_con_recibido()
    {
        // capBeforePenalty 500, multa 100 -> RefundCap 400. El operador ya devolvio 150 -> quedan 250.
        var dto = OperatorRefundReadModelService.BuildEstimatedForCurrency(
            "USD", new List<BookingCancellationLine> { Line(refundCap: 400m, penaltyAmount: 100m, received: 150m) },
            canSeeCost: true);

        Assert.Equal(500m, dto.PaidToOperator);
        Assert.Equal(100m, dto.PenaltyRetained);
        Assert.Equal(150m, dto.AmountReceived);
        Assert.Equal(250m, dto.EstimatedAmount);   // 400 - 150
        // El cierre exacto vale TAMBIEN con reembolso parcial: Pagado − Multa − Recibido = Estimado.
        Assert.Equal(dto.PaidToOperator - dto.PenaltyRetained - dto.AmountReceived, dto.EstimatedAmount);
        Assert.Null(dto.ZeroRefundReason);
    }

    [Fact]
    public void ZeroReason_FullyRefunded_cuando_el_operador_devolvio_todo()
    {
        // RefundCap 400, ya recibido 400 -> no queda residuo, pero SI se pago y devolvio (no es multa-cubre-todo).
        var dto = OperatorRefundReadModelService.BuildEstimatedForCurrency(
            "USD", new List<BookingCancellationLine> { Line(refundCap: 400m, penaltyAmount: 100m, received: 400m) },
            canSeeCost: true);

        Assert.Equal(0m, dto.EstimatedAmount);
        Assert.Equal(500m, dto.PaidToOperator);
        Assert.Equal(400m, dto.AmountReceived);
        Assert.Equal(nameof(OperatorRefundZeroReason.FullyRefunded), dto.ZeroRefundReason);
    }

    [Fact]
    public void SobreReembolso_topea_el_recibido_mostrado_y_el_invariante_cierra_igual()
    {
        // Review backend 2026-07-03: DistributeReceivedRefundToOperatorLines deja que la ULTIMA linea absorba
        // reembolso por ENCIMA de su cap (para no perder plata recibida). El crudo (600) superaria a lo pagado
        // (500) y la cuenta "Pagaste − Multa − Ya te devolvió" quedaria visiblemente rota. El DTO topea el
        // recibido mostrado por linea al cap; el crudo sigue decidiendo el motivo del $0 (FullyRefunded).
        var dto = OperatorRefundReadModelService.BuildEstimatedForCurrency(
            "USD", new List<BookingCancellationLine> { Line(refundCap: 400m, penaltyAmount: 100m, received: 600m) },
            canSeeCost: true);

        Assert.Equal(500m, dto.PaidToOperator);
        Assert.Equal(100m, dto.PenaltyRetained);
        Assert.Equal(400m, dto.AmountReceived);   // topeado al cap, NO el crudo 600
        Assert.Equal(0m, dto.EstimatedAmount);
        Assert.Equal(dto.PaidToOperator - dto.PenaltyRetained - dto.AmountReceived, dto.EstimatedAmount);
        Assert.Equal(nameof(OperatorRefundZeroReason.FullyRefunded), dto.ZeroRefundReason);
    }

    [Fact]
    public void SobreReembolso_multilinea_cierra_por_linea_no_por_suma()
    {
        // Dos lineas: una sobre-reembolsada (cap 300, recibio 500) y otra a medias (cap 200, recibio 50).
        // El tope es POR LINEA (min por linea), no min de sumas: mostrado = 300 + 50 = 350, estimado = 0 + 150.
        var dto = OperatorRefundReadModelService.BuildEstimatedForCurrency(
            "USD",
            new List<BookingCancellationLine> { Line(300m, 0m, received: 500m), Line(200m, 0m, received: 50m) },
            canSeeCost: true);

        Assert.Equal(500m, dto.PaidToOperator);
        Assert.Equal(350m, dto.AmountReceived);
        Assert.Equal(150m, dto.EstimatedAmount);
        Assert.Equal(dto.PaidToOperator - dto.PenaltyRetained - dto.AmountReceived, dto.EstimatedAmount);
        Assert.Null(dto.ZeroRefundReason);
    }

    [Fact]
    public void ZeroReason_null_cuando_hay_algo_para_devolver()
    {
        var dto = OperatorRefundReadModelService.BuildEstimatedForCurrency(
            "USD", new List<BookingCancellationLine> { Line(refundCap: 400m, penaltyAmount: 100m) }, canSeeCost: true);

        Assert.True(dto.EstimatedAmount > 0m);
        Assert.Null(dto.ZeroRefundReason);
    }

    // ===== Enmascarado (P6): montos a 0, motivo/flag siguen visibles =====

    [Fact]
    public void Enmascarado_sin_permiso_oculta_montos_y_tambien_el_motivo()
    {
        // Security review (2026-07-03): sin cobranzas.see_cost los montos van 0 Y el motivo cualitativo se enmascara
        // (null): "PenaltyCoversAll" revelaria que hubo multa >= lo pagado. Enmascarado completo server-side.
        var dto = OperatorRefundReadModelService.BuildEstimatedForCurrency(
            "USD", new List<BookingCancellationLine> { Line(refundCap: 0m, penaltyAmount: 300m) }, canSeeCost: false);

        Assert.Equal(0m, dto.EstimatedAmount);
        Assert.Equal(0m, dto.PaidToOperator);
        Assert.Equal(0m, dto.PenaltyRetained);
        Assert.Equal(0m, dto.AmountReceived);
        Assert.Null(dto.ZeroRefundReason);
    }

    [Fact]
    public void ConPermiso_conserva_el_motivo_del_cero()
    {
        // Con permiso el mismo caso SI expone el motivo (no hay perdida funcional para quien puede ver costos).
        var dto = OperatorRefundReadModelService.BuildEstimatedForCurrency(
            "USD", new List<BookingCancellationLine> { Line(refundCap: 0m, penaltyAmount: 300m) }, canSeeCost: true);

        Assert.Equal(nameof(OperatorRefundZeroReason.PenaltyCoversAll), dto.ZeroRefundReason);
    }

    [Fact]
    public void ConPermiso_muestra_montos_reales()
    {
        var dto = OperatorRefundReadModelService.BuildEstimatedForCurrency(
            "USD", new List<BookingCancellationLine> { Line(refundCap: 400m, penaltyAmount: 100m) }, canSeeCost: true);

        Assert.Equal(500m, dto.PaidToOperator);
        Assert.Equal(100m, dto.PenaltyRetained);
        Assert.Equal(400m, dto.EstimatedAmount);
    }

    // ===== PenaltyPendingConfirmation (via el servicio, depende de bc.PenaltyStatus) =====

    private static AppDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"refund-desglose-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static IHttpContextAccessor AdminAccessor()
        => new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Role, "Admin") }, "test")),
            },
        };

    private static async Task<int> SeedBcWithPenaltyStatusAsync(AppDbContext ctx, PenaltyStatus penaltyStatus, string numero)
    {
        var customer = new Customer { FullName = "Cliente", IsActive = true };
        var supplier = new Supplier { Name = "Operador", IsActive = true };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva { NumeroReserva = numero, Name = numero, PayerId = customer.Id, Status = EstadoReserva.Cancelled };
        var invoice = new Invoice { TipoComprobante = 11, Resultado = "A" };
        ctx.Reservas.Add(reserva);
        ctx.Invoices.Add(invoice);
        await ctx.SaveChangesAsync();

        var bc = new BookingCancellation
        {
            ReservaId = reserva.Id, CustomerId = customer.Id, SupplierId = supplier.Id,
            OriginatingInvoiceId = invoice.Id, Status = BookingCancellationStatus.AwaitingOperatorRefund,
            Reason = "rm", DraftedByUserId = "v", OperatorRefundDueBy = DateTime.UtcNow.AddDays(30),
            PenaltyStatus = penaltyStatus,
        };
        ctx.BookingCancellations.Add(bc);
        await ctx.SaveChangesAsync();

        ctx.Set<BookingCancellationLine>().Add(new BookingCancellationLine
        {
            BookingCancellationId = bc.Id, SupplierId = supplier.Id, ServiceTable = CancellableServiceTable.Hotel,
            ServiceId = 1, Scope = BookingCancellationLineScope.Full, Currency = "USD",
            RefundCap = 400m, ReceivedRefundAmount = 0m,
        });
        await ctx.SaveChangesAsync();
        return supplier.Id;
    }

    [Theory]
    [InlineData(PenaltyStatus.Estimated, true)]   // sin confirmar -> "Falta confirmar la multa"
    [InlineData(PenaltyStatus.Confirmed, false)]  // ya confirmada
    [InlineData(PenaltyStatus.Waived, false)]     // cerrada sin multa
    public async Task PenaltyPendingConfirmation_reflejaEstadoDeLaMulta(PenaltyStatus status, bool expectedPending)
    {
        await using var ctx = NewDbContext();
        var supplierId = await SeedBcWithPenaltyStatusAsync(ctx, status, "R-PEN");

        var svc = new OperatorRefundReadModelService(ctx, AdminAccessor(), permissionResolver: null);
        var items = await svc.GetSupplierPendingRefundsAsync(supplierId, CancellationToken.None);

        var item = Assert.Single(items);
        Assert.Equal(expectedPending, item.PenaltyPendingConfirmation);
    }

    [Fact]
    public async Task ItemEnmascarado_conserva_flag_de_multa_pendiente()
    {
        // Sin permiso de ver costos: los montos van 0 pero PenaltyPendingConfirmation (no es monto) sigue visible.
        await using var ctx = NewDbContext();
        var supplierId = await SeedBcWithPenaltyStatusAsync(ctx, PenaltyStatus.Estimated, "R-PEN-MASK");

        var svc = new OperatorRefundReadModelService(ctx, httpContextAccessor: null, permissionResolver: null);
        var items = await svc.GetSupplierPendingRefundsAsync(supplierId, CancellationToken.None);

        var item = Assert.Single(items);
        Assert.True(item.AmountsMasked);
        Assert.True(item.PenaltyPendingConfirmation);
        Assert.All(item.EstimatedRefundsByCurrency, e =>
        {
            Assert.Equal(0m, e.EstimatedAmount);
            Assert.Equal(0m, e.PaidToOperator);
            Assert.Equal(0m, e.PenaltyRetained);
            Assert.Equal(0m, e.AmountReceived);
        });
    }

    // ===== RESTOS: rotulo de fila (RowStatus) =====

    [Theory]
    [InlineData(BookingCancellationStatus.AbandonedByOperator, 0, OperatorRefundRowStatus.Abandoned)]
    [InlineData(BookingCancellationStatus.AbandonedByOperator, 50, OperatorRefundRowStatus.Abandoned)]
    [InlineData(BookingCancellationStatus.Closed, 100, OperatorRefundRowStatus.ClosedWithResidue)]
    [InlineData(BookingCancellationStatus.Closed, 0, OperatorRefundRowStatus.ClosedWithResidue)]
    [InlineData(BookingCancellationStatus.ClientCreditApplied, 100, OperatorRefundRowStatus.PartiallyRefunded)]
    [InlineData(BookingCancellationStatus.AwaitingOperatorRefund, 0, OperatorRefundRowStatus.AwaitingRefund)]
    [InlineData(BookingCancellationStatus.AwaitingFiscalConfirmation, 0, OperatorRefundRowStatus.InProcess)]
    [InlineData(BookingCancellationStatus.ArcaRejected, 0, OperatorRefundRowStatus.InProcess)]
    public void DeriveRowStatus_rotula_segun_estado_y_recibido(
        BookingCancellationStatus status, decimal received, OperatorRefundRowStatus expected)
        => Assert.Equal(expected, OperatorRefundReadModelService.DeriveRowStatus(status, received));

    // ===== RESTOS: cuadre POR CONSTRUCCION con el extracto + estados de fila =====

    /// <summary>
    /// Un operador con las 3 clases de fila que la solapa "Reembolsos" ahora muestra: pendiente completo
    /// (AwaitingOperatorRefund), residuo parcial (ClientCreditApplied) y cerrada sub-reembolsada (Closed).
    /// Las lineas usan un servicio inexistente a proposito: el predicado compartido lo trata como "ya no cuenta
    /// como compra" (elegible), IGUAL para el read-model y para el extracto, asi el cuadre es por construccion.
    /// </summary>
    private static async Task<int> SeedReconciliationScenarioAsync(AppDbContext ctx)
    {
        var customer = new Customer { FullName = "Cliente", IsActive = true };
        var supplier = new Supplier { Name = "Operador", IsActive = true };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        async Task SeedBcAsync(string numero, BookingCancellationStatus status, decimal refundCap, decimal received)
        {
            var reserva = new Reserva { NumeroReserva = numero, Name = numero, PayerId = customer.Id, Status = EstadoReserva.Cancelled };
            var invoice = new Invoice { TipoComprobante = 11, Resultado = "A" };
            ctx.Reservas.Add(reserva);
            ctx.Invoices.Add(invoice);
            await ctx.SaveChangesAsync();

            var bc = new BookingCancellation
            {
                ReservaId = reserva.Id, CustomerId = customer.Id, SupplierId = supplier.Id,
                OriginatingInvoiceId = invoice.Id, Status = status, Reason = "rm", DraftedByUserId = "v",
                OperatorRefundDueBy = DateTime.UtcNow.AddDays(30), PenaltyStatus = PenaltyStatus.Confirmed,
            };
            ctx.BookingCancellations.Add(bc);
            await ctx.SaveChangesAsync();

            ctx.Set<BookingCancellationLine>().Add(new BookingCancellationLine
            {
                BookingCancellationId = bc.Id, SupplierId = supplier.Id, ServiceTable = CancellableServiceTable.Hotel,
                ServiceId = reserva.Id, Scope = BookingCancellationLineScope.Full, Currency = "USD",
                RefundCap = refundCap, ReceivedRefundAmount = received,
            });
            await ctx.SaveChangesAsync();
        }

        await SeedBcAsync("R-AWAIT", BookingCancellationStatus.AwaitingOperatorRefund, refundCap: 400m, received: 0m);
        await SeedBcAsync("R-PARTIAL", BookingCancellationStatus.ClientCreditApplied, refundCap: 300m, received: 100m);
        await SeedBcAsync("R-CLOSED", BookingCancellationStatus.Closed, refundCap: 250m, received: 100m);

        return supplier.Id;
    }

    [Fact]
    public async Task Cuadre_por_construccion_solapa_reembolsos_igual_a_me_tiene_que_devolver()
    {
        await using var ctx = NewDbContext();
        var supplierId = await SeedReconciliationScenarioAsync(ctx);

        // "Me tiene que devolver" del extracto (fuente unica del recuadro).
        var circuit = await TravelApi.Infrastructure.Reservations.SupplierCancellationCircuitReader
            .LoadAsync(ctx, supplierId, CancellationToken.None);

        // Total por moneda de la solapa "Reembolsos" (read-model), con permiso de ver costos.
        var svc = new OperatorRefundReadModelService(ctx, AdminAccessor(), permissionResolver: null);
        var items = await svc.GetSupplierPendingRefundsAsync(supplierId, CancellationToken.None);
        var trayByCurrency = items
            .SelectMany(i => i.EstimatedRefundsByCurrency)
            .GroupBy(e => e.Currency)
            .ToDictionary(g => g.Key, g => g.Sum(e => e.EstimatedAmount));

        // Numeros explicitos: 400 (pendiente) + 200 (residuo parcial 300-100) + 150 (cerrada 250-100) = 750.
        Assert.Equal(750m, circuit.ReceivableByCurrency["USD"]);
        Assert.Equal(750m, trayByCurrency["USD"]);

        // Cuadre EXACTO por moneda en ambas direcciones (ni de menos ni monedas de mas).
        foreach (var kv in circuit.ReceivableByCurrency)
            Assert.Equal(kv.Value, trayByCurrency.TryGetValue(kv.Key, out var t) ? t : 0m);
        foreach (var kv in trayByCurrency)
            Assert.Equal(circuit.ReceivableByCurrency.TryGetValue(kv.Key, out var r) ? r : 0m, kv.Value);
    }

    [Fact]
    public async Task Filas_de_residuo_traen_rotulo_recibido_y_capacidad_de_registro_correctos()
    {
        await using var ctx = NewDbContext();
        var supplierId = await SeedReconciliationScenarioAsync(ctx);

        var svc = new OperatorRefundReadModelService(ctx, AdminAccessor(), permissionResolver: null);
        var items = await svc.GetSupplierPendingRefundsAsync(supplierId, CancellationToken.None);

        var awaiting = Assert.Single(items, i => i.NumeroReserva == "R-AWAIT");
        Assert.Equal(OperatorRefundRowStatus.AwaitingRefund, awaiting.RowStatus);
        Assert.True(awaiting.CanRegisterRefund);
        var awaitingUsd = Assert.Single(awaiting.EstimatedRefundsByCurrency);
        Assert.Equal(400m, awaitingUsd.EstimatedAmount);
        Assert.Equal(0m, awaitingUsd.AmountReceived);

        var partial = Assert.Single(items, i => i.NumeroReserva == "R-PARTIAL");
        Assert.Equal(OperatorRefundRowStatus.PartiallyRefunded, partial.RowStatus);
        Assert.True(partial.CanRegisterRefund);   // ClientCreditApplied SI admite registrar mas reembolso
        var partialUsd = Assert.Single(partial.EstimatedRefundsByCurrency);
        Assert.Equal(200m, partialUsd.EstimatedAmount);   // 300 - 100
        Assert.Equal(100m, partialUsd.AmountReceived);
        // "quedan X de Y": Y = residuo + recibido = 300.
        Assert.Equal(300m, partialUsd.EstimatedAmount + partialUsd.AmountReceived);

        var closed = Assert.Single(items, i => i.NumeroReserva == "R-CLOSED");
        Assert.Equal(OperatorRefundRowStatus.ClosedWithResidue, closed.RowStatus);
        Assert.False(closed.CanRegisterRefund);   // Closed NO admite registro directo (fila informativa)
        var closedUsd = Assert.Single(closed.EstimatedRefundsByCurrency);
        Assert.Equal(150m, closedUsd.EstimatedAmount);   // 250 - 100
        Assert.Equal(100m, closedUsd.AmountReceived);
    }
}
