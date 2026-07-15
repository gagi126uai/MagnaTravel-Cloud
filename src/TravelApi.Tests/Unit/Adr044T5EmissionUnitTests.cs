using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.DTOs.Cancellation;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Exceptions;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-044 T5-emision (2026-07-15): tests UNIT (InMemory, sin Docker) de la emision REAL de la Nota de
/// Credito parcial de una cancelacion PARCIAL (<see cref="BookingCancellationService.ConfirmPartialCancellationEmissionAsync"/>),
/// del reconciliador dedicado (<see cref="PartialCreditNoteT5Reconciliation"/>) y de los lectores child-aware
/// (C1/C2). InMemory NO valida <c>FOR UPDATE</c> ni CHECK constraints SQL — esos casos (concurrencia real,
/// reversion economica end-to-end, herencia de moneda/TC contra AfipService) van a integracion Postgres (ver
/// <c>Adr044T5EmissionIntegrationTests</c>). Lo que SI se cubre aca, ejecutable y verificado en verde:
/// guards de emision, el prorrateo de lineas, el reconciliador T5 (aprobado/rechazado, ambos degradan a
/// "cuerpo directo" en InMemory), la derivacion de <c>AnnulmentStatus</c>, y los 5 lectores child-aware.
/// </summary>
public class Adr044T5EmissionUnitTests
{
    // ============================================================
    // Builders
    // ============================================================

