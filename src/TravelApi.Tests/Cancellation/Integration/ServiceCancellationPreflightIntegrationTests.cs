using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.DTOs.Cancellation;
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
/// Tanda 7 (plan de remediacion "contrato pantalla-motor", 2026-07-20) — regla dura de merge (B2 del plan):
/// el flag <c>canCancel</c> que el DTO expone por CADA servicio (voucher / R1 plata pagada al operador sin
/// factura / factura viva sin cliente asignado) depende de estado real en la base (pagos al operador,
/// facturas con CAE, vouchers Issued, Payer), asi que el cross-check contra el guard real tiene que correr
/// contra Postgres real, no InMemory ni un test puro.
///
/// <para>Este archivo seedea los 4 escenarios de la spec de pantalla (T7), pide el DTO por
/// <c>ReservaService.GetReservaByIdAsync</c> (el mismo que arma la ficha) y verifica que el flag coincide
/// EXACTO (Allowed + Reason) con lo que responde el guard real de escritura
/// (<c>BookingCancellationService.CancelServiceAsync</c>, intentado de verdad sobre la MISMA fila de
/// Postgres) — nunca contra un valor fijo. Para el caso R1 "resuelto" (se agrega la factura que faltaba)
/// verifica ademas que el guard real ACEPTA la anulacion (no solo que deja de rechazarla).</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class ServiceCancellationPreflightIntegrationTests
    : IClassFixture<PostgresIntegrationFixture>, IAsyncLifetime
{
    private readonly PostgresIntegrationFixture _fixture;

    public ServiceCancellationPreflightIntegrationTests(PostgresIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => _fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ServicioPagadoAlOperador_SinFactura_FlagBloqueaYElGuardRealRechazaConElMismoCodigo()
    {
        var (reservaId, reservaPublicId, hotelPublicId) = await SeedReservaConServicioPagadoSinFacturaAsync();

        await AssertFlagMatchesRealRejectionAsync(
            reservaId, reservaPublicId, hotelPublicId,
            expectedReason: ServiceCancellationPreflightPolicy.UnanchoredOperatorRefundBlockedReason,
            expectedCode: ServiceCancellationRejectedException.Codes.UnanchoredOperatorRefund);
    }

    [Fact]
    public async Task MismaReserva_TrasEmitirLaFacturaQueFaltaba_FlagDejaDeBloquearYElGuardRealAceptaAnular()
    {
        var (reservaId, reservaPublicId, hotelPublicId) = await SeedReservaConServicioPagadoSinFacturaAsync();

        // El camino correcto que el propio candado R1 sugiere: emitir la factura de venta que faltaba.
        // Con eso, el ancla del receivable ya existe -> R1 deja de tener fuga que cerrar.
        await using (var seedCtx = _fixture.CreateDbContext())
        {
            seedCtx.Invoices.Add(NewLiveInvoice(reservaId, 80_000m, numeroComprobante: 1));
            await seedCtx.SaveChangesAsync();
        }

        // El flag del DTO ya no bloquea por R1...
        await using (var detailCtx = _fixture.CreateDbContext())
        await using (var detailBcCtx = _fixture.CreateDbContext())
        {
            var reservaService = BuildReservaService(detailCtx, BuildBookingCancellationService(detailBcCtx));
            var dto = await reservaService.GetReservaByIdAsync(reservaId);
            var hotelDto = Assert.Single(dto.HotelBookings);

            Assert.NotNull(hotelDto.CanCancel);
            Assert.True(hotelDto.CanCancel!.Allowed, $"Motivo inesperado: {hotelDto.CanCancel.Reason}");
            Assert.Null(hotelDto.CanCancel.Reason);
        }

        // ...y el guard REAL (intentar anular de verdad) tambien acepta: el servicio queda anulado.
        await using (var actCtx = _fixture.CreateDbContext())
        {
            var bcService = BuildBookingCancellationService(actCtx);
            var result = await bcService.CancelServiceAsync(
                new CancelServiceRequest(reservaPublicId, "Hotel", hotelPublicId, "T7: ya se emitio la factura"),
                "tester", "Tester Integracion", CancellationToken.None);

            Assert.Equal(1, result.CancelledServicesCount);
        }
    }

    [Fact]
    public async Task DosServiciosMismoOperadorYMoneda_PoolInsuficiente_FlagBloqueaAmbosPorqueElGuardRealRechazariaACualquieraEvaluadoSolo()
    {
        // Hallazgo del backend-dotnet-reviewer (2026-07-20, B1): el pool pagado a UN operador se reparte
        // GREEDY entre sus servicios cuando se reconstruye la reserva ENTERA de una sola vez (correcto para
        // "anular la reserva completa", donde las lineas compiten de verdad por el mismo pool AL MISMO
        // TIEMPO). Pero el guard REAL de un servicio SUELTO lo evalua AISLADO: ve el pool COMPLETO, sin
        // competir con un hermano que todavia no se esta cancelando. Con 2 servicios del MISMO operador+moneda
        // y pool insuficiente para los dos, el reparto greedy le daba cap 0 al segundo -> el flag decia
        // "se puede anular" -> pero al clickear, el guard real (aislado, pool completo) igual lo rechazaba.
        // Este test prueba que el preflight AHORA marca bloqueados a LOS DOS (nunca de menos), cruzando cada
        // uno contra el guard real por separado.
        var (reservaId, reservaPublicId, hotelAPublicId, hotelBPublicId) =
            await SeedReservaConDosServiciosMismoOperadorPoolInsuficienteAsync();

        // (a) el flag del DTO bloquea a los DOS servicios por R1.
        await using (var detailCtx = _fixture.CreateDbContext())
        await using (var detailBcCtx = _fixture.CreateDbContext())
        {
            var reservaService = BuildReservaService(detailCtx, BuildBookingCancellationService(detailBcCtx));
            var dto = await reservaService.GetReservaByIdAsync(reservaId);
            Assert.Equal(2, dto.HotelBookings.Count);

            foreach (var hotelDto in dto.HotelBookings)
            {
                Assert.NotNull(hotelDto.CanCancel);
                Assert.False(hotelDto.CanCancel!.Allowed,
                    $"Servicio {hotelDto.PublicId} deberia estar bloqueado por R1 (pool insuficiente compartido).");
                Assert.Equal(ServiceCancellationPreflightPolicy.UnanchoredOperatorRefundBlockedReason, hotelDto.CanCancel.Reason);
            }
        }

        // (a-bis) el camino REAL de la ficha (GET /reservas/{id}/hotels) bloquea igual a los DOS.
        await using (var collectionCtx = _fixture.CreateDbContext())
        await using (var collectionBcCtx = _fixture.CreateDbContext())
        {
            var bookingService = BuildBookingService(collectionCtx, BuildBookingCancellationService(collectionBcCtx));
            var hotelesDeLaColeccion = (await bookingService.GetHotelsAsync(reservaId, CancellationToken.None)).ToList();
            Assert.Equal(2, hotelesDeLaColeccion.Count);

            foreach (var hotelDto in hotelesDeLaColeccion)
            {
                Assert.NotNull(hotelDto.CanCancel);
                Assert.False(hotelDto.CanCancel!.Allowed,
                    $"GET /reservas/{{id}}/hotels: servicio {hotelDto.PublicId} deberia estar bloqueado por R1.");
                Assert.Equal(ServiceCancellationPreflightPolicy.UnanchoredOperatorRefundBlockedReason, hotelDto.CanCancel.Reason);
            }
        }

        // (b) cruce contra el guard REAL: intentar anular CADA UNO por separado (en contextos independientes,
        // sin que el intento anterior haya mutado nada) tiene que rechazar con el mismo motivo/codigo.
        foreach (var servicePublicId in new[] { hotelAPublicId, hotelBPublicId })
        {
            await using var actCtx = _fixture.CreateDbContext();
            var bcService = BuildBookingCancellationService(actCtx);
            var ex = await Assert.ThrowsAsync<ServiceCancellationRejectedException>(() =>
                bcService.CancelServiceAsync(
                    new CancelServiceRequest(reservaPublicId, "Hotel", servicePublicId, "T7: pool compartido insuficiente"),
                    "tester", "Tester Integracion", CancellationToken.None));

            Assert.Equal(ServiceCancellationRejectedException.Codes.UnanchoredOperatorRefund, ex.Code);
            Assert.Equal(ServiceCancellationPreflightPolicy.UnanchoredOperatorRefundBlockedReason, ex.Message);
        }
    }

    [Fact]
    public async Task ServicioConVoucherEmitido_FlagBloqueaYElGuardRealRechazaConElMismoCodigo()
    {
        var (reservaId, reservaPublicId, hotelPublicId) = await SeedReservaConVoucherEmitidoAsync();

        await AssertFlagMatchesRealRejectionAsync(
            reservaId, reservaPublicId, hotelPublicId,
            expectedReason: ServiceCancellationPreflightPolicy.VoucherBlockedReason,
            expectedCode: ServiceCancellationRejectedException.Codes.VoucherLive);
    }

    [Fact]
    public async Task ReservaSinPayer_ConFacturaViva_FlagBloqueaYElGuardRealRechazaConElMismoCodigo()
    {
        var (reservaId, reservaPublicId, hotelPublicId) = await SeedReservaSinPayerConFacturaVivaAsync();

        await AssertFlagMatchesRealRejectionAsync(
            reservaId, reservaPublicId, hotelPublicId,
            expectedReason: ServiceCancellationPreflightPolicy.NoPayerBlockedReason,
            expectedCode: ServiceCancellationRejectedException.Codes.NoPayer);
    }

    // ---------- el corazon del cross-check ----------

    /// <summary>
    /// Pide el DTO (el flag <c>canCancel</c> del servicio) por LOS DOS caminos que pueden armar
    /// <c>reserva.hotelBookings[]</c> — <c>ReservaService.GetReservaByIdAsync</c> (el detalle completo) y
    /// <c>BookingService.GetHotelsAsync</c> (<c>GET /reservas/{id}/hotels</c>, el que la ficha usa DE VERDAD
    /// segun <c>useReservaDetail.js</c> — hallazgo E2E real, 2026-07-20: el primero SI calculaba el flag, el
    /// segundo NO) — y, en un contexto INDEPENDIENTE, intenta anular DE VERDAD con
    /// <c>CancelServiceAsync</c> — el guard real. Verifica que los TRES coinciden EXACTO: los dos caminos
    /// dicen bloqueado con el motivo/codigo X, y el guard real rechaza con ESE MISMO motivo/codigo.
    /// </summary>
    private async Task AssertFlagMatchesRealRejectionAsync(
        int reservaId, Guid reservaPublicId, Guid hotelPublicId, string expectedReason, string expectedCode)
    {
        await using (var detailCtx = _fixture.CreateDbContext())
        await using (var detailBcCtx = _fixture.CreateDbContext())
        {
            var reservaService = BuildReservaService(detailCtx, BuildBookingCancellationService(detailBcCtx));
            var dto = await reservaService.GetReservaByIdAsync(reservaId);
            var hotelDto = Assert.Single(dto.HotelBookings);

            Assert.NotNull(hotelDto.CanCancel);
            Assert.False(hotelDto.CanCancel!.Allowed);
            Assert.Equal(expectedReason, hotelDto.CanCancel.Reason);
        }

        await using (var collectionCtx = _fixture.CreateDbContext())
        await using (var collectionBcCtx = _fixture.CreateDbContext())
        {
            var bookingService = BuildBookingService(collectionCtx, BuildBookingCancellationService(collectionBcCtx));
            var hotelesDeLaColeccion = (await bookingService.GetHotelsAsync(reservaId, CancellationToken.None)).ToList();
            var hotelDeLaColeccion = Assert.Single(hotelesDeLaColeccion);

            Assert.NotNull(hotelDeLaColeccion.CanCancel);
            Assert.False(hotelDeLaColeccion.CanCancel!.Allowed,
                "GET /reservas/{id}/hotels (el camino real de la ficha) tiene que bloquear igual que el detalle completo.");
            Assert.Equal(expectedReason, hotelDeLaColeccion.CanCancel.Reason);
        }

        await using (var actCtx = _fixture.CreateDbContext())
        {
            var bcService = BuildBookingCancellationService(actCtx);
            var ex = await Assert.ThrowsAsync<ServiceCancellationRejectedException>(() =>
                bcService.CancelServiceAsync(
                    new CancelServiceRequest(reservaPublicId, "Hotel", hotelPublicId, "T7: intento real"),
                    "tester", "Tester Integracion", CancellationToken.None));

            Assert.Equal(expectedCode, ex.Code);
            Assert.Equal(expectedReason, ex.Message);
        }
    }

    // ---------- helpers de seed ----------

    private static Invoice NewLiveInvoice(int reservaId, decimal importeTotal, int numeroComprobante)
        => new()
        {
            TipoComprobante = 11, // Factura C, no es NC ni ND
            PuntoDeVenta = 1,
            NumeroComprobante = numeroComprobante,
            CAE = "cae-viva-t7",
            Resultado = "A",
            ImporteTotal = importeTotal,
            ReservaId = reservaId,
            AnnulmentStatus = AnnulmentStatus.None,
            CreatedAt = DateTime.UtcNow,
        };

    /// <summary>Reserva Confirmada + Payer + hotel Confirmado con SupplierPayment IMPUTADO. SIN factura de venta.</summary>
    private async Task<(int ReservaId, Guid ReservaPublicId, Guid HotelPublicId)>
        SeedReservaConServicioPagadoSinFacturaAsync()
    {
        await using var ctx = _fixture.CreateDbContext();

        var customer = new Customer { FullName = "Cliente T7", IsActive = true };
        var supplier = new Supplier { Name = "Operador T7", IsActive = true };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = "F-T7-" + Guid.NewGuid().ToString("N")[..6],
            Name = "Reserva T7 pagado sin factura",
            PayerId = customer.Id,
            Status = EstadoReserva.Confirmed,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var hotel = new HotelBooking
        {
            ReservaId = reserva.Id,
            SupplierId = supplier.Id,
            Status = "Confirmado",
            NetCost = 50_000m,
            SalePrice = 80_000m,
            Currency = "ARS",
        };
        ctx.HotelBookings.Add(hotel);
        await ctx.SaveChangesAsync();

        // Pago IMPUTADO a la reserva (entra al pool de RefundCap -> receivable real, R1 tiene que bloquear).
        ctx.SupplierPayments.Add(new SupplierPayment
        {
            SupplierId = supplier.Id,
            ReservaId = reserva.Id,
            Amount = 50_000m,
            Currency = "ARS",
            ImputedCurrency = "ARS",
            ImputedAmount = 50_000m,
            PaidAt = DateTime.UtcNow,
            Method = "Transfer",
        });
        await ctx.SaveChangesAsync();

        // Obra "candado coherente" C2 (2026-07-22): uno de los dos tests que usan este seed SI llega a
        // cancelar de verdad (tras agregar la factura que faltaba) — necesita autorizacion viva. El otro
        // (sin factura) rechaza ANTES por R1, asi que la autorizacion es inocua para el.
        await CancellationTestData.SeedLiveEditAuthorizationAsync(ctx, reserva.Id);

        return (reserva.Id, reserva.PublicId, hotel.PublicId);
    }

    /// <summary>
    /// Reserva Confirmada + Payer + DOS hoteles Confirmados del MISMO operador y la MISMA moneda, con UN
    /// SOLO pago al operador cuyo monto NO alcanza para cubrir el costo de los dos (pool insuficiente
    /// compartido). SIN factura de venta. Este es el escenario del hallazgo del reviewer (B1): el guard
    /// real, evaluado aislado sobre CUALQUIERA de los dos, ve el pool COMPLETO y rechaza — el preflight
    /// tiene que marcar bloqueados a los DOS, no solo al primero del batch.
    /// </summary>
    private async Task<(int ReservaId, Guid ReservaPublicId, Guid HotelAPublicId, Guid HotelBPublicId)>
        SeedReservaConDosServiciosMismoOperadorPoolInsuficienteAsync()
    {
        await using var ctx = _fixture.CreateDbContext();

        var customer = new Customer { FullName = "Cliente T7 pool compartido", IsActive = true };
        var supplier = new Supplier { Name = "Operador T7 pool compartido", IsActive = true };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = "F-T7PS-" + Guid.NewGuid().ToString("N")[..6],
            Name = "Reserva T7 pool compartido insuficiente",
            PayerId = customer.Id,
            Status = EstadoReserva.Confirmed,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        // Cada hotel cuesta 50.000, pero el pool pagado al operador es de solo 30.000 -> insuficiente para
        // cualquiera de los dos si se evaluan JUNTOS con reparto greedy, pero el guard real evaluado AISLADO
        // ve el pool completo (30.000) y bloquea a CUALQUIERA de los dos (30.000 > 0).
        var hotelA = new HotelBooking
        {
            ReservaId = reserva.Id, SupplierId = supplier.Id, Status = "Confirmado",
            NetCost = 50_000m, SalePrice = 80_000m, Currency = "ARS",
        };
        var hotelB = new HotelBooking
        {
            ReservaId = reserva.Id, SupplierId = supplier.Id, Status = "Confirmado",
            NetCost = 50_000m, SalePrice = 80_000m, Currency = "ARS",
        };
        ctx.HotelBookings.AddRange(hotelA, hotelB);
        await ctx.SaveChangesAsync();

        ctx.SupplierPayments.Add(new SupplierPayment
        {
            SupplierId = supplier.Id,
            ReservaId = reserva.Id,
            Amount = 30_000m,
            Currency = "ARS",
            ImputedCurrency = "ARS",
            ImputedAmount = 30_000m,
            PaidAt = DateTime.UtcNow,
            Method = "Transfer",
        });
        await ctx.SaveChangesAsync();

        return (reserva.Id, reserva.PublicId, hotelA.PublicId, hotelB.PublicId);
    }

    /// <summary>
    /// Reserva Confirmada + Payer + hotel Confirmado SIN pago al operador (R1 no puede aplicar) + voucher
    /// Issued de la reserva. Aisla el motivo: solo el voucher bloquea.
    /// </summary>
    private async Task<(int ReservaId, Guid ReservaPublicId, Guid HotelPublicId)> SeedReservaConVoucherEmitidoAsync()
    {
        await using var ctx = _fixture.CreateDbContext();

        var customer = new Customer { FullName = "Cliente T7 voucher", IsActive = true };
        var supplier = new Supplier { Name = "Operador T7 voucher", IsActive = true };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = "F-T7V-" + Guid.NewGuid().ToString("N")[..6],
            Name = "Reserva T7 voucher emitido",
            PayerId = customer.Id,
            Status = EstadoReserva.Confirmed,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var hotel = new HotelBooking
        {
            ReservaId = reserva.Id,
            SupplierId = supplier.Id,
            Status = "Confirmado",
            NetCost = 10_000m,
            SalePrice = 20_000m,
            Currency = "ARS",
        };
        ctx.HotelBookings.Add(hotel);

        ctx.Vouchers.Add(new Voucher { ReservaId = reserva.Id, Status = VoucherStatuses.Issued });
        await ctx.SaveChangesAsync();

        return (reserva.Id, reserva.PublicId, hotel.PublicId);
    }

    /// <summary>
    /// Reserva Confirmada SIN Payer + hotel Confirmado SIN pago al operador (R1 no puede aplicar) + factura
    /// de venta viva. Aisla el motivo: solo "sin cliente asignado" bloquea.
    /// </summary>
    private async Task<(int ReservaId, Guid ReservaPublicId, Guid HotelPublicId)>
        SeedReservaSinPayerConFacturaVivaAsync()
    {
        await using var ctx = _fixture.CreateDbContext();

        var supplier = new Supplier { Name = "Operador T7 sin payer", IsActive = true };
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = "F-T7NP-" + Guid.NewGuid().ToString("N")[..6],
            Name = "Reserva T7 sin payer",
            PayerId = null,
            Status = EstadoReserva.Confirmed,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var hotel = new HotelBooking
        {
            ReservaId = reserva.Id,
            SupplierId = supplier.Id,
            Status = "Confirmado",
            NetCost = 5_000m,
            SalePrice = 10_000m,
            Currency = "ARS",
        };
        ctx.HotelBookings.Add(hotel);
        ctx.Invoices.Add(NewLiveInvoice(reserva.Id, 10_000m, numeroComprobante: 1));
        await ctx.SaveChangesAsync();

        return (reserva.Id, reserva.PublicId, hotel.PublicId);
    }

    // ---------- helpers de armado de services reales ----------

    /// <summary>
    /// Arma un <see cref="ReservaService"/> real (no mockeado) apuntando al Postgres del fixture, CON el
    /// <see cref="IBookingCancellationService"/> real inyectado (necesario para que
    /// <c>StampServiceCancellationCapabilitiesAsync</c> calcule el flag T7; sin esto quedaria en null).
    /// </summary>
    private static ReservaService BuildReservaService(AppDbContext context, IBookingCancellationService cancellationService)
    {
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OperationalFinanceSettings());

        var store = new Mock<IUserStore<ApplicationUser>>();
        var userManager = new UserManager<ApplicationUser>(
            store.Object, null!, null!,
            Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(),
            null!, null!, null!, null!);

        return new ReservaService(
            context,
            new MapperConfiguration(c => c.AddProfile<MappingProfile>()).CreateMapper(),
            settings.Object,
            userManager,
            NullLogger<ReservaService>.Instance,
            permissionResolver: null,
            httpContextAccessor: null,
            autoStateService: null,
            auditService: null,
            cancellationService: cancellationService);
    }

    /// <summary>
    /// Arma un <see cref="BookingCancellationService"/> real apuntando al Postgres del fixture — el mismo
    /// tipo de armado que <c>BookingCancellationServiceTests.BuildService</c> (AFIP mockeado, el resto real).
    /// </summary>
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

    /// <summary>
    /// Arma un <see cref="BookingService"/> real apuntando al Postgres del fixture — el service que atiende
    /// de verdad <c>GET /reservas/{id}/hotels</c> (y los otros 4 endpoints de sub-coleccion por tipo), el
    /// camino que <c>useReservaDetail.js</c> usa para pintar la ficha (hallazgo E2E real, 2026-07-20).
    /// <see cref="IBookingCancellationService"/> real inyectado (necesario para que el stamp de T7 calcule
    /// el flag; sin esto quedaria en null, igual que <c>BuildReservaService</c>).
    /// </summary>
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
