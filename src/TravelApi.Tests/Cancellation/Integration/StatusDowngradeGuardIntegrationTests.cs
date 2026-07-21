using System;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Application.Mappings;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Exceptions;
using TravelApi.Domain.Reservations;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Repositories;
using TravelApi.Infrastructure.Services;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Cancellation.Integration;

/// <summary>
/// P1 "circuito proveedor" (2026-07-21): cross-check contra Postgres real del candado UNIFICADO de
/// "bajar el estado" de un servicio con R1 ("anular servicio"). Mismo motivo que
/// <c>ServiceCancellationPreflightIntegrationTests</c> corre contra Postgres y no InMemory: el
/// predicado depende de <c>SupplierPayments</c>/facturas reales en la base, y
/// <c>WorkflowStatusHelper.CountsForSupplierDebtByType</c> no se traduce a SQL en ningun lado de este
/// candado (se materializa dentro de <c>BuildCancellationLinesAsync</c>).
/// </summary>
[Trait("Category", "Integration")]
public sealed class StatusDowngradeGuardIntegrationTests
    : IClassFixture<PostgresIntegrationFixture>, IAsyncLifetime
{
    private readonly PostgresIntegrationFixture _fixture;

    public StatusDowngradeGuardIntegrationTests(PostgresIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => _fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ServicioPagadoAlOperador_SinFactura_BajarElEstadoRechazaConElMismoCodigoQueAnular()
    {
        var (reservaId, hotelId) = await SeedConfirmedPaidHotelAsync(withLiveInvoice: false);

        await using var ctx = _fixture.CreateDbContext();
        var booking = BuildBookingService(ctx, BuildBookingCancellationService(ctx));

        var ex = await Assert.ThrowsAsync<ServiceCancellationRejectedException>(() =>
            booking.UpdateHotelStatusAsync(hotelId.ToString(), "Solicitado", confirmationNumber: null, CancellationToken.None));

        Assert.Equal(ServiceCancellationRejectedException.Codes.UnanchoredOperatorRefund, ex.Code);
        Assert.Equal(ServiceCancellationPreflightPolicy.UnanchoredOperatorRefundBlockedReasonForStatusDowngrade, ex.Message);

        await using var verifyCtx = _fixture.CreateDbContext();
        var reloaded = await verifyCtx.HotelBookings.AsNoTracking().FirstAsync(h => h.Id == hotelId);
        Assert.Equal("Confirmado", reloaded.Status); // no muto
    }

    [Fact]
    public async Task MismaReserva_ConFacturaViva_BajarElEstadoSePermite()
    {
        var (reservaId, hotelId) = await SeedConfirmedPaidHotelAsync(withLiveInvoice: true);

        await using var ctx = _fixture.CreateDbContext();
        var booking = BuildBookingService(ctx, BuildBookingCancellationService(ctx));

        await booking.UpdateHotelStatusAsync(hotelId.ToString(), "Solicitado", confirmationNumber: null, CancellationToken.None);

        await using var verifyCtx = _fixture.CreateDbContext();
        var reloaded = await verifyCtx.HotelBookings.AsNoTracking().FirstAsync(h => h.Id == hotelId);
        Assert.Equal("Solicitado", reloaded.Status);
    }

    // ---------- seed ----------

    private async Task<(int ReservaId, int HotelId)> SeedConfirmedPaidHotelAsync(bool withLiveInvoice)
    {
        await using var ctx = _fixture.CreateDbContext();

        var customer = new Customer { FullName = "Cliente P1 downgrade", IsActive = true };
        var supplier = new Supplier { Name = "Operador P1 downgrade", IsActive = true };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = "F-P1DG-" + Guid.NewGuid().ToString("N")[..6],
            Name = "Reserva P1 downgrade",
            PayerId = customer.Id,
            Status = EstadoReserva.Confirmed,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var hotel = new HotelBooking
        {
            ReservaId = reserva.Id, SupplierId = supplier.Id, Status = "Confirmado",
            NetCost = 50_000m, SalePrice = 80_000m, Currency = "ARS",
        };
        ctx.HotelBookings.Add(hotel);
        await ctx.SaveChangesAsync();

        ctx.SupplierPayments.Add(new SupplierPayment
        {
            SupplierId = supplier.Id, ReservaId = reserva.Id, Amount = 50_000m, Currency = "ARS",
            ImputedCurrency = "ARS", ImputedAmount = 50_000m, PaidAt = DateTime.UtcNow, Method = "Transfer",
        });

        if (withLiveInvoice)
        {
            ctx.Invoices.Add(new Invoice
            {
                TipoComprobante = 11, PuntoDeVenta = 1, NumeroComprobante = 1,
                CAE = "cae-viva-p1-downgrade", Resultado = "A", ImporteTotal = 80_000m,
                ReservaId = reserva.Id, AnnulmentStatus = AnnulmentStatus.None, CreatedAt = DateTime.UtcNow,
            });
        }

        await ctx.SaveChangesAsync();

        return (reserva.Id, hotel.Id);
    }

    // ---------- armado de services reales (mismo patron que ServiceCancellationPreflightIntegrationTests) ----------

    private static BookingCancellationService BuildBookingCancellationService(AppDbContext context)
    {
        var invoiceMock = new Mock<IInvoiceService>();
        var settingsMock = new Mock<IOperationalFinanceSettingsService>();
        settingsMock
            .Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings
            {
                EnableNewCancellationFlow = true,
                OperatorRefundTimeoutDays = 60,
            });

        var approvalSettings = new Mock<IOperationalFinanceSettingsService>();
        approvalSettings
            .Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings());
        var approvalService = new ApprovalRequestService(context, approvalSettings.Object);

        return new BookingCancellationService(
            context,
            invoiceMock.Object,
            approvalService,
            new Mock<IAuditService>().Object,
            NullLogger<BookingCancellationService>.Instance,
            settingsMock.Object,
            new Mock<IFiscalLiquidationCalculator>().Object,
            new Mock<IAdminUserCountService>().Object);
    }

    private static BookingService BuildBookingService(AppDbContext context, IBookingCancellationService cancellationService)
    {
        var reservaServiceMock = new Mock<IReservaService>();
        reservaServiceMock.Setup(s => s.UpdateBalanceAsync(It.IsAny<int>())).Returns(Task.CompletedTask);
        reservaServiceMock.Setup(s => s.UpdateBalanceAsync(It.IsAny<int>(), It.IsAny<bool>())).Returns(Task.CompletedTask);

        var supplierServiceMock = new Mock<ISupplierService>();
        supplierServiceMock.Setup(s => s.UpdateBalanceAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        return new BookingService(
            new Repository<FlightSegment>(context),
            new Repository<HotelBooking>(context),
            new Repository<PackageBooking>(context),
            new Repository<TransferBooking>(context),
            new Repository<AssistanceBooking>(context),
            new Repository<Reserva>(context),
            new Repository<Supplier>(context),
            reservaServiceMock.Object,
            supplierServiceMock.Object,
            context,
            new MapperConfiguration(c => c.AddProfile<MappingProfile>()).CreateMapper(),
            NullLogger<BookingService>.Instance,
            permissionResolver: null,
            httpContextAccessor: null,
            settingsService: null,
            auditService: null,
            cancellationService: cancellationService);
    }
}
