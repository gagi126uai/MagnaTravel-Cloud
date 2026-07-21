using System;
using System.Linq;
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
/// Tanda 7 (plan "contrato pantalla-motor", 2026-07-20): tests del NUCLEO de calculo
/// <see cref="BookingCancellationService.GetServiceCancellationPreflightAsync"/> — el short-circuit
/// obligatorio (B1 del review de arquitectura) y la reconstruccion en batch cuando no hay factura viva.
///
/// <para>El cross-check de que este calculo COINCIDE con el guard real (<c>CancelServiceAsync</c>) sobre la
/// MISMA fila corre aparte, como test de INTEGRACION Postgres (regla dura de merge):
/// <c>TravelApi.Tests.Cancellation.Integration.ServiceCancellationPreflightIntegrationTests</c>. Estos tests
/// de aca son mas baratos (InMemory) y solo verifican la FORMA del resultado (que servicios entran/salen del
/// conjunto bloqueado, que el short-circuit no reconstruye nada de mas).</para>
/// </summary>
public class ServiceCancellationPreflightServiceTests
{
    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"t7-preflight-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static BookingCancellationService BuildBcService(AppDbContext ctx)
    {
        var settingsMock = new Mock<IOperationalFinanceSettingsService>();
        settingsMock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings { EnableNewCancellationFlow = true, OperatorRefundTimeoutDays = 60 });
        return new BookingCancellationService(
            ctx, new Mock<IInvoiceService>().Object, new Mock<IApprovalRequestService>().Object,
            new Mock<IAuditService>().Object, NullLogger<BookingCancellationService>.Instance,
            settingsMock.Object, new Mock<IFiscalLiquidationCalculator>().Object,
            new Mock<IAdminUserCountService>().Object);
    }

    [Fact]
    public async Task ReservaConFacturaViva_ShortCircuit_DevuelveConjuntoVacioSinPayer()
    {
        await using var ctx = NewContext();
        var customer = new Customer { FullName = "Cliente", IsActive = true };
        var supplier = new Supplier { Name = "Operador", IsActive = true };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva { NumeroReserva = "R-T7-1", Name = "R-T7-1", PayerId = customer.Id, Status = EstadoReserva.Confirmed };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        // Servicio pagado al operador -- pero SI existe la factura, esto NUNCA deberia reconstruirse.
        var hotel = new HotelBooking
        {
            ReservaId = reserva.Id, SupplierId = supplier.Id, Status = "Confirmado",
            NetCost = 50_000m, SalePrice = 80_000m, Currency = "ARS",
        };
        ctx.HotelBookings.Add(hotel);
        ctx.SupplierPayments.Add(new SupplierPayment
        {
            SupplierId = supplier.Id, ReservaId = reserva.Id, Amount = 50_000m, Currency = "ARS",
            ImputedCurrency = "ARS", ImputedAmount = 50_000m, Method = "Transferencia",
        });
        ctx.Invoices.Add(new Invoice
        {
            TipoComprobante = 11, PuntoDeVenta = 1, NumeroComprobante = 1, CAE = "cae-1", Resultado = "A",
            ImporteTotal = 80_000m, ReservaId = reserva.Id, AnnulmentStatus = AnnulmentStatus.None, CreatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();

        var service = BuildBcService(ctx);
        var result = await service.GetServiceCancellationPreflightAsync(reserva.Id, CancellationToken.None);

        Assert.False(result.HasLiveSaleInvoiceWithoutPayer); // hay Payer -> no aplica
        Assert.Empty(result.ServicesBlockedByUnanchoredOperatorRefund); // R1 resuelto por la factura
    }

    [Fact]
    public async Task ReservaConFacturaViva_SinPayer_MarcaSinClienteYSigueSinBloquearNadaPorR1()
    {
        await using var ctx = NewContext();
        var supplier = new Supplier { Name = "Operador", IsActive = true };
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva { NumeroReserva = "R-T7-2", Name = "R-T7-2", PayerId = null, Status = EstadoReserva.Confirmed };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var hotel = new HotelBooking
        {
            ReservaId = reserva.Id, SupplierId = supplier.Id, Status = "Confirmado",
            NetCost = 1_000m, SalePrice = 2_000m, Currency = "ARS",
        };
        ctx.HotelBookings.Add(hotel);
        ctx.Invoices.Add(new Invoice
        {
            TipoComprobante = 11, PuntoDeVenta = 1, NumeroComprobante = 2, CAE = "cae-2", Resultado = "A",
            ImporteTotal = 2_000m, ReservaId = reserva.Id, AnnulmentStatus = AnnulmentStatus.None, CreatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();

        var service = BuildBcService(ctx);
        var result = await service.GetServiceCancellationPreflightAsync(reserva.Id, CancellationToken.None);

        Assert.True(result.HasLiveSaleInvoiceWithoutPayer);
        Assert.Empty(result.ServicesBlockedByUnanchoredOperatorRefund);
    }

    [Fact]
    public async Task ReservaSinFactura_ServicioPagado_ReconstruyeEnBatchYLoMarcaBloqueado()
    {
        await using var ctx = NewContext();
        var customer = new Customer { FullName = "Cliente", IsActive = true };
        // DOS operadores DISTINTOS para que el escenario sea el simple: un servicio con plata
        // pagada a SU operador (bloqueado) y otro sin nada pagado (libre). El caso de dos
        // servicios que COMPARTEN operador+moneda tiene su propio test de integracion Postgres
        // (DosServiciosMismoOperadorYMoneda_PoolInsuficiente_...): cada candidato se evalua
        // AISLADO contra el pool completo, igual que el guard real.
        var supplierPagado = new Supplier { Name = "Operador pagado", IsActive = true };
        var supplierImpago = new Supplier { Name = "Operador impago", IsActive = true };
        ctx.Customers.Add(customer);
        ctx.Suppliers.AddRange(supplierPagado, supplierImpago);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva { NumeroReserva = "R-T7-3", Name = "R-T7-3", PayerId = customer.Id, Status = EstadoReserva.Confirmed };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var hotelPagado = new HotelBooking
        {
            ReservaId = reserva.Id, SupplierId = supplierPagado.Id, Status = "Confirmado",
            NetCost = 50_000m, SalePrice = 80_000m, Currency = "ARS",
        };
        var hotelImpago = new HotelBooking
        {
            ReservaId = reserva.Id, SupplierId = supplierImpago.Id, Status = "Confirmado",
            NetCost = 10_000m, SalePrice = 15_000m, Currency = "ARS",
        };
        ctx.HotelBookings.AddRange(hotelPagado, hotelImpago);
        ctx.SupplierPayments.Add(new SupplierPayment
        {
            SupplierId = supplierPagado.Id, ReservaId = reserva.Id, Amount = 50_000m, Currency = "ARS",
            ImputedCurrency = "ARS", ImputedAmount = 50_000m, Method = "Transferencia",
        });
        await ctx.SaveChangesAsync();

        var service = BuildBcService(ctx);
        var result = await service.GetServiceCancellationPreflightAsync(reserva.Id, CancellationToken.None);

        Assert.False(result.HasLiveSaleInvoiceWithoutPayer); // sin factura, este candado no puede aplicar
        var blocked = result.ServicesBlockedByUnanchoredOperatorRefund;
        Assert.Contains((CancellableServiceTable.Hotel, hotelPagado.PublicId), blocked);
        Assert.DoesNotContain((CancellableServiceTable.Hotel, hotelImpago.PublicId), blocked); // impago: sin pool propio, RefundCap 0
    }

    [Fact]
    public async Task ReservaInexistente_DevuelveResultadoNeutroSinExplotar()
    {
        await using var ctx = NewContext();
        var service = BuildBcService(ctx);

        var result = await service.GetServiceCancellationPreflightAsync(reservaId: 999_999, CancellationToken.None);

        Assert.False(result.HasLiveSaleInvoiceWithoutPayer);
        Assert.Empty(result.ServicesBlockedByUnanchoredOperatorRefund);
    }
}
