using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Decision 18/08 (tanda 3, gate backend del panel de devolucion en Presupuesto): una reserva en
/// Presupuesto todavia no tiene factura de venta, asi que nunca puede tener una devolucion de NC parcial
/// pendiente de emitir. Este test cubre el gate agregado en
/// <c>BookingCancellationService.BuildPartialCreditNoteEmissionSummaryAsync</c>: aunque por datos raros
/// exista un <see cref="BookingCancellation"/> con lineas Partial "resueltas" colgando de una reserva que
/// volvio a Presupuesto, la API nunca devuelve <c>PartialCreditNoteEmission</c> en ese estado.
/// </summary>
public class BookingCancellationBudgetGateTests
{
    private static AppDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"bc-budget-gate-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static BookingCancellationService BuildService(AppDbContext ctx)
    {
        var settingsMock = new Mock<IOperationalFinanceSettingsService>();
        settingsMock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings
            {
                EnableNewCancellationFlow = true,
                OperatorRefundTimeoutDays = 45,
                EnableCancellationDebitNote = false,
                IvaProrrateoMode = IvaProrrateoMode.ProportionalToNet,
                PartialCreditNoteRoundingTolerance = 0.02m,
            });

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

    [Fact]
    public async Task GetByPublicIdAsync_ReservaInBudget_PartialCreditNoteEmissionIsNull()
    {
        using var ctx = NewDbContext();
        ctx.AfipSettings.Add(new AfipSettings { TaxCondition = "Monotributo", Cuit = 20111111111 });

        var customer = new Customer { FullName = "Cliente Gate Budget", IsActive = true, TaxCondition = "Consumidor Final" };
        var supplier = new Supplier { Name = "Operador Gate Budget", IsActive = true, TaxCondition = "IVA_RESP_INSCRIPTO" };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        // Dato raro deliberado: la reserva volvio a Presupuesto (revertida) pero le quedo colgando un BC
        // parcial viejo con una linea YA resuelta (factura destino + monto). El gate debe cortar ANTES de
        // llegar a ese calculo, sin importar que tan "resuelto" luzca el dato historico.
        var reserva = new Reserva
        {
            NumeroReserva = "R-GATE-BUDGET",
            Name = "Reserva revertida a presupuesto",
            PayerId = customer.Id,
            Status = EstadoReserva.Budget,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var invoice = new Invoice
        {
            TipoComprobante = 11, PuntoDeVenta = 1, NumeroComprobante = 900, CAE = "cae-gate", Resultado = "A",
            MonId = "PES", MonCotiz = 1m, ImporteTotal = 50_000m, ImporteNeto = 50_000m, ImporteIva = 0m,
            ReservaId = reserva.Id, AnnulmentStatus = AnnulmentStatus.None, CreatedAt = DateTime.UtcNow,
        };
        ctx.Invoices.Add(invoice);
        await ctx.SaveChangesAsync();

        var bc = new BookingCancellation
        {
            ReservaId = reserva.Id,
            CustomerId = customer.Id,
            SupplierId = supplier.Id,
            OriginatingInvoiceId = invoice.Id,
            Status = BookingCancellationStatus.Drafted,
            Reason = "Cancelacion vieja colgada tras revertir la reserva a Presupuesto",
            DraftedAt = DateTime.UtcNow,
            DraftedByUserId = "vendedor-1",
            FiscalSnapshot = new FiscalSnapshot { Source = ExchangeRateSource.Unset, FetchedAt = default },
        };
        bc.Lines.Add(new BookingCancellationLine
        {
            SupplierId = supplier.Id,
            ServiceTable = CancellableServiceTable.Generic,
            ServiceId = 1,
            Scope = BookingCancellationLineScope.Partial,
            Currency = "ARS",
            LineSaleAmount = 50_000m,
            TargetInvoiceId = invoice.Id,
            ConfirmedGrossCreditAmount = 50_000m,
        });
        ctx.BookingCancellations.Add(bc);
        await ctx.SaveChangesAsync();

        var service = BuildService(ctx);
        var dto = await service.GetByPublicIdAsync(bc.PublicId, CancellationToken.None);

        Assert.NotNull(dto);
        Assert.Null(dto!.PartialCreditNoteEmission);
    }
}
