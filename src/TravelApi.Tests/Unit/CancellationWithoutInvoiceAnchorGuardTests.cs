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
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// R4 (obra "anular sin factura", 2026-07-23, decisión del dueño): un <see cref="BookingCancellation"/> SIN
/// ancla fiscal (<c>OriginatingInvoiceId</c> null) JAMÁS emite Nota de Crédito/Débito ni transiciona a un
/// estado fiscal — el guard duro corre al INICIO de <c>ConfirmAsync</c> (el único punto de entrada que puede
/// mover un BC Drafted hacia el circuito de emisión). Y el GET de la ficha (<c>MapToDtoAsync</c>, alcanzado
/// por <c>GetByPublicIdAsync</c>) tiene que devolver un DTO SANO (sin <c>NullReferenceException</c>) para
/// cualquier BC, con o sin factura.
/// </summary>
public class CancellationWithoutInvoiceAnchorGuardTests
{
    private static AppDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"r4-noanchor-{Guid.NewGuid()}")
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
                OperatorRefundTimeoutDays = 60,
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

    /// <summary>
    /// Siembra DIRECTO (sin pasar por CancelServiceAsync) un BC Drafted SIN ancla fiscal, con una línea
    /// Partial que representa el receivable del operador — el estado que deja
    /// <c>RecordPartialCancellationLineAsync</c> cuando la reserva no tiene factura viva.
    /// </summary>
    private static async Task<(Reserva Reserva, Supplier Supplier, BookingCancellation Bc)> SeedUnanchoredDraftAsync(AppDbContext ctx)
    {
        var customer = new Customer { FullName = "Cliente R4", IsActive = true };
        var supplier = new Supplier { Name = "Operador R4", IsActive = true };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = "R-R4-NOANCHOR", Name = "Reserva sin factura", Status = EstadoReserva.Cancelled,
            PayerId = customer.Id, Balance = 0m,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var hotel = new HotelBooking
        {
            ReservaId = reserva.Id, SupplierId = supplier.Id, Status = WorkflowStatuses.Cancelado,
            NetCost = 40_000m, SalePrice = 60_000m, Currency = "ARS",
        };
        ctx.HotelBookings.Add(hotel);
        await ctx.SaveChangesAsync();

        var bc = new BookingCancellation
        {
            ReservaId = reserva.Id, CustomerId = customer.Id, SupplierId = supplier.Id,
            OriginatingInvoiceId = null, // obra "anular sin factura": sin ancla fiscal.
            Status = BookingCancellationStatus.Drafted, Reason = "Cancelacion parcial sin factura de venta",
            DraftedByUserId = "vendedor-1", DraftedByUserName = "Vendedor Test",
            FiscalSnapshot = new FiscalSnapshot { Source = ExchangeRateSource.Unset, FetchedAt = default },
        };
        ctx.BookingCancellations.Add(bc);
        await ctx.SaveChangesAsync();

        ctx.BookingCancellationLines.Add(new BookingCancellationLine
        {
            BookingCancellationId = bc.Id, SupplierId = supplier.Id, ServiceTable = CancellableServiceTable.Hotel,
            ServiceId = hotel.Id, Scope = BookingCancellationLineScope.Partial, Currency = "ARS",
            LineSaleAmount = 40_000m, RefundCap = 40_000m, ReceivedRefundAmount = 0m,
        });
        await ctx.SaveChangesAsync();

        return (reserva, supplier, bc);
    }

    [Fact]
    public async Task ConfirmAsync_BcSinAncla_RechazaConInvarianteClara_NuncaEmiteNiTransiciona()
    {
        await using var ctx = NewDbContext();
        var (_, _, bc) = await SeedUnanchoredDraftAsync(ctx);
        var service = BuildService(ctx);

        var request = new ConfirmCancellationRequest(
            SnapshotData: null, IsAdminOverride: false, OverrideReason: null, ApprovalRequestPublicId: null);

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            service.ConfirmAsync(bc.PublicId, request, "vendedor-1", "Vendedor", requesterIsAdmin: false, CancellationToken.None));

        Assert.Equal("INV-BC-NOANCHOR", ex.InvariantCode);

        // Nunca transicionó: sigue Drafted, sin NC, sin snapshot fiscal completo.
        var reloaded = await ctx.BookingCancellations.AsNoTracking().SingleAsync(b => b.Id == bc.Id);
        Assert.Equal(BookingCancellationStatus.Drafted, reloaded.Status);
        Assert.Null(reloaded.CreditNoteInvoiceId);
        Assert.Null(reloaded.ConfirmedWithClientAt);
    }

    [Fact]
    public async Task GetByPublicIdAsync_BcSinAncla_DevuelveDtoSano_SinNullReferenceException()
    {
        await using var ctx = NewDbContext();
        var (_, _, bc) = await SeedUnanchoredDraftAsync(ctx);
        var service = BuildService(ctx);

        // ANTES del fix (MapToDtoAsync con bc.OriginatingInvoice.PublicId sin null-check): esta llamada
        // tiraba NullReferenceException (500 técnico) para cualquier cancelación sin factura.
        var dto = await service.GetByPublicIdAsync(bc.PublicId, CancellationToken.None);

        Assert.NotNull(dto);
        Assert.Null(dto!.OriginatingInvoicePublicId);
        Assert.Equal("Drafted", dto.Status);
    }
}