    private static AppDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"adr044-t5-emission-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private sealed record Harness(
        BookingCancellationService Service,
        AppDbContext Ctx,
        Mock<IInvoiceService> InvoiceMock,
        Mock<IAuditService> AuditMock);

    private static Harness BuildService(AppDbContext ctx, bool enableCancellationDebitNote = false)
    {
        var invoiceMock = new Mock<IInvoiceService>();
        var settingsMock = new Mock<IOperationalFinanceSettingsService>();
        settingsMock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings
            {
                EnableNewCancellationFlow = true,
                OperatorRefundTimeoutDays = 45,
                EnableCancellationDebitNote = enableCancellationDebitNote,
                IvaProrrateoMode = IvaProrrateoMode.ProportionalToNet,
                PartialCreditNoteRoundingTolerance = 0.02m,
            });
        var auditMock = new Mock<IAuditService>();

        var service = new BookingCancellationService(
            ctx,
            invoiceMock.Object,
            new Mock<IApprovalRequestService>().Object,
            auditMock.Object,
            NullLogger<BookingCancellationService>.Instance,
            settingsMock.Object,
            new Mock<IFiscalLiquidationCalculator>().Object,
            new Mock<IAdminUserCountService>().Object);

        return new Harness(service, ctx, invoiceMock, auditMock);
    }

    /// <summary>
    /// Siembra el esqueleto minimo: agencia Monotributo (AfipSettings), cliente, operador, reserva, factura
    /// de venta VIVA con CAE (mono-alicuota 21%, un solo item) y un BC T5 Drafted con UNA linea Partial
    /// resuelta contra esa factura.
    /// </summary>
    private static async Task<(
        Reserva Reserva, Invoice Invoice, BookingCancellation Bc, BookingCancellationLine Line, Supplier Supplier)>
        SeedResolvedPartialAsync(
            AppDbContext ctx,
            decimal invoiceTotal = 100_000m,
            decimal confirmedAmount = 30_000m,
            string agencyTaxCondition = "Monotributo")
    {
        ctx.AfipSettings.Add(new AfipSettings { TaxCondition = agencyTaxCondition, Cuit = 20111111111 });

        var customer = new Customer { FullName = "Cliente T5", IsActive = true, TaxCondition = "Consumidor Final" };
        var supplier = new Supplier { Name = "Operador T5", IsActive = true, TaxCondition = "IVA_RESP_INSCRIPTO" };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = "R-T5-EMIT",
            Name = "Reserva T5 emision",
            PayerId = customer.Id,
            Status = EstadoReserva.Confirmed,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var invoice = new Invoice
        {
            TipoComprobante = 11, // Factura C (Monotributo)
            PuntoDeVenta = 1,
            NumeroComprobante = 500,
            CAE = "cae-viva-t5",
            Resultado = "A",
            MonId = "PES",
            MonCotiz = 1m,
            ImporteTotal = invoiceTotal,
            ImporteNeto = invoiceTotal,
            ImporteIva = 0m,
            ReservaId = reserva.Id,
            AnnulmentStatus = AnnulmentStatus.None,
            CreatedAt = DateTime.UtcNow,
        };
        ctx.Invoices.Add(invoice);
        var item = new InvoiceItem
        {
            Description = "Hotel Test",
            Quantity = 1,
            UnitPrice = invoiceTotal,
            Total = invoiceTotal,
            AlicuotaIvaId = 3, // 0% (Factura C no discrimina IVA)
        };
        await ctx.SaveChangesAsync();
        item.InvoiceId = invoice.Id;
        ctx.Set<InvoiceItem>().Add(item);
        await ctx.SaveChangesAsync();

        var bc = new BookingCancellation
        {
            ReservaId = reserva.Id,
            CustomerId = customer.Id,
            SupplierId = supplier.Id,
            OriginatingInvoiceId = invoice.Id,
            Status = BookingCancellationStatus.Drafted,
            Reason = "Cancelacion parcial de servicio",
            DraftedAt = DateTime.UtcNow,
            DraftedByUserId = "vendedor-1",
            FiscalSnapshot = new FiscalSnapshot { Source = ExchangeRateSource.Unset, FetchedAt = default },
        };
        var line = new BookingCancellationLine
        {
            SupplierId = supplier.Id,
            ServiceTable = CancellableServiceTable.Hotel,
            ServiceId = 1,
            Scope = BookingCancellationLineScope.Partial,
            Currency = "ARS",
            LineSaleAmount = confirmedAmount,
            TargetInvoiceId = invoice.Id,
            ConfirmedGrossCreditAmount = confirmedAmount,
        };
        bc.Lines.Add(line);
        ctx.BookingCancellations.Add(bc);
        await ctx.SaveChangesAsync();

        return (reserva, invoice, bc, line, supplier);
    }

    /// <summary>Hace que CreateAsync inserte una Invoice NC PENDING real en la BD InMemory y devuelva su DTO.</summary>
    private static void SetupCreateEmitsCreditNote(Harness h, Invoice originalInvoice)
    {
        h.InvoiceMock
            .Setup(s => s.CreateAsync(
                It.IsAny<CreateInvoiceRequest>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CreateInvoiceRequest req, string? uid, string? uname, CancellationToken ct) =>
            {
                var nc = new Invoice
                {
                    TipoComprobante = 13, // NC C (espeja la Factura C original)
                    PuntoDeVenta = 1,
                    NumeroComprobante = 999,
                    Resultado = "PENDING",
                    MonId = req.MonId,
                    MonCotiz = req.MonCotiz,
                    ImporteTotal = req.TotalsOverride?.ImpTotal ?? req.Items.Sum(i => i.Total),
                    ImporteNeto = req.TotalsOverride?.ImpNeto ?? 0m,
                    ImporteIva = req.TotalsOverride?.ImpIVA ?? 0m,
                    ReservaId = originalInvoice.ReservaId,
                    OriginalInvoiceId = originalInvoice.Id,
                    CreatedAt = DateTime.UtcNow,
                };
                h.Ctx.Invoices.Add(nc);
                h.Ctx.SaveChanges();
                return new InvoiceDto { PublicId = nc.PublicId };
            });
    }

    // =====================================================================================
    // BuildPartialCreditNoteLinesForTargetInvoice — prorrateo puro (V11).
    // =====================================================================================

    [Fact]
    public void BuildLines_MonoAlicuota_OneLineWithFullAmount()
    {
        var items = new List<InvoiceItem>
        {
            new() { Description = "Hotel", Quantity = 1, UnitPrice = 100_000m, Total = 100_000m, AlicuotaIvaId = 3 },
        };

        var lines = BookingCancellationService.BuildPartialCreditNoteLinesForTargetInvoice(
            30_000m, items, "Devolución de prueba");

        var single = Assert.Single(lines);
        Assert.Equal(30_000m, single.Total);
        Assert.Equal(3, single.AlicuotaIvaId);
        Assert.Equal("Devolución de prueba", single.Description);
    }

    [Fact]
    public void BuildLines_MultiAlicuota_ProratesAndAbsorbsRoundingResidueExact()
    {
        // Dos alicuotas distintas en la factura origen (21% y 10.5%), pesos 2/3 y 1/3 del total.
        var items = new List<InvoiceItem>
        {
            new() { Description = "Item 21%", Quantity = 1, UnitPrice = 66_666.67m, Total = 66_666.67m, AlicuotaIvaId = 5 },
            new() { Description = "Item 10.5%", Quantity = 1, UnitPrice = 33_333.33m, Total = 33_333.33m, AlicuotaIvaId = 4 },
        };

        var fiscalAmountToCredit = 10_000.01m; // monto que fuerza redondeo no exacto en el prorrateo
        var lines = BookingCancellationService.BuildPartialCreditNoteLinesForTargetInvoice(
            fiscalAmountToCredit, items, "no se usa (multi-alicuota)");

        Assert.Equal(2, lines.Count);
        // Invariante duro: la suma de las lineas es EXACTAMENTE el monto a acreditar (residuo absorbido).
        Assert.Equal(fiscalAmountToCredit, lines.Sum(l => l.Total));
        Assert.Contains(lines, l => l.AlicuotaIvaId == 5);
        Assert.Contains(lines, l => l.AlicuotaIvaId == 4);
    }

    // =====================================================================================
    // Guards de ConfirmPartialCancellationEmissionAsync (§6.1).
    // =====================================================================================

    [Fact]
    public async Task Emit_LineWithoutTargetInvoice_RejectsWithUnresolved()
    {
        using var ctx = NewDbContext();
        var (_, _, bc, line, _) = await SeedResolvedPartialAsync(ctx);
        line.TargetInvoiceId = null;
        line.ConfirmedGrossCreditAmount = null;
        await ctx.SaveChangesAsync();

        var h = BuildService(ctx);

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            h.Service.ConfirmPartialCancellationEmissionAsync(bc.PublicId, "u1", "U", CancellationToken.None));
        Assert.Equal("INV-T5-EMIT-UNRESOLVED", ex.InvariantCode);
    }

    [Fact]
    public async Task Emit_BcNotDrafted_RejectsWithState()
    {
        using var ctx = NewDbContext();
        var (_, _, bc, _, _) = await SeedResolvedPartialAsync(ctx);
        bc.Status = BookingCancellationStatus.AwaitingFiscalConfirmation;
        await ctx.SaveChangesAsync();

        var h = BuildService(ctx);

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            h.Service.ConfirmPartialCancellationEmissionAsync(bc.PublicId, "u1", "U", CancellationToken.None));
        Assert.Equal("INV-T5-EMIT-STATE", ex.InvariantCode);
    }

    [Fact]
    public async Task Emit_BcHasFullLine_RejectsWithState()
    {
        // Un BC con una linea Full es el circuito de anulacion TOTAL (otro contrato) — nunca se emite por T5,
        // aunque tambien tenga lineas Partial (fusion tras "anular el resto", FRENTE E).
        using var ctx = NewDbContext();
        var (_, invoice, bc, _, supplier) = await SeedResolvedPartialAsync(ctx);
        bc.Lines.Add(new BookingCancellationLine
        {
            SupplierId = supplier.Id,
            ServiceTable = CancellableServiceTable.Generic,
            ServiceId = 0,
            Scope = BookingCancellationLineScope.Full,
            Currency = "ARS",
            LineSaleAmount = 1000m,
        });
        await ctx.SaveChangesAsync();

        var h = BuildService(ctx);

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            h.Service.ConfirmPartialCancellationEmissionAsync(bc.PublicId, "u1", "U", CancellationToken.None));
        Assert.Equal("INV-T5-EMIT-STATE", ex.InvariantCode);
    }

    [Fact]
    public async Task Emit_TwoLinesResolvedToDifferentInvoices_RejectsWithMultiInvoice()
    {
        using var ctx = NewDbContext();
        var (reserva, invoice1, bc, _, supplier) = await SeedResolvedPartialAsync(ctx);
        var invoice2 = new Invoice
        {
            TipoComprobante = 11, PuntoDeVenta = 1, NumeroComprobante = 501, CAE = "cae-2", Resultado = "A",
            MonId = "PES", MonCotiz = 1m, ImporteTotal = 50_000m, ImporteNeto = 50_000m,
            ReservaId = reserva.Id, AnnulmentStatus = AnnulmentStatus.None, CreatedAt = DateTime.UtcNow,
        };
        ctx.Invoices.Add(invoice2);
        await ctx.SaveChangesAsync();
        bc.Lines.Add(new BookingCancellationLine
        {
            SupplierId = supplier.Id,
            ServiceTable = CancellableServiceTable.Hotel,
            ServiceId = 2,
            Scope = BookingCancellationLineScope.Partial,
            Currency = "ARS",
            LineSaleAmount = 10_000m,
            TargetInvoiceId = invoice2.Id,
            ConfirmedGrossCreditAmount = 10_000m,
        });
        await ctx.SaveChangesAsync();

        var h = BuildService(ctx);

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            h.Service.ConfirmPartialCancellationEmissionAsync(bc.PublicId, "u1", "U", CancellationToken.None));
        Assert.Equal("INV-T5-EMIT-MULTI-INVOICE", ex.InvariantCode);
    }

    [Fact]
    public async Task Emit_AgencyResponsableInscripto_RejectsWithRiSignoff()
    {
        using var ctx = NewDbContext();
        var (_, _, bc, _, _) = await SeedResolvedPartialAsync(ctx, agencyTaxCondition: "Responsable Inscripto");
        var h = BuildService(ctx);

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            h.Service.ConfirmPartialCancellationEmissionAsync(bc.PublicId, "u1", "U", CancellationToken.None));
        Assert.Equal("INV-T5-EMIT-RI-SIGNOFF", ex.InvariantCode);
        // Nunca debe haber intentado emitir: Monotributo es la unica condicion habilitada hoy.
        h.InvoiceMock.Verify(s => s.CreateAsync(
            It.IsAny<CreateInvoiceRequest>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Emit_AmountExceedsRemaining_RejectsWithCap()
    {
        // Otra reserva por-linea (T5) contra la MISMA factura ya reservo casi todo el remanente: la
        // confirmacion de ESTA cancelacion, sumada, superaria el ImporteTotal.
        using var ctx = NewDbContext();
        var (reserva, invoice, bc, _, supplier) = await SeedResolvedPartialAsync(
            ctx, invoiceTotal: 100_000m, confirmedAmount: 30_000m);

        // BC hermano (otro evento de cancelacion) con una linea Partial resuelta por 80.000 contra la MISMA
        // factura: remanente fresco = 100.000 - 80.000 = 20.000 < 30.000 que esta cancelacion quiere acreditar.
        var siblingBc = new BookingCancellation
        {
            ReservaId = reserva.Id,
            CustomerId = bc.CustomerId,
            SupplierId = supplier.Id,
            OriginatingInvoiceId = invoice.Id,
            Status = BookingCancellationStatus.Drafted,
            Reason = "Otra cancelacion parcial (hermana)",
            DraftedAt = DateTime.UtcNow,
            DraftedByUserId = "vendedor-2",
            FiscalSnapshot = new FiscalSnapshot { Source = ExchangeRateSource.Unset, FetchedAt = default },
        };
        siblingBc.Lines.Add(new BookingCancellationLine
        {
            SupplierId = supplier.Id,
            ServiceTable = CancellableServiceTable.Hotel,
            ServiceId = 99,
            Scope = BookingCancellationLineScope.Partial,
            Currency = "ARS",
            LineSaleAmount = 80_000m,
            TargetInvoiceId = invoice.Id,
            ConfirmedGrossCreditAmount = 80_000m,
        });
        ctx.BookingCancellations.Add(siblingBc);
        await ctx.SaveChangesAsync();

        var h = BuildService(ctx);

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            h.Service.ConfirmPartialCancellationEmissionAsync(bc.PublicId, "u1", "U", CancellationToken.None));
        Assert.Equal("INV-T5-EMIT-CAP", ex.InvariantCode);
    }

    // =====================================================================================
    // Camino feliz: sella snapshot, transiciona, emite (mockeado) y crea la hija con el link
    // YA seteado (invariante dura B2) — el escalar del PADRE queda null (Decision B3).
    // =====================================================================================

    [Fact]
    public async Task Emit_HappyPath_SealsSnapshot_CreatesChildWithLinkAlreadySet_ParentScalarStaysNull()
    {
        using var ctx = NewDbContext();
        var (_, invoice, bc, _, _) = await SeedResolvedPartialAsync(ctx);
        var h = BuildService(ctx);
        SetupCreateEmitsCreditNote(h, invoice);

        var dto = await h.Service.ConfirmPartialCancellationEmissionAsync(bc.PublicId, "u1", "U", CancellationToken.None);

        Assert.Equal("AwaitingFiscalConfirmation", dto.Status);
        Assert.NotNull(dto.FiscalSnapshot);
        Assert.Equal("ARS", dto.FiscalSnapshot!.CurrencyAtEvent);

        var reloaded = await ctx.BookingCancellations
            .Include(b => b.CreditNotes)
            .AsNoTracking()
            .SingleAsync(b => b.Id == bc.Id);
        // DECISION B3: el escalar del PADRE queda null para T5 (defensa por construccion contra el
        // re-vinculador de ND huerfana).
        Assert.Null(reloaded.CreditNoteInvoiceId);
        Assert.Equal(CreditNoteKind.PartialOnOriginal, reloaded.CreditNoteKind);

        var child = Assert.Single(reloaded.CreditNotes);
        Assert.Equal(invoice.Id, child.OriginatingInvoiceId);
        // INVARIANTE DURA B2: la hija nace con el link YA seteado, en Pending (antes del CAE).
        Assert.NotNull(child.CreditNoteInvoiceId);
        Assert.Equal(BookingCancellationCreditNoteStatus.Pending, child.Status);

        // La factura destino sigue viva: nadie la marco anulada al solo encolar la NC.
        var invoiceReloaded = await ctx.Invoices.AsNoTracking().SingleAsync(i => i.Id == invoice.Id);
        Assert.Equal(AnnulmentStatus.None, invoiceReloaded.AnnulmentStatus);
    }

    [Fact]
    public async Task Emit_RetryAfterPreviousFailure_ReusesSameChildRow_NoUniqueViolation()
    {
        // Simula el "Reintentar": la hija ya existe en Failed (de un rechazo ARCA previo) y el BC volvio a
        // Drafted (lo hace el reconciliador T5). Confirmar-y-emitir de nuevo debe REUSAR esa fila (no crear una
        // segunda, que violaria el UNIQUE OriginatingInvoiceId+BookingCancellationId).
        using var ctx = NewDbContext();
        var (_, invoice, bc, _, _) = await SeedResolvedPartialAsync(ctx);
        ctx.BookingCancellationCreditNotes.Add(new BookingCancellationCreditNote
        {
            BookingCancellationId = bc.Id,
            OriginatingInvoiceId = invoice.Id,
            CreditNoteInvoiceId = 999999, // NC muerta anterior (rechazada)
            Status = BookingCancellationCreditNoteStatus.Failed,
            ArcaErrorMessage = "rechazo previo",
        });
        await ctx.SaveChangesAsync();

        var h = BuildService(ctx);
        SetupCreateEmitsCreditNote(h, invoice);

        await h.Service.ConfirmPartialCancellationEmissionAsync(bc.PublicId, "u1", "U", CancellationToken.None);

        var children = await ctx.BookingCancellationCreditNotes.AsNoTracking()
            .Where(c => c.BookingCancellationId == bc.Id).ToListAsync();
        var child = Assert.Single(children); // UNA sola fila hija, reusada.
        Assert.Equal(BookingCancellationCreditNoteStatus.Pending, child.Status);
        Assert.Null(child.ArcaErrorMessage);
    }

    // =====================================================================================
    // T-B2 (link no diferido): el cap ve el remanente correcto de una hija Pending CON link, y
    // reserva el ImporteTotal completo si (por bug) quedara Pending SIN link.
    // =====================================================================================

    [Fact]
    public async Task ComputeRemaining_ChildPendingWithLink_SeesCorrectRemainder_NotZero()
    {
        using var ctx = NewDbContext();
        var (_, invoice, bc, _, _) = await SeedResolvedPartialAsync(ctx, invoiceTotal: 100_000m, confirmedAmount: 30_000m);
        var h = BuildService(ctx);
        SetupCreateEmitsCreditNote(h, invoice);

        await h.Service.ConfirmPartialCancellationEmissionAsync(bc.PublicId, "u1", "U", CancellationToken.None);

        // El remanente FRESCO tiene que ser 100.000 - 30.000 = 70.000 (la hija Pending CON link cuenta por su
        // NC real, no reserva el ImporteTotal completo).
        var remaining = await h.Service.ComputeInvoiceRemainingCreditableAmountAsync(invoice.Id, CancellationToken.None);
        Assert.Equal(70_000m, remaining);
    }

    [Fact]
    public async Task ComputeRemaining_ChildPendingWithoutLink_ReservesFullImporteTotal_ContraRegression()
    {
        // Contra-regresion documentada por B2: si una hija quedara Pending SIN CreditNoteInvoiceId (el bug que
        // la invariante dura evita), el cap la trata como "NC TOTAL en camino" y reserva TODO.
        using var ctx = NewDbContext();
        var (_, invoice, bc, _, _) = await SeedResolvedPartialAsync(ctx, invoiceTotal: 100_000m, confirmedAmount: 30_000m);
        ctx.BookingCancellationCreditNotes.Add(new BookingCancellationCreditNote
        {
            BookingCancellationId = bc.Id,
            OriginatingInvoiceId = invoice.Id,
            CreditNoteInvoiceId = null, // BUG simulado: hija sin link.
            Status = BookingCancellationCreditNoteStatus.Pending,
        });
        await ctx.SaveChangesAsync();

        var h = BuildService(ctx);
        var remaining = await h.Service.ComputeInvoiceRemainingCreditableAmountAsync(invoice.Id, CancellationToken.None);
        Assert.Equal(0m, remaining);
    }

    // =====================================================================================
    // T-B1 (contra-test critico) + derivacion de AnnulmentStatus — el reconciliador T5 DEDICADO,
    // corrido directo (InMemory degrada RunUnderInvoiceLockAsync-equivalente al cuerpo directo).
    // =====================================================================================

    [Fact]
    public async Task Reconciler_Approved_SucceedsChild_AdvancesBc_NeverTouchesReservaOrParentScalar()
    {
        using var ctx = NewDbContext();
        var (reserva, invoice, bc, _, _) = await SeedResolvedPartialAsync(
            ctx, invoiceTotal: 100_000m, confirmedAmount: 30_000m);
        var h = BuildService(ctx);
        SetupCreateEmitsCreditNote(h, invoice);
        await h.Service.ConfirmPartialCancellationEmissionAsync(bc.PublicId, "u1", "U", CancellationToken.None);

        var nc = await ctx.Invoices.SingleAsync(i => i.OriginalInvoiceId == invoice.Id);
        nc.Resultado = "A";
        nc.CAE = "cae-nc-t5";
        await ctx.SaveChangesAsync();

        var reconciled = await PartialCreditNoteT5Reconciliation.TryReconcileAsync(
            ctx, nc, auditService: null, NullLogger.Instance, CancellationToken.None);
        Assert.True(reconciled);

        var bcReloaded = await ctx.BookingCancellations.Include(b => b.CreditNotes).AsNoTracking().SingleAsync(b => b.Id == bc.Id);
        var child = Assert.Single(bcReloaded.CreditNotes);
        Assert.Equal(BookingCancellationCreditNoteStatus.Succeeded, child.Status);
        // T-B1: NUNCA se disparo el camino de completitud ADR-042 — el escalar del padre sigue null y el BC
        // avanzo por SU circuito (AwaitingOperatorRefund), NO AwaitingOperatorRefund-de-anulacion-total con
        // el escalar seteado.
        Assert.Null(bcReloaded.CreditNoteInvoiceId);
        Assert.Equal(BookingCancellationStatus.AwaitingOperatorRefund, bcReloaded.Status);
        Assert.NotNull(bcReloaded.OperatorRefundDueBy);

        // La reserva NUNCA se marca cancelada/en curso de anulacion por este camino.
        var reservaReloaded = await ctx.Reservas.AsNoTracking().SingleAsync(r => r.Id == reserva.Id);
        Assert.Equal(EstadoReserva.Confirmed, reservaReloaded.Status);

        // Derivacion de AnnulmentStatus: remanente 100.000-30.000=70.000 > 0 -> la factura SIGUE VIVA.
        var invoiceReloaded = await ctx.Invoices.AsNoTracking().SingleAsync(i => i.Id == invoice.Id);
        Assert.NotEqual(AnnulmentStatus.Succeeded, invoiceReloaded.AnnulmentStatus);
    }

    [Fact]
    public async Task Reconciler_Approved_LastPortion_RemainderZero_MarksInvoiceAnnulmentSucceeded()
    {
        // Borde "ultima porcion" (§15-IR / T-derivacion-borde): el monto acreditado ES el remanente completo.
        using var ctx = NewDbContext();
        var (_, invoice, bc, _, _) = await SeedResolvedPartialAsync(
            ctx, invoiceTotal: 30_000m, confirmedAmount: 30_000m);
        var h = BuildService(ctx);
        SetupCreateEmitsCreditNote(h, invoice);
        await h.Service.ConfirmPartialCancellationEmissionAsync(bc.PublicId, "u1", "U", CancellationToken.None);

        var nc = await ctx.Invoices.SingleAsync(i => i.OriginalInvoiceId == invoice.Id);
        nc.Resultado = "A";
        nc.CAE = "cae-nc-t5-full";
        await ctx.SaveChangesAsync();

        await PartialCreditNoteT5Reconciliation.TryReconcileAsync(
            ctx, nc, auditService: null, NullLogger.Instance, CancellationToken.None);

        var invoiceReloaded = await ctx.Invoices.AsNoTracking().SingleAsync(i => i.Id == invoice.Id);
        Assert.Equal(AnnulmentStatus.Succeeded, invoiceReloaded.AnnulmentStatus);
        Assert.NotNull(invoiceReloaded.AnnulledAt);
    }

    [Fact]
    public async Task Reconciler_Rejected_ChildFailed_BcBackToDrafted_InvoiceUntouched()
    {
        using var ctx = NewDbContext();
        var (_, invoice, bc, _, _) = await SeedResolvedPartialAsync(ctx);
        var h = BuildService(ctx);
        SetupCreateEmitsCreditNote(h, invoice);
        await h.Service.ConfirmPartialCancellationEmissionAsync(bc.PublicId, "u1", "U", CancellationToken.None);

        var nc = await ctx.Invoices.SingleAsync(i => i.OriginalInvoiceId == invoice.Id);
        nc.Resultado = "R";
        nc.Observaciones = "CUIT invalido (simulado)";
        await ctx.SaveChangesAsync();

        var reconciled = await PartialCreditNoteT5Reconciliation.TryReconcileAsync(
            ctx, nc, auditService: null, NullLogger.Instance, CancellationToken.None);
        Assert.True(reconciled);

        var bcReloaded = await ctx.BookingCancellations.Include(b => b.CreditNotes).AsNoTracking().SingleAsync(b => b.Id == bc.Id);
        var child = Assert.Single(bcReloaded.CreditNotes);
        Assert.Equal(BookingCancellationCreditNoteStatus.Failed, child.Status);
        // Reintentable: vuelve a Drafted (mismo estado que exige el guard de emision).
        Assert.Equal(BookingCancellationStatus.Drafted, bcReloaded.Status);

        var invoiceReloaded = await ctx.Invoices.AsNoTracking().SingleAsync(i => i.Id == invoice.Id);
        Assert.Equal(AnnulmentStatus.None, invoiceReloaded.AnnulmentStatus);
    }

    [Fact]
    public async Task Reconciler_Idempotent_RedeliveryOnAlreadySucceededChild_NoOp()
    {
        using var ctx = NewDbContext();
        var (_, invoice, bc, _, _) = await SeedResolvedPartialAsync(ctx);
        var h = BuildService(ctx);
        SetupCreateEmitsCreditNote(h, invoice);
        await h.Service.ConfirmPartialCancellationEmissionAsync(bc.PublicId, "u1", "U", CancellationToken.None);

        var nc = await ctx.Invoices.SingleAsync(i => i.OriginalInvoiceId == invoice.Id);
        nc.Resultado = "A";
        nc.CAE = "cae-nc-t5";
        await ctx.SaveChangesAsync();

        await PartialCreditNoteT5Reconciliation.TryReconcileAsync(
            ctx, nc, auditService: null, NullLogger.Instance, CancellationToken.None);

        // Redelivery de Hangfire: se vuelve a llamar con la MISMA Invoice ya resuelta.
        var secondCall = await PartialCreditNoteT5Reconciliation.TryReconcileAsync(
            ctx, nc, auditService: null, NullLogger.Instance, CancellationToken.None);
        Assert.False(secondCall); // no-op: la hija ya no esta Pending.
    }

    [Fact]
    public async Task Reconciler_UnrelatedInvoice_NoOp_DoesNotThrow()
    {
        // Cualquier otra NC/ND que no sea una devolucion T5 tiene que dar 0 filas (no-op barato).
        using var ctx = NewDbContext();
        var invoice = new Invoice
        {
            TipoComprobante = 8, PuntoDeVenta = 1, NumeroComprobante = 1, Resultado = "A", CAE = "x",
            ImporteTotal = 100m, CreatedAt = DateTime.UtcNow,
        };
        ctx.Invoices.Add(invoice);
        await ctx.SaveChangesAsync();

        var reconciled = await PartialCreditNoteT5Reconciliation.TryReconcileAsync(
            ctx, invoice, auditService: null, NullLogger.Instance, CancellationToken.None);
        Assert.False(reconciled);
    }

    // =====================================================================================
    // T-B3 (colision clase-"Deshacer"): el re-vinculador de ND huerfana NO debe adoptar una ND
    // suelta sobre un BC T5, ni siquiera con multa de operador CONFIRMADA (peor caso).
    // =====================================================================================

    [Fact]
    public async Task OrphanDebitNoteRelinker_NeverAdoptsOntoT5Bc_EvenWithConfirmedPenalty()
    {
        using var ctx = NewDbContext();
        var (_, invoice, bc, _, _) = await SeedResolvedPartialAsync(ctx);
        var h = BuildService(ctx, enableCancellationDebitNote: true);
        SetupCreateEmitsCreditNote(h, invoice);
        await h.Service.ConfirmPartialCancellationEmissionAsync(bc.PublicId, "u1", "U", CancellationToken.None);

        // Simular CAE de la NC (para llegar a AwaitingOperatorRefund, precondicion de ConfirmPenaltyAsync).
        var nc = await ctx.Invoices.SingleAsync(i => i.OriginalInvoiceId == invoice.Id);
        nc.Resultado = "A";
        nc.CAE = "cae-nc-t5";
        await ctx.SaveChangesAsync();
        await PartialCreditNoteT5Reconciliation.TryReconcileAsync(
            ctx, nc, auditService: null, NullLogger.Instance, CancellationToken.None);

        // Peor caso del re-vinculador: PenaltyStatus=Confirmed en el BC T5, DebitNoteInvoiceId null (nunca se
        // emitio la ND), y una ND SUELTA para la MISMA factura original + reserva (candidata a re-vincularse).
        var bcTracked = await ctx.BookingCancellations.SingleAsync(b => b.Id == bc.Id);
        bcTracked.PenaltyStatus = PenaltyStatus.Confirmed;
        await ctx.SaveChangesAsync();

        ctx.Invoices.Add(new Invoice
        {
            TipoComprobante = 12, // ND C
            PuntoDeVenta = 1,
            NumeroComprobante = 700,
            Resultado = "A",
            CAE = "cae-nd-suelta",
            OriginalInvoiceId = invoice.Id,
            ReservaId = bcTracked.ReservaId,
            CreatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();

        var pending = await h.Service.GetCancellationsWithMissingDebitNoteAsync(CancellationToken.None);

        // El re-vinculador NUNCA debe haber tocado este BC: su escalar CreditNoteInvoiceId sigue null (rompe
        // la PRIMERA condicion del predicado `CreditNoteInvoiceId != null`), asi que nunca entra al candidate
        // set y la ND suelta NUNCA se re-vincula.
        var bcAfter = await ctx.BookingCancellations.AsNoTracking().SingleAsync(b => b.Id == bc.Id);
        Assert.Null(bcAfter.DebitNoteInvoiceId);
        Assert.Null(bcAfter.CreditNoteInvoiceId);
        Assert.DoesNotContain(pending, p => p.BookingCancellationPublicId == bc.PublicId);
    }

    // =====================================================================================
    // C1/C2 — lectores child-aware de la pata de multa/fee. T-C1a (no deadlock, post-CAE),
    // T-C2a (pre-CAE rebota), T-C2b (post-CAE funciona), T-C1b (bandeja de fee).
    // =====================================================================================

    private async Task<(BookingCancellation Bc, Invoice Invoice)> SeedT5PostEmissionAsync(
        AppDbContext ctx, Harness h, BookingCancellationCreditNoteStatus childStatus)
    {
        var (_, invoice, bc, _, _) = await SeedResolvedPartialAsync(ctx);
        SetupCreateEmitsCreditNote(h, invoice);
        await h.Service.ConfirmPartialCancellationEmissionAsync(bc.PublicId, "u1", "U", CancellationToken.None);

        var nc = await ctx.Invoices.SingleAsync(i => i.OriginalInvoiceId == invoice.Id);
        var bcTracked = await ctx.BookingCancellations.Include(b => b.CreditNotes).SingleAsync(b => b.Id == bc.Id);
        var child = bcTracked.CreditNotes.Single();

        if (childStatus == BookingCancellationCreditNoteStatus.Succeeded)
        {
            // Camino real: simular CAE + correr el reconciliador (deja el BC en AwaitingOperatorRefund, la
            // precondicion de estado que exige ConfirmPenaltyAsync/WaiveOperatorPenaltyAsync).
            nc.Resultado = "A";
            nc.CAE = "cae-nc-t5";
            await ctx.SaveChangesAsync();
            await PartialCreditNoteT5Reconciliation.TryReconcileAsync(
                ctx, nc, auditService: null, NullLogger.Instance, CancellationToken.None);
        }
        // childStatus == Pending: no tocar nada mas (la hija ya nacio Pending, BC en AwaitingFiscalConfirmation
        // — el estado PRE-CAE real, sin atajos).

        var bcFinal = await ctx.BookingCancellations.AsNoTracking().SingleAsync(b => b.Id == bc.Id);
        return (bcFinal, invoice);
    }

    [Fact]
    public async Task ConfirmPenalty_PostCae_ChildSucceeded_ParentScalarNull_DoesNotDeadlock()
    {
        using var ctx = NewDbContext();
        var h = BuildService(ctx, enableCancellationDebitNote: true);
        var (bc, _) = await SeedT5PostEmissionAsync(ctx, h, BookingCancellationCreditNoteStatus.Succeeded);

        // Precondicion: el escalar del padre sigue null (Decision B3) aunque la hija ya tenga CAE.
        Assert.Null(bc.CreditNoteInvoiceId);
        Assert.Equal(BookingCancellationStatus.AwaitingOperatorRefund, bc.Status);

        var request = new ConfirmPenaltyRequest(
            ConceptKind: CancellationConceptKind.OperatorPenaltyPassThrough,
            ConfirmedPenaltyAmount: 5_000m,
            OperatorConfirmationDate: DateTime.UtcNow.AddDays(-1),
            DebitNotePurpose: null,
            SupportingDocumentReference: "https://docs/operador.pdf");

        // No debe rebotar con INV-ADR014-001 ("la NC al cliente aun no esta confirmada por la AFIP") — C2
        // hace que el gate vea la NC viva por la HIJA (Status==Succeeded), no por el escalar del padre (null).
        var ex = await Record.ExceptionAsync(() =>
            h.Service.ConfirmPenaltyAsync(
                bc.PublicId, request, "u1", "U", requesterIsAdmin: false, CancellationToken.None,
                userCanClassifyAgencyPenalty: true));
        if (ex is BusinessInvariantViolationException biv)
            Assert.NotEqual("INV-ADR014-001", biv.InvariantCode);
    }

    [Fact]
    public async Task WaivePenalty_PostCae_ChildSucceeded_ParentScalarNull_DoesNotDeadlock()
    {
        using var ctx = NewDbContext();
        var h = BuildService(ctx, enableCancellationDebitNote: true);
        var (bc, _) = await SeedT5PostEmissionAsync(ctx, h, BookingCancellationCreditNoteStatus.Succeeded);

        var ex = await Record.ExceptionAsync(() =>
            h.Service.WaiveOperatorPenaltyAsync(
                bc.PublicId, "el operador no cobro multa (test)", "u1", "U", CancellationToken.None,
                userCanClassifyAgencyPenalty: true));
        if (ex is BusinessInvariantViolationException biv)
            Assert.NotEqual("INV-WAIVE-001", biv.InvariantCode);
    }

    [Fact]
    public async Task ConfirmPenalty_PreCae_ChildPending_ParentScalarNull_RebotaCreditNoteNotYetIssued()
    {
        // T-C2a: el gate de estado (AwaitingFiscalConfirmation NO esta en PostCreditNoteStatuses) sigue
        // rebotando ANTES del CAE, aunque la hija ya haya nacido con el link (Pending, invariante B2).
        using var ctx = NewDbContext();
        var h = BuildService(ctx, enableCancellationDebitNote: true);
        var (bc, _) = await SeedT5PostEmissionAsync(ctx, h, BookingCancellationCreditNoteStatus.Pending);

        Assert.Equal(BookingCancellationStatus.AwaitingFiscalConfirmation, bc.Status);

        var request = new ConfirmPenaltyRequest(
            ConceptKind: CancellationConceptKind.OperatorPenaltyPassThrough,
            ConfirmedPenaltyAmount: 5_000m,
            OperatorConfirmationDate: DateTime.UtcNow.AddDays(-1),
            DebitNotePurpose: null,
            SupportingDocumentReference: null);

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            h.Service.ConfirmPenaltyAsync(
                bc.PublicId, request, "u1", "U", requesterIsAdmin: false, CancellationToken.None,
                userCanClassifyAgencyPenalty: true));
        Assert.Equal("INV-ADR014-001", ex.InvariantCode);
    }

    [Fact]
    public async Task FeeTray_PreCae_DoesNotListT5Bc_PostCae_ListsIt()
    {
        // T-C2 sobre la bandeja de fee (sin gate de estado propio — el "boton que rebota" que C2 evita).
        using var ctx = NewDbContext();
        var h = BuildService(ctx, enableCancellationDebitNote: true);
        var (_, invoice, bc, _, _) = await SeedResolvedPartialAsync(ctx);
        SetupCreateEmitsCreditNote(h, invoice);
        await h.Service.ConfirmPartialCancellationEmissionAsync(bc.PublicId, "u1", "U", CancellationToken.None);

        var bcTracked = await ctx.BookingCancellations.SingleAsync(b => b.Id == bc.Id);
        bcTracked.ConceptKind = CancellationConceptKind.AgencyManagementFee;
        bcTracked.PenaltyStatus = PenaltyStatus.Estimated;
        await ctx.SaveChangesAsync();

        // PRE-CAE: la hija sigue Pending (sin CAE). La bandeja NO debe listar este BC (evita el "boton que
        // rebota": si lo listara, el click abriria un modal que ConfirmPenaltyAsync rechazaria).
        var preCae = await h.Service.GetCancellationsWithMissingDebitNoteAsync(CancellationToken.None);
        Assert.DoesNotContain(preCae, p => p.BookingCancellationPublicId == bc.PublicId);

        // POST-CAE: simular la aprobacion + reconciliar.
        var nc = await ctx.Invoices.SingleAsync(i => i.OriginalInvoiceId == invoice.Id);
        nc.Resultado = "A";
        nc.CAE = "cae-nc-t5";
        await ctx.SaveChangesAsync();
        await PartialCreditNoteT5Reconciliation.TryReconcileAsync(
            ctx, nc, auditService: null, NullLogger.Instance, CancellationToken.None);

        var postCae = await h.Service.GetCancellationsWithMissingDebitNoteAsync(CancellationToken.None);
        Assert.Contains(postCae, p => p.BookingCancellationPublicId == bc.PublicId);
    }
}
