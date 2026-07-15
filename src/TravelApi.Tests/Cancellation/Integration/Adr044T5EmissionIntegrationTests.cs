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
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Cancellation.Integration;

/// <summary>
/// ADR-044 T5-emision (2026-07-15, diseño §9): tests de INTEGRACION contra Postgres real
/// (<see cref="PostgresIntegrationFixture"/>) de lo que InMemory NO puede validar — el candado
/// <c>SELECT ... FOR UPDATE</c> serializando emisiones concurrentes sobre la MISMA factura, y la reversion
/// economica real via <see cref="AfipService"/> (<see cref="AfipService.ApplyCreditNoteEconomicReversalAsync"/>
/// es <c>internal</c>, visible aca por <c>InternalsVisibleTo</c>).
///
/// <para><b>Como se aisla ARCA</b>: mismo criterio que el resto del modulo — <see cref="IInvoiceService"/> se
/// mockea (<c>CreateAsync</c> persiste una NC real PENDING en la BD del fixture, igual que produccion); el CAE
/// se simula editando <c>Resultado</c>/<c>CAE</c> a mano (el SOAP real no corre) y llamando al reconciliador
/// dedicado <see cref="PartialCreditNoteT5Reconciliation"/> DIRECTO — el mismo patron que el resto de la suite
/// usa para <c>OnArcaSucceededAsync</c> (ver <c>CancellationFlowE2ETests</c>). Esto corre el flujo REAL de
/// dominio de punta a punta (captura → confirmar/emitir → CAE simulado), NO seeds a mano del estado del BC.</para>
///
/// <para><b>NO CORRE LOCAL</b> (requiere Docker + Postgres). Compila y corre en el VPS / CI, como el resto del
/// modulo FC1/ADR-044.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class Adr044T5EmissionIntegrationTests
    : IClassFixture<PostgresIntegrationFixture>, IAsyncLifetime
{
    private readonly PostgresIntegrationFixture _fixture;

    public Adr044T5EmissionIntegrationTests(PostgresIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => _fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    // =========================================================================
    // Builders
    // =========================================================================

    private static BookingCancellationService BuildService(AppDbContext ctx, Mock<IInvoiceService> invoiceMock)
    {
        var settingsMock = new Mock<IOperationalFinanceSettingsService>();
        settingsMock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings
            {
                EnableNewCancellationFlow = true,
                OperatorRefundTimeoutDays = 45,
                IvaProrrateoMode = IvaProrrateoMode.ProportionalToNet,
                PartialCreditNoteRoundingTolerance = 0.02m,
            });

        return new BookingCancellationService(
            ctx, invoiceMock.Object,
            new ApprovalRequestService(ctx, settingsMock.Object),
            new Mock<IAuditService>().Object,
            NullLogger<BookingCancellationService>.Instance,
            settingsMock.Object,
            new Mock<IFiscalLiquidationCalculator>().Object,
            new Mock<IAdminUserCountService>().Object);
    }

    /// <summary>Mock de IInvoiceService.CreateAsync que persiste una NC PENDING real (mismo contrato que produccion).</summary>
    private static Mock<IInvoiceService> BuildInvoiceServiceMock(AppDbContext ctx)
    {
        var mock = new Mock<IInvoiceService>();
        mock.Setup(s => s.CreateAsync(
                It.IsAny<CreateInvoiceRequest>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns<CreateInvoiceRequest, string?, string?, CancellationToken>(async (req, uid, uname, ct) =>
            {
                var original = await ctx.Invoices.FirstAsync(i => i.PublicId.ToString() == req.OriginalInvoiceId, ct);
                var nc = new Invoice
                {
                    ReservaId = original.ReservaId,
                    TipoComprobante = original.TipoComprobante switch { 1 => 3, 6 => 8, 11 => 13, 51 => 53, _ => 8 },
                    PuntoDeVenta = original.PuntoDeVenta,
                    NumeroComprobante = 0,
                    ImporteTotal = req.TotalsOverride?.ImpTotal ?? req.Items.Sum(i => i.Total),
                    ImporteNeto = req.TotalsOverride?.ImpNeto ?? 0m,
                    ImporteIva = req.TotalsOverride?.ImpIVA ?? 0m,
                    MonId = req.MonId,
                    MonCotiz = req.MonCotiz,
                    OriginalInvoiceId = original.Id,
                    Resultado = "PENDING",
                    CreatedAt = DateTime.UtcNow,
                };
                ctx.Invoices.Add(nc);
                await ctx.SaveChangesAsync(ct);
                return new InvoiceDto { PublicId = nc.PublicId };
            });
        return mock;
    }

    /// <summary>
    /// Esqueleto minimo: agencia Monotributo, cliente, operador, reserva, factura de venta viva con UN item
    /// (mono-alicuota) y un BC T5 Drafted con una linea Partial resuelta.
    /// </summary>
    private static async Task<(Guid ReservaPublicId, int ReservaId, int InvoiceId, Guid BcPublicId, int BcId)>
        SeedResolvedPartialAsync(
            PostgresIntegrationFixture fixture,
            decimal invoiceTotal,
            decimal confirmedAmount,
            string invoiceMonId = "PES",
            decimal invoiceMonCotiz = 1m)
    {
        await using var seed = fixture.CreateDbContext();

        seed.AfipSettings.Add(new AfipSettings { TaxCondition = "Monotributo", Cuit = 20111111111 });

        var (customerId, supplierId, reservaId, invoiceId) = await CancellationTestData.SeedBaseAsync(seed);

        var invoice = await seed.Invoices.FirstAsync(i => i.Id == invoiceId);
        invoice.ImporteTotal = invoiceTotal;
        invoice.ImporteNeto = invoiceTotal;
        invoice.ImporteIva = 0m;
        invoice.TipoComprobante = 11; // Factura C (Monotributo)
        invoice.MonId = invoiceMonId;
        invoice.MonCotiz = invoiceMonCotiz;
        if (invoiceMonId != "PES")
        {
            invoice.ExchangeRateSource = ExchangeRateSource.BNA_VendedorDivisa;
            invoice.ExchangeRateJustification = "TC del dia habil anterior (test)";
            invoice.ExchangeRateFetchedAt = DateTime.UtcNow;
        }
        seed.Set<InvoiceItem>().Add(new InvoiceItem
        {
            InvoiceId = invoice.Id,
            Description = "Hotel test",
            Quantity = 1,
            UnitPrice = invoiceTotal,
            Total = invoiceTotal,
            AlicuotaIvaId = 3,
        });
        await seed.SaveChangesAsync();

        var bc = new BookingCancellation
        {
            ReservaId = reservaId,
            CustomerId = customerId,
            SupplierId = supplierId,
            OriginatingInvoiceId = invoiceId,
            Status = BookingCancellationStatus.Drafted,
            Reason = "Cancelacion parcial de servicio (integracion)",
            DraftedAt = DateTime.UtcNow,
            DraftedByUserId = "vendedor-1",
            FiscalSnapshot = new FiscalSnapshot { Source = ExchangeRateSource.Unset, FetchedAt = default },
        };
        bc.Lines.Add(new BookingCancellationLine
        {
            SupplierId = supplierId,
            ServiceTable = CancellableServiceTable.Hotel,
            ServiceId = 1,
            Scope = BookingCancellationLineScope.Partial,
            Currency = invoiceMonId == "PES" ? "ARS" : "USD",
            LineSaleAmount = confirmedAmount,
            TargetInvoiceId = invoiceId,
            ConfirmedGrossCreditAmount = confirmedAmount,
        });
        seed.BookingCancellations.Add(bc);
        await seed.SaveChangesAsync();

        var reserva = await seed.Reservas.FirstAsync(r => r.Id == reservaId);
        return (reserva.PublicId, reservaId, invoiceId, bc.PublicId, bc.Id);
    }

    private static async Task SimulateArcaAndReconcileAsync(
        PostgresIntegrationFixture fixture, int invoiceId, string resultado, string? cae = "68000000009999")
    {
        await using var ctx = fixture.CreateDbContext();
        var nc = await ctx.Invoices.FirstAsync(i => i.OriginalInvoiceId == invoiceId);
        nc.Resultado = resultado;
        if (resultado == "A")
        {
            nc.CAE = cae;
            nc.VencimientoCAE = DateTime.UtcNow.AddDays(10);
        }
        else
        {
            nc.Observaciones = "Rechazo simulado (test integracion)";
        }
        await ctx.SaveChangesAsync();

        await PartialCreditNoteT5Reconciliation.TryReconcileAsync(
            ctx, nc, auditService: null, NullLogger.Instance, CancellationToken.None);

        if (resultado == "A")
        {
            // La reversion economica corre en produccion desde AfipService.ProcessInvoiceJob, disparada al
            // conseguir el CAE. Se invoca aca DIRECTO (mismo momento, mismo dato) para no depender del SOAP.
            var afip = new AfipService(
                ctx, NullLogger<AfipService>.Instance, new System.Net.Http.HttpClient(),
                new Mock<ISensitiveDataProtector>().Object, auditService: null);
            await afip.ApplyCreditNoteEconomicReversalAsync(nc.Id);
        }
    }

    // =========================================================================
    // Test obligatorio §9.6 — invariante AnnulmentStatus, flujo REAL end-to-end.
    // =========================================================================

    [Fact]
    public async Task AnnulmentStatusInvariant_PartialWithRemainingServices_InvoiceStaysLive()
    {
        var (_, _, invoiceId, bcPublicId, _) = await SeedResolvedPartialAsync(
            _fixture, invoiceTotal: 100_000m, confirmedAmount: 30_000m);

        await using (var ctx = _fixture.CreateDbContext())
        {
            var invoiceMock = BuildInvoiceServiceMock(ctx);
            var service = BuildService(ctx, invoiceMock);
            await service.ConfirmPartialCancellationEmissionAsync(bcPublicId, "u1", "U", CancellationToken.None);
        }

        await SimulateArcaAndReconcileAsync(_fixture, invoiceId, resultado: "A");

        await using var verify = _fixture.CreateDbContext();
        var invoice = await verify.Invoices.AsNoTracking().FirstAsync(i => i.Id == invoiceId);
        Assert.NotEqual(AnnulmentStatus.Succeeded, invoice.AnnulmentStatus); // sigue viva, cobrable/facturable.
    }

    [Fact]
    public async Task AnnulmentStatusInvariant_PartialConsumesFullRemainder_InvoiceMarkedSucceeded()
    {
        var (_, _, invoiceId, bcPublicId, _) = await SeedResolvedPartialAsync(
            _fixture, invoiceTotal: 30_000m, confirmedAmount: 30_000m); // ultima (unica) porcion.

        await using (var ctx = _fixture.CreateDbContext())
        {
            var invoiceMock = BuildInvoiceServiceMock(ctx);
            var service = BuildService(ctx, invoiceMock);
            await service.ConfirmPartialCancellationEmissionAsync(bcPublicId, "u1", "U", CancellationToken.None);
        }

        await SimulateArcaAndReconcileAsync(_fixture, invoiceId, resultado: "A");

        await using var verify = _fixture.CreateDbContext();
        var invoice = await verify.Invoices.AsNoTracking().FirstAsync(i => i.Id == invoiceId);
        Assert.Equal(AnnulmentStatus.Succeeded, invoice.AnnulmentStatus);
    }

    // =========================================================================
    // Test obligatorio §9.7 + T-derivacion-borde — reversion parcial (NO cascade-void), incluso en la
    // ULTIMA porcion (borde §15-IR): el discriminador de §6.2a debe leer la HIJA, no el monto.
    // =========================================================================

    [Fact]
    public async Task PartialReversal_LastPortion_DoesNotCascadeVoidReceipts()
    {
        var (_, reservaId, invoiceId, bcPublicId, _) = await SeedResolvedPartialAsync(
            _fixture, invoiceTotal: 30_000m, confirmedAmount: 30_000m); // ultima porcion == monto EXACTO.

        // Recibo VIVO cuyo monto coincide EXACTO con la NC — el escenario que, si el discriminador cayera
        // (por bug) al fallback por monto en el borde de la ultima porcion, cruzaria a reversal TOTAL y
        // cascade-voidearia este recibo. El comportamiento CORRECTO para T5 es dejarlo vivo siempre (el
        // recibo cubre TODA la reserva, no solo el servicio cancelado).
        int paymentId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var payment = new Payment
            {
                ReservaId = reservaId, Amount = 30_000m, PaidAt = DateTime.UtcNow.AddDays(-5),
                Method = "Transfer", Status = "Paid",
                EntryType = PaymentEntryTypes.Payment, AffectsCash = true,
                RelatedInvoiceId = invoiceId,
            };
            seed.Payments.Add(payment);
            await seed.SaveChangesAsync();
            seed.PaymentReceipts.Add(new PaymentReceipt
            {
                PaymentId = payment.Id, ReservaId = reservaId, ReceiptNumber = "R-T5-1",
                Amount = 30_000m, Status = PaymentReceiptStatuses.Issued, IssuedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
            paymentId = payment.Id;
        }

        await using (var ctx = _fixture.CreateDbContext())
        {
            var invoiceMock = BuildInvoiceServiceMock(ctx);
            var service = BuildService(ctx, invoiceMock);
            await service.ConfirmPartialCancellationEmissionAsync(bcPublicId, "u1", "U", CancellationToken.None);
        }

        await SimulateArcaAndReconcileAsync(_fixture, invoiceId, resultado: "A");

        await using var verify = _fixture.CreateDbContext();
        // El recibo original SIGUE Issued: la reversion T5 nunca cascade-voidea (politica F2.3, aplicada
        // deterministicamente por el discriminador de la hija, no por el monto).
        var receipt = await verify.PaymentReceipts.AsNoTracking().FirstAsync(r => r.PaymentId == paymentId);
        Assert.Equal(PaymentReceiptStatuses.Issued, receipt.Status);

        // Y se creo el Payment reversal (efecto economico real, capeado a lo cobrado).
        var reversal = await verify.Payments.AsNoTracking()
            .FirstOrDefaultAsync(p => p.EntryType == PaymentEntryTypes.CreditNoteReversal && p.ReservaId == reservaId);
        Assert.NotNull(reversal);
        Assert.True(reversal!.Amount < 0m);
    }

    // =========================================================================
    // Test obligatorio §9.10 — concurrencia real: dos T5 (BCs distintos) sobre la MISMA factura no pueden
    // sobre-acreditar. Requiere el FOR UPDATE real de Postgres (InMemory no lo serializa).
    // =========================================================================

    [Fact]
    public async Task Concurrency_TwoT5EmissionsSameInvoice_Serialized_NeverOverCredit()
    {
        // Factura de 100 con dos eventos de cancelacion parcial de 60 cada uno (cada uno en su PROPIO BC,
        // Decision C): sin el lock, ambos verian el remanente completo (100) y confirmarian 60+60=120>100.
        Guid reservaPublicId; int reservaId; int invoiceId;
        await using (var seed = _fixture.CreateDbContext())
        {
            seed.AfipSettings.Add(new AfipSettings { TaxCondition = "Monotributo", Cuit = 20111111111 });
            var (customerId, supplierId, resId, invId) = await CancellationTestData.SeedBaseAsync(seed);
            var invoice = await seed.Invoices.FirstAsync(i => i.Id == invId);
            invoice.ImporteTotal = 100m;
            invoice.ImporteNeto = 100m;
            invoice.ImporteIva = 0m;
            invoice.TipoComprobante = 11;
            seed.Set<InvoiceItem>().Add(new InvoiceItem
            {
                InvoiceId = invId, Description = "Hotel", Quantity = 1, UnitPrice = 100m, Total = 100m, AlicuotaIvaId = 3,
            });
            await seed.SaveChangesAsync();

            BookingCancellation NewBc(int lineServiceId, decimal amount) => new()
            {
                ReservaId = resId, CustomerId = customerId, SupplierId = supplierId, OriginatingInvoiceId = invId,
                Status = BookingCancellationStatus.Drafted, Reason = "Cancelacion parcial concurrente",
                DraftedAt = DateTime.UtcNow, DraftedByUserId = "vendedor-1",
                FiscalSnapshot = new FiscalSnapshot { Source = ExchangeRateSource.Unset, FetchedAt = default },
                Lines =
                {
                    new BookingCancellationLine
                    {
                        SupplierId = supplierId, ServiceTable = CancellableServiceTable.Hotel, ServiceId = lineServiceId,
                        Scope = BookingCancellationLineScope.Partial, Currency = "ARS", LineSaleAmount = amount,
                        TargetInvoiceId = invId, ConfirmedGrossCreditAmount = amount,
                    },
                },
            };
            var bc1 = NewBc(1, 60m);
            var bc2 = NewBc(2, 60m);
            seed.BookingCancellations.AddRange(bc1, bc2);
            await seed.SaveChangesAsync();

            var reserva = await seed.Reservas.FirstAsync(r => r.Id == resId);
            reservaPublicId = reserva.PublicId;
            reservaId = resId;
            invoiceId = invId;
        }

        var bcPublicIds = new System.Collections.Generic.List<Guid>();
        await using (var ctxList = _fixture.CreateDbContext())
        {
            bcPublicIds = await ctxList.BookingCancellations.AsNoTracking()
                .Where(b => b.ReservaId == reservaId).Select(b => b.PublicId).ToListAsync();
        }
        Assert.Equal(2, bcPublicIds.Count);

        await using (var ctxA = _fixture.CreateDbContext())
        await using (var ctxB = _fixture.CreateDbContext())
        {
            var serviceA = BuildService(ctxA, BuildInvoiceServiceMock(ctxA));
            var serviceB = BuildService(ctxB, BuildInvoiceServiceMock(ctxB));

            async Task<bool> RunSwallowingCapAsync(BookingCancellationService svc, Guid publicId)
            {
                try
                {
                    await svc.ConfirmPartialCancellationEmissionAsync(publicId, "cajero", "Cajero", CancellationToken.None);
                    return true;
                }
                catch (TravelApi.Domain.Exceptions.BusinessInvariantViolationException ex)
                    when (ex.InvariantCode == "INV-T5-EMIT-CAP")
                {
                    return false; // la otra emision gano la carrera y consumio el remanente.
                }
            }

            var results = await Task.WhenAll(
                RunSwallowingCapAsync(serviceA, bcPublicIds[0]),
                RunSwallowingCapAsync(serviceB, bcPublicIds[1]));

            // Outcome exacto: exactamente UNA de las dos confirma (60 <= 100 pero 60+60 > 100).
            Assert.Equal(1, results.Count(r => r));
        }

        await using var verify = _fixture.CreateDbContext();
        var childrenAmounts = await verify.BookingCancellationCreditNotes.AsNoTracking()
            .Where(c => c.OriginatingInvoiceId == invoiceId)
            .ToListAsync();
        Assert.Single(childrenAmounts); // solo la ganadora creo su hija.
    }

    // =========================================================================
    // Test obligatorio §9.13 — herencia de moneda/TC: factura destino en USD, la NC sale en DOL con el
    // MonCotiz heredado (nunca recotizado).
    // =========================================================================

    [Fact]
    public async Task CurrencyInheritance_ForeignInvoice_NcInheritsMonIdAndMonCotiz()
    {
        var (_, _, invoiceId, bcPublicId, _) = await SeedResolvedPartialAsync(
            _fixture, invoiceTotal: 500m, confirmedAmount: 200m, invoiceMonId: "DOL", invoiceMonCotiz: 1180m);

        await using (var ctx = _fixture.CreateDbContext())
        {
            var invoiceMock = BuildInvoiceServiceMock(ctx);
            var service = BuildService(ctx, invoiceMock);
            var dto = await service.ConfirmPartialCancellationEmissionAsync(bcPublicId, "u1", "U", CancellationToken.None);
            Assert.Equal("USD", dto.FiscalSnapshot?.CurrencyAtEvent);
            Assert.Equal(1180m, dto.FiscalSnapshot?.ExchangeRateAtOriginalInvoice);
        }

        await using var verify = _fixture.CreateDbContext();
        var nc = await verify.Invoices.AsNoTracking().FirstAsync(i => i.OriginalInvoiceId == invoiceId);
        Assert.Equal("DOL", nc.MonId);
        Assert.Equal(1180m, nc.MonCotiz);
    }
}
