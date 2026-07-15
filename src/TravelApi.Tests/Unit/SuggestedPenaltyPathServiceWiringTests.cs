using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Reservations;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Configuracion de multas de cancelacion (2026-07-14): tests de INTEGRACION (InMemory) que verifican que
/// <c>BookingCancellationService</c> efectivamente CABLEA <see cref="Supplier.PenaltyBehavior"/> hasta
/// <see cref="OperatorPenaltySituationDto.SuggestedPenaltyPath"/> — la regla PURA ya esta cubierta caso por caso
/// en <see cref="SuggestedPenaltyPathRuleTests"/>; esto protege contra una futura regresion de "cableado" (ej.
/// alguien saca el campo de la query proyectada y la regla sigue viva pero nunca se llama con el dato real).
///
/// <para>Harness propio (copia reducida del de <c>CancellationCorrectPenaltyAndSituationTests</c>) para no tocar
/// ese archivo existente.</para>
/// </summary>
public class SuggestedPenaltyPathServiceWiringTests
{
    private static AppDbContext NewDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: $"suggested-penalty-path-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    private static BookingCancellationService BuildService(AppDbContext ctx)
    {
        var settings = new OperationalFinanceSettings
        {
            EnableNewCancellationFlow = true,
            EnableCancellationDebitNote = true,
            EnableMultiCurrencyInvoicing = true,
            CancellationDebitNoteGraceDays = 15,
            CancellationDebitNoteHardWarnDays = 60,
            CancellationDebitNoteFourEyesThreshold = 2_000_000m,
        };
        var settingsMock = new Mock<IOperationalFinanceSettingsService>();
        settingsMock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>())).ReturnsAsync(settings);

        return new BookingCancellationService(
            ctx,
            new Mock<IInvoiceService>().Object,
            new Mock<IApprovalRequestService>().Object,
            new Mock<IAuditService>().Object,
            NullLogger<BookingCancellationService>.Instance,
            settingsMock.Object,
            new Mock<IFiscalLiquidationCalculator>().Object,
            new Mock<IAdminUserCountService>().Object);
    }

    /// <summary>
    /// Semilla post-NC (factura C con CAE + NC C con CAE) con el BC en la etapa de la PREGUNTA (Estimated,
    /// pendiente de decidir). <paramref name="supplierPenaltyBehavior"/> queda configurado en el operador ANTES
    /// de armar el BC, para que la query proyectada del situation-builder lo traiga.
    /// </summary>
    private static async Task<(BookingCancellation Bc, Reserva Reserva)> SeedPendingDecisionAsync(
        AppDbContext ctx, SupplierPenaltyBehavior supplierPenaltyBehavior)
    {
        var customer = new Customer { FullName = "Cliente Test", IsActive = true };
        var supplier = new Supplier
        {
            Name = "Operador X", IsActive = true,
            PenaltyOwnership = PenaltyOwnership.Operator,
            PenaltyBehavior = supplierPenaltyBehavior,
        };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = "R-SUGGEST",
            Name = "Reserva Test",
            PayerId = customer.Id,
            Status = EstadoReserva.PendingOperatorRefund,
            Balance = 0m,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var original = new Invoice
        {
            TipoComprobante = 11, PuntoDeVenta = 1, NumeroComprobante = 100,
            CAE = "12345678", Resultado = "A", MonId = "PES", MonCotiz = 1m,
            ImporteTotal = 100_000m, ImporteNeto = 100_000m, ImporteIva = 0m,
            ReservaId = reserva.Id, AnnulmentStatus = AnnulmentStatus.None,
        };
        ctx.Invoices.Add(original);
        await ctx.SaveChangesAsync();

        var creditNote = new Invoice
        {
            TipoComprobante = 13, PuntoDeVenta = 1, NumeroComprobante = 101,
            CAE = "99999999", Resultado = "A",
            ReservaId = reserva.Id, OriginalInvoiceId = original.Id,
        };
        ctx.Invoices.Add(creditNote);
        await ctx.SaveChangesAsync();

        var bc = new BookingCancellation
        {
            ReservaId = reserva.Id,
            CustomerId = customer.Id,
            SupplierId = supplier.Id,
            OriginatingInvoiceId = original.Id,
            CreditNoteInvoiceId = creditNote.Id,
            Status = BookingCancellationStatus.AwaitingOperatorRefund,
            Reason = "Cliente anulo",
            DraftedByUserId = "vendedor-1",
            ConfirmedWithClientAt = DateTime.UtcNow.AddDays(-1),
            FiscalSnapshot = new FiscalSnapshot
            {
                CurrencyAtEvent = "ARS",
                AgencyTaxConditionAtEvent = "MONOTRIBUTISTA",
                SupplierTaxConditionAtEvent = "MONOTRIBUTISTA",
                CustomerTaxConditionAtEvent = "CONSUMIDOR_FINAL",
                Source = ExchangeRateSource.Manual,
                ExchangeRateAtOriginalInvoice = 1m,
                FetchedAt = DateTime.UtcNow.AddDays(-1),
            },
        };
        ctx.BookingCancellations.Add(bc);
        await ctx.SaveChangesAsync();

        return (bc, reserva);
    }

    // ---------------------------------------------------------------------------------------------------
    // Etapa de la pregunta: el read-model sugiere segun el comportamiento configurado del operador.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task PendingDecision_SupplierRarelyCharges_DtoSuggestsProbablyNoPenalty()
    {
        await using var ctx = NewDbContext();
        var service = BuildService(ctx);
        var (_, reserva) = await SeedPendingDecisionAsync(ctx, SupplierPenaltyBehavior.RarelyCharges);

        var singular = await service.GetOperatorPenaltySituationAsync(
            reserva.PublicId, userCanClassifyOperatorPenalty: true, isCallerAdmin: true, default);
        Assert.Equal(OperatorPenaltySituationState.PendingDecision.ToString(), singular.State);
        Assert.Equal("probablyNoPenalty", singular.SuggestedPenaltyPath);

        // La version LISTA (la que realmente consume ReservaService en produccion) tiene que dar lo mismo para
        // el operador principal.
        var list = await service.GetOperatorPenaltySituationsAsync(
            reserva.PublicId, userCanClassifyOperatorPenalty: true, isCallerAdmin: true, default);
        var primary = Assert.Single(list);
        Assert.Equal("probablyNoPenalty", primary.SuggestedPenaltyPath);
    }

    [Fact]
    public async Task PendingDecision_SupplierUsuallyCharges_DtoSuggestsProbablyPenalty()
    {
        await using var ctx = NewDbContext();
        var service = BuildService(ctx);
        var (_, reserva) = await SeedPendingDecisionAsync(ctx, SupplierPenaltyBehavior.UsuallyCharges);

        var sit = await service.GetOperatorPenaltySituationAsync(
            reserva.PublicId, userCanClassifyOperatorPenalty: true, isCallerAdmin: true, default);

        Assert.Equal(OperatorPenaltySituationState.PendingDecision.ToString(), sit.State);
        Assert.Equal("probablyPenalty", sit.SuggestedPenaltyPath);
    }

    [Fact]
    public async Task PendingDecision_SupplierUnknown_DtoSuggestsNothing()
    {
        await using var ctx = NewDbContext();
        var service = BuildService(ctx);
        var (_, reserva) = await SeedPendingDecisionAsync(ctx, SupplierPenaltyBehavior.Unknown);

        var sit = await service.GetOperatorPenaltySituationAsync(
            reserva.PublicId, userCanClassifyOperatorPenalty: true, isCallerAdmin: true, default);

        Assert.Equal(OperatorPenaltySituationState.PendingDecision.ToString(), sit.State);
        Assert.Null(sit.SuggestedPenaltyPath);
    }

    // ---------------------------------------------------------------------------------------------------
    // Paso YA resuelto (multa confirmada y Nota de Debito emitida = "Done"): nunca sugiere nada, aunque el
    // operador este configurado como "casi siempre cobra". Mostrar una sugerencia sobre algo ya decidido
    // no tendria sentido.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task DoneState_SupplierUsuallyCharges_DtoSuggestsNothing()
    {
        await using var ctx = NewDbContext();
        var service = BuildService(ctx);
        var (bc, reserva) = await SeedPendingDecisionAsync(ctx, SupplierPenaltyBehavior.UsuallyCharges);

        // Lleva el BC a "confirmada con Nota de Debito ya emitida con CAE" (estado Done).
        bc.ConceptKind = CancellationConceptKind.OperatorPenaltyPassThrough;
        bc.PenaltyStatus = PenaltyStatus.Confirmed;
        bc.PenaltyAmountAtEvent = 30_000m;
        bc.PenaltyCurrencyAtEvent = "ARS";
        bc.DebitNoteStatus = DebitNoteStatus.Issued;
        bc.PenaltyConfirmedByUserId = "u";
        bc.PenaltyConfirmedByUserName = "U";
        bc.PenaltyConfirmedAt = DateTime.UtcNow;
        var nd = new Invoice
        {
            TipoComprobante = 12, PuntoDeVenta = 1, NumeroComprobante = 500,
            Resultado = "A", CAE = "77777777",
            ReservaId = bc.ReservaId, OriginalInvoiceId = bc.OriginatingInvoiceId,
        };
        ctx.Invoices.Add(nd);
        await ctx.SaveChangesAsync();
        bc.DebitNoteInvoiceId = nd.Id;
        await ctx.SaveChangesAsync();

        var sit = await service.GetOperatorPenaltySituationAsync(
            reserva.PublicId, userCanClassifyOperatorPenalty: true, isCallerAdmin: true, default);

        Assert.Equal(OperatorPenaltySituationState.Done.ToString(), sit.State);
        Assert.Null(sit.SuggestedPenaltyPath);
    }

    // ---------------------------------------------------------------------------------------------------
    // Paso YA cerrado sin multa (Waived): tampoco sugiere nada.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task WaivedState_SupplierRarelyCharges_DtoSuggestsNothing()
    {
        await using var ctx = NewDbContext();
        var service = BuildService(ctx);
        var (bc, reserva) = await SeedPendingDecisionAsync(ctx, SupplierPenaltyBehavior.RarelyCharges);

        bc.PenaltyStatus = PenaltyStatus.Waived;
        bc.PenaltyAmountAtEvent = 0m;
        bc.PenaltyConfirmedByUserId = "u";
        bc.PenaltyConfirmedByUserName = "U";
        bc.PenaltyConfirmedAt = DateTime.UtcNow;
        await ctx.SaveChangesAsync();

        var sit = await service.GetOperatorPenaltySituationAsync(
            reserva.PublicId, userCanClassifyOperatorPenalty: true, isCallerAdmin: true, default);

        Assert.Equal(OperatorPenaltySituationState.Waived.ToString(), sit.State);
        Assert.Null(sit.SuggestedPenaltyPath);
    }
}
