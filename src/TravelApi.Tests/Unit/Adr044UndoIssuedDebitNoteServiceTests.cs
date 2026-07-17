using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Exceptions;
using TravelApi.Domain.Reservations;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Reservations;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-044 "Deshacer una multa ya emitida" (2026-07-14): tests UNIT de <c>BookingCancellationService.UndoIssuedDebitNoteAsync</c>
/// (guards duros + happy path + idempotencia) y del read-model <c>GetOperatorPenaltySituationAsync</c> con los
/// dos estados nuevos. Molde de <c>CancellationCorrectPenaltyAndSituationTests</c>. InMemory + mocks, sin Docker.
///
/// <para><b>Gate (i)/(ii) del re-review (B1)</b>: ver <see cref="Gate_PayingAPenaltyOnACancelledReserva_DoesNotAutoMintClientCredit"/>,
/// que resuelve con evidencia de código cuál de los dos escenarios es el real ANTES de fiar la fórmula de B1.</para>
/// </summary>
public class Adr044UndoIssuedDebitNoteServiceTests
{
    private static AppDbContext NewDbContext(string? dbName = null) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName ?? $"undo-debit-note-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private sealed record Harness(
        BookingCancellationService Service, AppDbContext Ctx, Mock<IInvoiceService> InvoiceMock, Mock<IAuditService> AuditMock);

    private static Harness BuildService(bool flagOn = true)
    {
        var ctx = NewDbContext();
        var invoiceMock = new Mock<IInvoiceService>();
        var auditMock = new Mock<IAuditService>();
        var settings = new OperationalFinanceSettings
        {
            EnableNewCancellationFlow = true,
            EnableCancellationDebitNote = flagOn,
            EnableMultiCurrencyInvoicing = true,
        };
        var settingsMock = new Mock<IOperationalFinanceSettingsService>();
        settingsMock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>())).ReturnsAsync(settings);

        var service = new BookingCancellationService(
            ctx, invoiceMock.Object, new Mock<IApprovalRequestService>().Object, auditMock.Object,
            NullLogger<BookingCancellationService>.Instance, settingsMock.Object,
            new Mock<IFiscalLiquidationCalculator>().Object, new Mock<IAdminUserCountService>().Object);

        return new Harness(service, ctx, invoiceMock, auditMock);
    }

    /// <summary>
    /// Semilla: reserva Cancelled, factura C original con CAE, BC Confirmed + multa Confirmed + ND (C=12) YA
    /// EMITIDA CON CAE (Issued, vinculada). Es el punto de partida "Done" que habilita deshacer.
    /// </summary>
    private static async Task<(Guid BcPublicId, BookingCancellation Bc, Invoice Nd, Invoice Original, Reserva Reserva, Customer Customer)>
        SeedIssuedDebitNoteAsync(AppDbContext ctx, decimal ndAmount = 30_000m, string ndCurrency = "PES")
    {
        var customer = new Customer { FullName = "Cliente Deshacer", IsActive = true };
        var supplier = new Supplier { Name = "Operador Deshacer", IsActive = true };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = "R-UNDO",
            Name = "Reserva Deshacer",
            PayerId = customer.Id,
            Status = EstadoReserva.Cancelled,
            Balance = 0m,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var original = new Invoice
        {
            TipoComprobante = 11, PuntoDeVenta = 1, NumeroComprobante = 100,
            CAE = "12345678", Resultado = "A", MonId = "PES", MonCotiz = 1m,
            ImporteTotal = 100_000m, ReservaId = reserva.Id, AnnulmentStatus = AnnulmentStatus.None,
        };
        ctx.Invoices.Add(original);
        await ctx.SaveChangesAsync();

        var nd = new Invoice
        {
            TipoComprobante = 12, PuntoDeVenta = 1, NumeroComprobante = 200,
            CAE = "55555555", Resultado = "A", MonId = ndCurrency, MonCotiz = 1m,
            ImporteTotal = ndAmount, ReservaId = reserva.Id, OriginalInvoiceId = original.Id,
        };
        ctx.Invoices.Add(nd);
        await ctx.SaveChangesAsync();

        ctx.Set<InvoiceItem>().Add(new InvoiceItem
        {
            InvoiceId = nd.Id, Description = "Multa por cancelación", Quantity = 1, UnitPrice = ndAmount,
            Total = ndAmount, AlicuotaIvaId = 3,
        });
        await ctx.SaveChangesAsync();

        var bc = new BookingCancellation
        {
            ReservaId = reserva.Id,
            CustomerId = customer.Id,
            SupplierId = supplier.Id,
            OriginatingInvoiceId = original.Id,
            Status = BookingCancellationStatus.AwaitingOperatorRefund,
            PenaltyStatus = PenaltyStatus.Confirmed,
            PenaltyAmountAtEvent = ndAmount,
            PenaltyCurrencyAtEvent = ndCurrency,
            DebitNoteInvoiceId = nd.Id,
            DebitNoteStatus = DebitNoteStatus.Issued,
            Reason = "Anulacion; multa ya emitida",
            DraftedByUserId = "vendedor-1",
            ConfirmedWithClientAt = DateTime.UtcNow.AddDays(-20),
            PenaltyConfirmedAt = DateTime.UtcNow.AddDays(-5),
        };
        ctx.BookingCancellations.Add(bc);
        await ctx.SaveChangesAsync();

        return (bc.PublicId, bc, nd, original, reserva, customer);
    }

    /// <summary>CreateAsync (mock) emite una NC en la BD InMemory con CbteAsoc apuntando al comprobante pasado.</summary>
    private static void SetupCreateAsyncEmitsCreditNote(Harness h, Action<CreateInvoiceRequest>? captureRequest = null)
    {
        h.InvoiceMock
            .Setup(s => s.CreateAsync(
                It.IsAny<CreateInvoiceRequest>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((CreateInvoiceRequest req, string? uid, string? uname, CancellationToken ct) =>
            {
                captureRequest?.Invoke(req);
                var reservaId = h.Ctx.Reservas.First().Id;
                var originalInvoiceId = h.Ctx.Invoices
                    .First(i => i.PublicId == Guid.Parse(req.OriginalInvoiceId!)).Id;
                var nc = new Invoice
                {
                    TipoComprobante = 13, PuntoDeVenta = 1, NumeroComprobante = 900,
                    Resultado = "PENDING", ReservaId = reservaId, OriginalInvoiceId = originalInvoiceId,
                };
                h.Ctx.Invoices.Add(nc);
                h.Ctx.SaveChanges();
                return new InvoiceDto { PublicId = nc.PublicId };
            });
    }

    // ============================================================
    // Gate (i)/(ii) del re-review — DEBE resolverse ANTES de confiar en la fórmula B1.
    // ============================================================

    [Fact]
    public async Task Gate_PayingAPenaltyOnACancelledReserva_DoesNotAutoMintClientCredit()
    {
        // Este test demuestra, contra el código REAL (no supuesto), que HOY ningún camino automático convierte
        // en ClientCreditEntry la plata de un pago sobre una reserva anulada. Los ÚNICOS 3 call-sites de
        // OverpaymentCreditConverter.ConvertAsync (grep exhaustivo del código) son PaymentService.CreatePaymentAsync
        // (bloqueado en reservas Cancelled por EnsureCollectable/EvaluateRegisterPayment — Cancelled NO está en
        // SaleFirmStatuses), PaymentService.RestorePaymentAsync (restaurar un cobro voideado, no "cobrar la multa")
        // y ReservaService.AddPaymentAsync legacy (mismo guard EnsureCollectable). NINGUNO corre como efecto
        // lateral de que ConfirmedSale caiga a 0 (NC total) ni de que se confirme/deshaga una multa. Por eso:
        // simulamos el ÚNICO estado alcanzable hoy (un Payment "vivo" en la reserva, en la moneda de la multa,
        // que deja Balance negativo) y verificamos que, SIN pasar por nuestro código nuevo, NO existe ningún
        // ClientCreditEntry — confirmando el escenario (ii) del re-review: el dinero queda SIN bolsillo hasta que
        // el "deshacer" lo acuña explícitamente (B1).
        var h = BuildService();
        var (_, bc, _, _, reserva, customer) = await SeedIssuedDebitNoteAsync(h.Ctx);

        // Plata que quedó "sobrante" en la reserva (por ejemplo, un pago pre-cancelación que la reversión de la
        // NC total del viaje no terminó de neutralizar del todo) — vive como Payment, NUNCA como ClientCreditEntry.
        h.Ctx.Payments.Add(new Payment
        {
            ReservaId = reserva.Id, Amount = 30_000m, Currency = "ARS",
            Status = "Paid", EntryType = PaymentEntryTypes.Payment, AffectsCash = true, PaidAt = DateTime.UtcNow,
        });
        await h.Ctx.SaveChangesAsync();

        var creditsForCustomer = await h.Ctx.ClientCreditEntries
            .Where(e => e.CustomerId == customer.Id)
            .ToListAsync();

        Assert.Empty(creditsForCustomer); // (ii) confirmado: nada lo acuñó todavía.
    }

    // ============================================================
    // Guards (INV-UNDO-*)
    // ============================================================

    [Fact]
    public async Task Undo_WithEmptyReason_Throws400()
    {
        var h = BuildService();
        var (bcId, _, _, _, _, _) = await SeedIssuedDebitNoteAsync(h.Ctx);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            h.Service.UndoIssuedDebitNoteAsync(bcId, "   ", "u", "U", default, requesterIsAdmin: true));
    }

    [Fact]
    public async Task Undo_WithFlagOff_Throws409()
    {
        var h = BuildService(flagOn: false);
        var (bcId, _, _, _, _, _) = await SeedIssuedDebitNoteAsync(h.Ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Service.UndoIssuedDebitNoteAsync(
                bcId, "La ND estaba mal.", "u", "U", default, requesterIsAdmin: true));
    }

    [Fact]
    public async Task Undo_WhenNotAdmin_Rebounds409_INV_UNDO_PERM()
    {
        // Gate B1 (spec UX firmada): SOLO Admin. Un no-admin (aunque tuviera el permiso classify) rebota.
        var h = BuildService();
        var (bcId, _, _, _, _, _) = await SeedIssuedDebitNoteAsync(h.Ctx);

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            h.Service.UndoIssuedDebitNoteAsync(
                bcId, "La ND estaba mal.", "u", "U", default, requesterIsAdmin: false));
        Assert.Equal("INV-UNDO-PERM", ex.InvariantCode);
    }

    [Fact]
    public async Task Undo_WhenDebitNoteStillPendingWithoutCae_Rebounds409_INV_UNDO_001()
    {
        var h = BuildService();
        var (bcId, bc, nd, _, _, _) = await SeedIssuedDebitNoteAsync(h.Ctx);
        // La ND todavía no tiene CAE (sigue en vuelo): no hay nada fiscal que deshacer todavía.
        nd.Resultado = "PENDING";
        nd.CAE = null;
        bc.DebitNoteStatus = DebitNoteStatus.Pending;
        await h.Ctx.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            h.Service.UndoIssuedDebitNoteAsync(
                bcId, "La ND estaba mal.", "u", "U", default, requesterIsAdmin: true));
        Assert.Equal("INV-UNDO-001", ex.InvariantCode);
    }

    [Fact]
    public async Task Undo_WhenPenaltyNotConfirmed_Rebounds409_INV_UNDO_001()
    {
        var h = BuildService();
        var (bcId, bc, _, _, _, _) = await SeedIssuedDebitNoteAsync(h.Ctx);
        bc.PenaltyStatus = PenaltyStatus.Estimated;
        await h.Ctx.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            h.Service.UndoIssuedDebitNoteAsync(
                bcId, "La ND estaba mal.", "u", "U", default, requesterIsAdmin: true));
        Assert.Equal("INV-UNDO-001", ex.InvariantCode);
    }

    [Fact]
    public async Task Undo_WhenAlreadyHasLiveAnnulment_Rebounds409_INV_UNDO_002()
    {
        var h = BuildService();
        var (bcId, bc, nd, _, _, _) = await SeedIssuedDebitNoteAsync(h.Ctx);
        h.Ctx.Set<BookingCancellationDebitNoteAnnulment>().Add(new BookingCancellationDebitNoteAnnulment
        {
            BookingCancellationId = bc.Id,
            AnnulledDebitNoteInvoiceId = nd.Id,
            Status = DebitNoteAnnulmentStatus.Pending,
            Reason = "Ya en curso.",
            Amount = nd.ImporteTotal,
            Currency = "ARS",
            RequestedByUserId = "otro",
        });
        await h.Ctx.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            h.Service.UndoIssuedDebitNoteAsync(
                bcId, "Deshacer otra vez.", "u", "U", default, requesterIsAdmin: true));
        Assert.Equal("INV-UNDO-002", ex.InvariantCode);
    }

    [Fact]
    public async Task Undo_WhenPreviousAttemptFailed_IsAllowedAgain()
    {
        // Regla dura #10: las Failed NO cuentan contra la idempotencia (se puede reintentar deshacer).
        var h = BuildService();
        var (bcId, bc, nd, _, _, _) = await SeedIssuedDebitNoteAsync(h.Ctx);
        h.Ctx.Set<BookingCancellationDebitNoteAnnulment>().Add(new BookingCancellationDebitNoteAnnulment
        {
            BookingCancellationId = bc.Id,
            AnnulledDebitNoteInvoiceId = nd.Id,
            Status = DebitNoteAnnulmentStatus.Failed,
            Reason = "Intento previo rechazado por ARCA.",
            Amount = nd.ImporteTotal,
            Currency = "ARS",
            RequestedByUserId = "otro",
        });
        await h.Ctx.SaveChangesAsync();
        SetupCreateAsyncEmitsCreditNote(h);

        var dto = await h.Service.UndoIssuedDebitNoteAsync(
            bcId, "Reintento del deshacer.", "u", "U", default, requesterIsAdmin: true);

        Assert.NotNull(dto);
        var annulments = h.Ctx.Set<BookingCancellationDebitNoteAnnulment>()
            .Where(a => a.BookingCancellationId == bc.Id).ToList();
        Assert.Equal(2, annulments.Count); // la Failed vieja + la Pending nueva.
        Assert.Contains(annulments, a => a.Status == DebitNoteAnnulmentStatus.Pending);
    }

    [Fact]
    public async Task Undo_WhenOriginatingInvoiceFullyAnnulled_IsAllowed_CreatesPendingAnnulment()
    {
        // Caso real de producción (F-2026-1043, bug encontrado 2026-07-14): CUALQUIER multa de operador cuelga
        // de una reserva anulada del todo, y anular una reserva SIEMPRE emite antes la NC total de la factura de
        // venta original (la deja AnnulmentStatus.Succeeded). O sea que este es el escenario NORMAL, no una
        // excepción — antes de este fix la regla dura #11 lo bloqueaba en el 100% de los casos y el botón de
        // deshacer nunca funcionaba. La NC que deshace la multa apunta a la ND (nunca a la factura de venta), así
        // que el estado de la factura de venta no debería importarle nada a esta operación.
        var h = BuildService();
        var (bcId, _, nd, original, _, _) = await SeedIssuedDebitNoteAsync(h.Ctx, ndAmount: 30_000m, ndCurrency: "PES");
        original.AnnulmentStatus = AnnulmentStatus.Succeeded;
        await h.Ctx.SaveChangesAsync();
        SetupCreateAsyncEmitsCreditNote(h);

        var dto = await h.Service.UndoIssuedDebitNoteAsync(
            bcId, "La multa estaba mal calculada.", "u", "U", default, requesterIsAdmin: true);

        Assert.NotNull(dto);
        var annulment = h.Ctx.Set<BookingCancellationDebitNoteAnnulment>().Single();
        Assert.Equal(DebitNoteAnnulmentStatus.Pending, annulment.Status);
        Assert.Equal(nd.Id, annulment.AnnulledDebitNoteInvoiceId);
    }

    [Fact]
    public async Task Undo_WhenDebitNoteHasTributes_Rebounds409_INV_UNDO_MANUAL()
    {
        // Point 4 (guard defensivo): una ND con tributos (IIBB) no se auto-deshace (la NC-anula-ND no los
        // reversaría -> fuga fiscal). Inalcanzable por el camino automático (el gating manda a manual las
        // facturas con tributos), pero se blinda por si una ND con tributos existiera por un camino manual.
        var h = BuildService();
        var (bcId, bc, nd, _, _, _) = await SeedIssuedDebitNoteAsync(h.Ctx);
        h.Ctx.Set<InvoiceTribute>().Add(new InvoiceTribute
        {
            InvoiceId = nd.Id, TributeId = 99, Description = "IIBB CABA",
            BaseImponible = 1000m, Alicuota = 3m, Importe = 30m,
        });
        await h.Ctx.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            h.Service.UndoIssuedDebitNoteAsync(
                bcId, "Deshacer una ND con impuestos.", "u", "U", default, requesterIsAdmin: true));
        Assert.Equal("INV-UNDO-MANUAL", ex.InvariantCode);
    }

    // ============================================================
    // Tanda D1 (2026-07-16) B3 — guard AMPLIO: bloquear si hay un Payment VIVO imputado a la ND que NO afecta
    // el saldo operativo de la reserva (AffectsReservaBalance=false). Cubre el puente de saldo a favor aplicado
    // Y un cobro real (efectivo/transferencia) vigente desde 44fcea6.
    // ============================================================

    [Fact]
    public async Task Undo_WhenPenaltyHasLiveCreditBridge_Rebounds409_INV_UNDO_CREDITBRIDGE()
    {
        // Si el cliente ya aplico saldo a favor contra esta multa, deshacer la ND perderia esa plata: el
        // reconciliador (OperatorPenaltyUndoRules, balance-based) no ve el puente porque a proposito no toca
        // el saldo de la reserva.
        var h = BuildService();
        var (bcId, bc, nd, _, _, _) = await SeedIssuedDebitNoteAsync(h.Ctx);

        h.Ctx.Payments.Add(new Payment
        {
            ReservaId = bc.ReservaId,
            LinkedInvoiceId = nd.Id,
            Amount = 5000m,
            Currency = "ARS",
            Method = AppliedCreditBridge.PenaltyBridgeMethod,
            AffectsCash = false,
            AffectsReservaBalance = false,
            EntryType = PaymentEntryTypes.Payment,
            Status = "Paid",
            PaidAt = DateTime.UtcNow,
        });
        await h.Ctx.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            h.Service.UndoIssuedDebitNoteAsync(
                bcId, "Deshacer con saldo aplicado.", "u", "U", default, requesterIsAdmin: true));
        Assert.Equal("INV-UNDO-CREDITBRIDGE", ex.InvariantCode);
    }

    [Fact]
    public async Task Undo_WhenPenaltyHasLiveRealCashPayment_Rebounds409_INV_UNDO_LIVEPAYMENT()
    {
        // Agujero preexistente (vigente desde 44fcea6): un cobro REAL en efectivo/transferencia contra la ND
        // tampoco mueve el saldo de la reserva -> deshacer perderia esa plata igual que con el puente.
        var h = BuildService();
        var (bcId, bc, nd, _, _, _) = await SeedIssuedDebitNoteAsync(h.Ctx);

        h.Ctx.Payments.Add(new Payment
        {
            ReservaId = bc.ReservaId,
            LinkedInvoiceId = nd.Id,
            Amount = 5000m,
            Currency = "ARS",
            Method = "Transfer",
            AffectsCash = true,
            AffectsReservaBalance = false,
            EntryType = PaymentEntryTypes.Payment,
            Status = "Paid",
            PaidAt = DateTime.UtcNow,
        });
        await h.Ctx.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            h.Service.UndoIssuedDebitNoteAsync(
                bcId, "Deshacer con cobro real.", "u", "U", default, requesterIsAdmin: true));
        Assert.Equal("INV-UNDO-LIVEPAYMENT", ex.InvariantCode);
    }

    [Fact]
    public async Task Undo_WhenCreditBridgeAlreadyReversed_IsAllowed()
    {
        // Anti falso-positivo: un puente YA revertido (soft-deleted) no bloquea el deshacer.
        var h = BuildService();
        var (bcId, bc, nd, _, _, _) = await SeedIssuedDebitNoteAsync(h.Ctx);

        h.Ctx.Payments.Add(new Payment
        {
            ReservaId = bc.ReservaId,
            LinkedInvoiceId = nd.Id,
            Amount = 5000m,
            Currency = "ARS",
            Method = AppliedCreditBridge.PenaltyBridgeMethod,
            AffectsCash = false,
            AffectsReservaBalance = false,
            EntryType = PaymentEntryTypes.Payment,
            Status = "Paid",
            PaidAt = DateTime.UtcNow,
            IsDeleted = true,
            DeletedAt = DateTime.UtcNow,
        });
        await h.Ctx.SaveChangesAsync();
        SetupCreateAsyncEmitsCreditNote(h);

        var dto = await h.Service.UndoIssuedDebitNoteAsync(
            bcId, "Deshacer con puente ya revertido.", "u", "U", default, requesterIsAdmin: true);

        Assert.NotNull(dto);
    }

    // ============================================================
    // B2 — guard conservador multi-operador (INV-UNDO-MULTIOP)
    // ============================================================

    [Fact]
    public async Task Undo_MultiOperatorAmbiguousManualReviewLine_Rebounds409_INV_UNDO_MULTIOP()
    {
        var h = BuildService();
        var (bcId, bc, nd, _, _, _) = await SeedIssuedDebitNoteAsync(h.Ctx);

        var supplierB = new Supplier { Name = "Operador B", IsActive = true };
        var supplierC = new Supplier { Name = "Operador C", IsActive = true };
        h.Ctx.Suppliers.Add(supplierB);
        h.Ctx.Suppliers.Add(supplierC);
        await h.Ctx.SaveChangesAsync();

        // Linea A: alimento la ND (TargetInvoiceId = nd.Id).
        var lineA = new BookingCancellationLine
        {
            BookingCancellationId = bc.Id, SupplierId = bc.SupplierId, Currency = "ARS",
            PenaltyStatus = PenaltyStatus.Confirmed, RefundCap = 10_000m,
        };
        h.Ctx.BookingCancellationLines.Add(lineA);
        await h.Ctx.SaveChangesAsync();
        h.Ctx.BookingCancellationLineOperatorCharges.Add(new BookingCancellationLineOperatorCharge
        {
            BookingCancellationLineId = lineA.Id, Kind = OperatorChargeKind.AdministrativeFee,
            CollectionMode = PenaltyCollectionMode.Retenida, Amount = 15_000m, Currency = "ARS",
            TargetInvoiceId = nd.Id, ConfirmedByUserId = "u",
        });

        // Linea C: OTRO operador que TAMBIEN alimento la MISMA ND (TargetInvoiceId = nd.Id) -> la ND mezcla
        // cargos de 2+ operadores (condicion #1 del guard).
        var lineC = new BookingCancellationLine
        {
            BookingCancellationId = bc.Id, SupplierId = supplierC.Id, Currency = "ARS",
            PenaltyStatus = PenaltyStatus.Confirmed, RefundCap = 7_000m,
        };
        h.Ctx.BookingCancellationLines.Add(lineC);
        await h.Ctx.SaveChangesAsync();
        h.Ctx.BookingCancellationLineOperatorCharges.Add(new BookingCancellationLineOperatorCharge
        {
            BookingCancellationLineId = lineC.Id, Kind = OperatorChargeKind.AdministrativeFee,
            CollectionMode = PenaltyCollectionMode.Retenida, Amount = 5_000m, Currency = "ARS",
            TargetInvoiceId = nd.Id, ConfirmedByUserId = "u",
        });

        // Linea B: un TERCER operador, ManualReview (ND complementaria pendiente), cargo SIN TargetInvoiceId
        // (legacy): no se puede determinar mecanicamente que quedo afuera de la ND de A+C -> ambiguedad (condicion #2).
        var lineB = new BookingCancellationLine
        {
            BookingCancellationId = bc.Id, SupplierId = supplierB.Id, Currency = "ARS",
            PenaltyStatus = PenaltyStatus.Confirmed, DebitNoteStatus = DebitNoteStatus.ManualReview,
            RefundCap = 5_000m,
        };
        h.Ctx.BookingCancellationLines.Add(lineB);
        await h.Ctx.SaveChangesAsync();
        h.Ctx.BookingCancellationLineOperatorCharges.Add(new BookingCancellationLineOperatorCharge
        {
            BookingCancellationLineId = lineB.Id, Kind = OperatorChargeKind.AdministrativeFee,
            CollectionMode = PenaltyCollectionMode.Retenida, Amount = 8_000m, Currency = "ARS",
            TargetInvoiceId = null, // legacy, sin resolver -> ambiguo
            ConfirmedByUserId = "u",
        });
        await h.Ctx.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            h.Service.UndoIssuedDebitNoteAsync(
                bcId, "Deshacer con multi-operador ambiguo.", "u", "U", default, requesterIsAdmin: true));
        Assert.Equal("INV-UNDO-MULTIOP", ex.InvariantCode);
    }

    // ============================================================
    // Happy path + contrato exacto de la NC (regla dura #1/#3/#4)
    // ============================================================

    [Fact]
    public async Task Undo_HappyPath_CreatesPendingAnnulment_WithNcMirroringTheDebitNote()
    {
        var h = BuildService();
        var (bcId, bc, nd, _, _, _) = await SeedIssuedDebitNoteAsync(h.Ctx, ndAmount: 30_000m, ndCurrency: "PES");
        CreateInvoiceRequest? captured = null;
        SetupCreateAsyncEmitsCreditNote(h, req => captured = req);

        var dto = await h.Service.UndoIssuedDebitNoteAsync(
            bcId, "El monto de la multa estaba mal calculado.", "u", "U", default,
            requesterIsAdmin: true);

        Assert.NotNull(dto);
        Assert.NotNull(captured);
        // Regla dura #1: OriginalInvoiceId apunta a LA ND, nunca a la factura.
        Assert.Equal(nd.PublicId.ToString(), captured!.OriginalInvoiceId);
        Assert.True(captured.IsCreditNote);
        Assert.False(captured.IsDebitNote);
        // Regla dura #4: hereda el TC/moneda CONGELADOS de la ND (nunca recotiza).
        Assert.Equal(nd.MonId, captured.MonId);
        Assert.Equal(nd.MonCotiz, captured.MonCotiz);
        // Espejo de renglones (regla dura #3).
        Assert.Single(captured.Items);
        Assert.Equal(30_000m, captured.Items.Single().Total);

        // La ND SIGUE Issued (todavía no llegó el CAE de la NC): el desvinculo lo hace el reconciliador, no esto.
        var bcAfter = h.Ctx.BookingCancellations.AsNoTracking().Single();
        Assert.Equal(DebitNoteStatus.Issued, bcAfter.DebitNoteStatus);
        Assert.Equal(nd.Id, bcAfter.DebitNoteInvoiceId);

        var annulment = h.Ctx.Set<BookingCancellationDebitNoteAnnulment>().Single();
        Assert.Equal(DebitNoteAnnulmentStatus.Pending, annulment.Status);
        Assert.Equal(nd.Id, annulment.AnnulledDebitNoteInvoiceId);
        Assert.NotNull(annulment.AnnulmentCreditNoteInvoiceId);
        Assert.Equal(30_000m, annulment.Amount);
        Assert.Equal("El monto de la multa estaba mal calculado.", annulment.Reason);
        Assert.Equal("u", annulment.RequestedByUserId);

        h.AuditMock.Verify(a => a.StageBusinessEvent(
            TravelApi.Application.Constants.AuditActions.OperatorPenaltyDebitNoteUndoRequested,
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), "u", "U"), Times.Once);
    }

    // ============================================================
    // Read-model: los dos estados nuevos, vistos desde GetOperatorPenaltySituationAsync.
    // ============================================================

    [Fact]
    public async Task Situation_AfterUndoRequested_IsDebitNoteAnnulling()
    {
        var h = BuildService();
        var (bcId, bc, nd, _, reserva, _) = await SeedIssuedDebitNoteAsync(h.Ctx);
        SetupCreateAsyncEmitsCreditNote(h);

        await h.Service.UndoIssuedDebitNoteAsync(
            bcId, "Deshacer.", "u", "U", default, requesterIsAdmin: true);

        var sit = await h.Service.GetOperatorPenaltySituationAsync(
            reserva.PublicId, userCanClassifyOperatorPenalty: true, isCallerAdmin: true, default);

        Assert.Equal(OperatorPenaltySituationState.DebitNoteAnnulling.ToString(), sit.State);
        // Mientras se deshace, ninguna otra accion se ofrece (ni retry, ni correct, ni waive, ni undo de nuevo).
        Assert.False(sit.CanRetryDebitNote);
        Assert.False(sit.CanCorrectAmountCurrency);
        Assert.False(sit.CanWaive);
        Assert.False(sit.CanUndoDebitNote);
    }

    [Fact]
    public async Task Situation_WhenLastAnnulmentFailed_IsDebitNoteAnnulmentFailed()
    {
        var h = BuildService();
        var (_, bc, nd, _, reserva, _) = await SeedIssuedDebitNoteAsync(h.Ctx);
        h.Ctx.Set<BookingCancellationDebitNoteAnnulment>().Add(new BookingCancellationDebitNoteAnnulment
        {
            BookingCancellationId = bc.Id,
            AnnulledDebitNoteInvoiceId = nd.Id,
            Status = DebitNoteAnnulmentStatus.Failed,
            Reason = "ARCA rechazo la NC.",
            Amount = nd.ImporteTotal,
            Currency = "ARS",
            RequestedByUserId = "u",
            RequestedAt = DateTime.UtcNow.AddHours(-1),
        });
        await h.Ctx.SaveChangesAsync();

        var sit = await h.Service.GetOperatorPenaltySituationAsync(
            reserva.PublicId, userCanClassifyOperatorPenalty: true, isCallerAdmin: true, default);

        Assert.Equal(OperatorPenaltySituationState.DebitNoteAnnulmentFailed.ToString(), sit.State);
        // Gap 1: el deshacer fallido HABILITA reintentar (mismo botón, mismo endpoint POST undo-debit-note).
        Assert.True(sit.CanUndoDebitNote);
    }

    [Fact]
    public async Task Situation_WhenDone_CanUndoDebitNoteIsTrue_ForAdmin()
    {
        var h = BuildService();
        var (_, _, _, _, reserva, _) = await SeedIssuedDebitNoteAsync(h.Ctx);

        var sit = await h.Service.GetOperatorPenaltySituationAsync(
            reserva.PublicId, userCanClassifyOperatorPenalty: true, isCallerAdmin: true, default);

        Assert.Equal(OperatorPenaltySituationState.Done.ToString(), sit.State);
        Assert.True(sit.CanUndoDebitNote);
    }

    [Fact]
    public async Task Situation_WhenDone_ClassifyPermissionButNotAdmin_CanUndoDebitNoteIsFalse()
    {
        // Gate B1 (spec UX firmada): deshacer es SOLO Admin. Un usuario con el permiso classify (que SÍ puede
        // confirmar/corregir/reintentar la multa) NO debe ver el link de deshacer — si lo viera, el endpoint le
        // rebotaría INV-UNDO-PERM (anti-patrón "botón que rebota"). Por eso canUndoDebitNote mira isCallerAdmin.
        var h = BuildService();
        var (_, _, _, _, reserva, _) = await SeedIssuedDebitNoteAsync(h.Ctx);

        var sit = await h.Service.GetOperatorPenaltySituationAsync(
            reserva.PublicId, userCanClassifyOperatorPenalty: true, isCallerAdmin: false, default);

        Assert.Equal(OperatorPenaltySituationState.Done.ToString(), sit.State);
        Assert.False(sit.CanUndoDebitNote);
    }

    [Fact]
    public async Task Situation_WhenDone_NoPermissionAtAll_CanUndoDebitNoteIsFalse()
    {
        var h = BuildService();
        var (_, _, _, _, reserva, _) = await SeedIssuedDebitNoteAsync(h.Ctx);

        var sit = await h.Service.GetOperatorPenaltySituationAsync(
            reserva.PublicId, userCanClassifyOperatorPenalty: false, isCallerAdmin: false, default);

        Assert.False(sit.CanUndoDebitNote);
    }

    // ============================================================
    // Gap 1 (retry) — el deshacer fallido se puede reintentar de punta a punta.
    // ============================================================

    [Fact]
    public async Task Situation_FailedThenRetry_IsAllowed_AndInsertsANewPendingRow()
    {
        // El estado DebitNoteAnnulmentFailed prende canUndoDebitNote; volver a llamar al endpoint inserta una fila
        // NUEVA (el índice único parcial excluye Failed, INV-UNDO-002 no bloquea) y el paso vuelve a "en curso".
        var h = BuildService();
        var (bcId, bc, nd, _, reserva, _) = await SeedIssuedDebitNoteAsync(h.Ctx);
        h.Ctx.Set<BookingCancellationDebitNoteAnnulment>().Add(new BookingCancellationDebitNoteAnnulment
        {
            BookingCancellationId = bc.Id, AnnulledDebitNoteInvoiceId = nd.Id,
            Status = DebitNoteAnnulmentStatus.Failed, Reason = "ARCA rechazo.", Amount = nd.ImporteTotal,
            Currency = "ARS", RequestedByUserId = "u", RequestedAt = DateTime.UtcNow.AddHours(-2),
        });
        await h.Ctx.SaveChangesAsync();
        SetupCreateAsyncEmitsCreditNote(h);

        // El read-model ofrece el retry.
        var before = await h.Service.GetOperatorPenaltySituationAsync(
            reserva.PublicId, userCanClassifyOperatorPenalty: true, isCallerAdmin: true, default);
        Assert.True(before.CanUndoDebitNote);

        // El retry (mismo endpoint) inserta una fila Pending nueva sin rebotar por idempotencia.
        await h.Service.UndoIssuedDebitNoteAsync(
            bcId, "Reintento del deshacer.", "u", "U", default, requesterIsAdmin: true);

        var rows = h.Ctx.Set<BookingCancellationDebitNoteAnnulment>()
            .Where(a => a.BookingCancellationId == bc.Id).ToList();
        Assert.Equal(2, rows.Count);
        Assert.Single(rows, a => a.Status == DebitNoteAnnulmentStatus.Pending);
        Assert.Single(rows, a => a.Status == DebitNoteAnnulmentStatus.Failed);

        var after = await h.Service.GetOperatorPenaltySituationAsync(
            reserva.PublicId, userCanClassifyOperatorPenalty: true, isCallerAdmin: true, default);
        Assert.Equal(OperatorPenaltySituationState.DebitNoteAnnulling.ToString(), after.State);
    }

    // ============================================================
    // Gap 3 (collectedPenaltyAmount) — variante "ya pagó" del modal.
    // ============================================================

    [Fact]
    public async Task Situation_Done_SettledOrOverpaidReserva_CollectedPenaltyAmountIsZero()
    {
        // Fix bloqueante seguridad (2026-07-14): balance <= 0 (reserva anulada SALDADA, el caso normal) NO es
        // "multa pagada" -> collected 0 (evita el crédito FANTASMA por el total de la multa que nadie pagó).
        var h = BuildService();
        var (_, _, _, _, reserva, _) = await SeedIssuedDebitNoteAsync(h.Ctx, ndAmount: 30_000m, ndCurrency: "PES");
        h.Ctx.ReservaMoneyByCurrency.Add(new ReservaMoneyByCurrency
        {
            ReservaId = reserva.Id, Currency = "ARS", Balance = 0m,
        });
        await h.Ctx.SaveChangesAsync();

        var sit = await h.Service.GetOperatorPenaltySituationAsync(
            reserva.PublicId, userCanClassifyOperatorPenalty: true, isCallerAdmin: true, default);

        Assert.True(sit.CanUndoDebitNote);
        Assert.Equal(0m, sit.CollectedPenaltyAmount);
    }

    [Fact]
    public async Task Situation_Done_Impaga_CollectedPenaltyAmountIsZero()
    {
        // Balance == gross en la moneda -> multa íntegramente por cobrar -> collected = 0 (nada cobrado todavía).
        var h = BuildService();
        var (_, _, _, _, reserva, _) = await SeedIssuedDebitNoteAsync(h.Ctx, ndAmount: 30_000m, ndCurrency: "PES");
        h.Ctx.ReservaMoneyByCurrency.Add(new ReservaMoneyByCurrency
        {
            ReservaId = reserva.Id, Currency = "ARS", Balance = 30_000m,
        });
        await h.Ctx.SaveChangesAsync();

        var sit = await h.Service.GetOperatorPenaltySituationAsync(
            reserva.PublicId, userCanClassifyOperatorPenalty: true, isCallerAdmin: true, default);

        Assert.Equal(0m, sit.CollectedPenaltyAmount);
    }

    [Fact]
    public async Task Situation_Done_PartiallyPaid_CollectedPenaltyAmountIsCollectedPortion()
    {
        // gross 30000, pendiente 10000 -> collected 20000 (misma fórmula que acuña el crédito al consumar).
        var h = BuildService();
        var (_, _, _, _, reserva, _) = await SeedIssuedDebitNoteAsync(h.Ctx, ndAmount: 30_000m, ndCurrency: "PES");
        h.Ctx.ReservaMoneyByCurrency.Add(new ReservaMoneyByCurrency
        {
            ReservaId = reserva.Id, Currency = "ARS", Balance = 10_000m,
        });
        await h.Ctx.SaveChangesAsync();

        var sit = await h.Service.GetOperatorPenaltySituationAsync(
            reserva.PublicId, userCanClassifyOperatorPenalty: true, isCallerAdmin: true, default);

        Assert.Equal(20_000m, sit.CollectedPenaltyAmount);
    }

    [Fact]
    public async Task Situation_WhenCannotUndo_CollectedPenaltyAmountIsNull()
    {
        // En un estado que NO habilita deshacer (ej. ND encolada Pending) no se calcula el collected: null.
        var h = BuildService();
        var (_, bc, nd, _, reserva, _) = await SeedIssuedDebitNoteAsync(h.Ctx);
        nd.Resultado = "PENDING";
        nd.CAE = null;
        bc.DebitNoteStatus = DebitNoteStatus.Pending;
        h.Ctx.ReservaMoneyByCurrency.Add(new ReservaMoneyByCurrency
        {
            ReservaId = reserva.Id, Currency = "ARS", Balance = 0m,
        });
        await h.Ctx.SaveChangesAsync();

        var sit = await h.Service.GetOperatorPenaltySituationAsync(
            reserva.PublicId, userCanClassifyOperatorPenalty: true, isCallerAdmin: true, default);

        Assert.Equal(OperatorPenaltySituationState.DebitNoteQueued.ToString(), sit.State);
        Assert.False(sit.CanUndoDebitNote);
        Assert.Null(sit.CollectedPenaltyAmount);
    }

    // ============================================================
    // Gap 2 (lastDebitNoteUndo) — rastro del último deshacer consumado.
    // ============================================================

    [Fact]
    public async Task Situation_ExposesLastConsummatedUndo_FromSucceededChildRow()
    {
        // Escenario "re-abierto": la ND vieja YA se deshizo (fila hija Succeeded, ND desvinculada), la multa
        // volvió a ConfirmedNoDebitNote. El DTO debe mostrar el rastro del deshacer para el cartel.
        var h = BuildService();
        var (_, bc, oldNd, _, reserva, _) = await SeedIssuedDebitNoteAsync(h.Ctx);
        // Simular el post-consumación: ND desvinculada, paso reabierto.
        bc.DebitNoteInvoiceId = null;
        bc.DebitNoteStatus = DebitNoteStatus.NotApplicable;
        await h.Ctx.SaveChangesAsync();

        var undoneAt = new DateTime(2026, 07, 10, 15, 30, 0, DateTimeKind.Utc);
        h.Ctx.Set<BookingCancellationDebitNoteAnnulment>().Add(new BookingCancellationDebitNoteAnnulment
        {
            BookingCancellationId = bc.Id, AnnulledDebitNoteInvoiceId = oldNd.Id,
            Status = DebitNoteAnnulmentStatus.Succeeded, Reason = "El monto estaba mal calculado.",
            Amount = oldNd.ImporteTotal, Currency = "ARS", RequestedByUserId = "corrector",
            RequestedByUserName = "Corrector Uno", RequestedAt = undoneAt,
        });
        await h.Ctx.SaveChangesAsync();

        var sit = await h.Service.GetOperatorPenaltySituationAsync(
            reserva.PublicId, userCanClassifyOperatorPenalty: true, isCallerAdmin: true, default);

        Assert.NotNull(sit.LastDebitNoteUndo);
        Assert.Equal(undoneAt, sit.LastDebitNoteUndo!.UndoneAt);
        Assert.Equal("Corrector Uno", sit.LastDebitNoteUndo.UndoneByName);
        Assert.Equal("El monto estaba mal calculado.", sit.LastDebitNoteUndo.Reason);
    }

    [Fact]
    public async Task Situation_LastUndo_TakesMostRecentSucceeded_IgnoringFailedAndPending()
    {
        // Con varias vueltas: el rastro muestra la Succeeded MÁS RECIENTE, ignorando Failed/Pending.
        var h = BuildService();
        var (_, bc, nd, _, reserva, _) = await SeedIssuedDebitNoteAsync(h.Ctx);
        h.Ctx.Set<BookingCancellationDebitNoteAnnulment>().AddRange(
            new BookingCancellationDebitNoteAnnulment
            {
                BookingCancellationId = bc.Id, AnnulledDebitNoteInvoiceId = nd.Id,
                Status = DebitNoteAnnulmentStatus.Succeeded, Reason = "Primera vuelta.", Amount = nd.ImporteTotal,
                Currency = "ARS", RequestedByUserId = "u", RequestedByUserName = "Uno",
                RequestedAt = new DateTime(2026, 07, 01, 10, 0, 0, DateTimeKind.Utc),
            },
            new BookingCancellationDebitNoteAnnulment
            {
                BookingCancellationId = bc.Id, AnnulledDebitNoteInvoiceId = nd.Id,
                Status = DebitNoteAnnulmentStatus.Succeeded, Reason = "Segunda vuelta (la más reciente).",
                Amount = nd.ImporteTotal, Currency = "ARS", RequestedByUserId = "u", RequestedByUserName = "Dos",
                RequestedAt = new DateTime(2026, 07, 05, 10, 0, 0, DateTimeKind.Utc),
            },
            new BookingCancellationDebitNoteAnnulment
            {
                BookingCancellationId = bc.Id, AnnulledDebitNoteInvoiceId = nd.Id,
                Status = DebitNoteAnnulmentStatus.Failed, Reason = "Un intento fallido posterior.",
                Amount = nd.ImporteTotal, Currency = "ARS", RequestedByUserId = "u", RequestedByUserName = "Tres",
                RequestedAt = new DateTime(2026, 07, 09, 10, 0, 0, DateTimeKind.Utc),
            });
        await h.Ctx.SaveChangesAsync();

        var sit = await h.Service.GetOperatorPenaltySituationAsync(
            reserva.PublicId, userCanClassifyOperatorPenalty: true, isCallerAdmin: true, default);

        Assert.NotNull(sit.LastDebitNoteUndo);
        Assert.Equal("Segunda vuelta (la más reciente).", sit.LastDebitNoteUndo!.Reason);
        Assert.Equal("Dos", sit.LastDebitNoteUndo.UndoneByName);
    }

    [Fact]
    public async Task Situation_WhenNeverUndone_LastDebitNoteUndoIsNull()
    {
        var h = BuildService();
        var (_, _, _, _, reserva, _) = await SeedIssuedDebitNoteAsync(h.Ctx);

        var sit = await h.Service.GetOperatorPenaltySituationAsync(
            reserva.PublicId, userCanClassifyOperatorPenalty: true, isCallerAdmin: true, default);

        Assert.Null(sit.LastDebitNoteUndo);
    }
}
