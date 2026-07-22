using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.DTOs.Cancellation;
using TravelApi.Application.Interfaces;
using TravelApi.Application.Mappings;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Repositories;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Obra "candado coherente" (matriz 2026-07-22, decisiones C2/C3/C4 firmadas por Gaston en
/// <c>docs/architecture/2026-07-22-matriz-candado-decisiones-gaston.md</c>).
///
/// <list type="bullet">
/// <item><b>C2</b>: <c>BookingCancellationService.CancelServiceAsync</c> (anular UN servicio) ahora pasa
/// por el candado de autorizacion, cerrando el bypass real que tenia sobre una reserva Confirmada.</item>
/// <item><b>C4</b>: <c>BookingService.ConfirmHotelCostAsync</c> (y sus 4 hermanos tipados) ahora pasan por
/// el MISMO candado, sin agregar el gate de estado ADR-035 (fuera del alcance firmado).</item>
/// <item><b>C3</b>: anular la RESERVA ENTERA (<c>ReservaService.AnnulWithPaymentsToCreditAsync</c>) sigue
/// SIN candado a proposito — es un circuito propio con sus propios frenos fiscales. Este archivo lo deja
/// fijado con un test que prueba que una reserva Confirmada SIN autorizacion viva igual se puede anular.</item>
/// </list>
/// </summary>
public class CandadoCoherenteC2C4C3Tests
{
    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"candado-coherente-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    // ===================== C2 — CancelServiceAsync bajo candado =====================

    private static BookingCancellationService BuildCancellationService(AppDbContext ctx)
    {
        var invoiceMock = new Mock<IInvoiceService>();
        var settingsMock = new Mock<IOperationalFinanceSettingsService>();
        settingsMock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings
            {
                EnableNewCancellationFlow = true,
                OperatorRefundTimeoutDays = 60,
            });

        return new BookingCancellationService(
            ctx,
            invoiceMock.Object,
            new Mock<IApprovalRequestService>().Object,
            new Mock<IAuditService>().Object,
            NullLogger<BookingCancellationService>.Instance,
            settingsMock.Object,
            new Mock<IFiscalLiquidationCalculator>().Object,
            new Mock<IAdminUserCountService>().Object);
    }

    /// <summary>Reserva + hotel Confirmado, sin factura/voucher/pago al operador (ningun otro candado interfiere).</summary>
    private static async Task<(Reserva Reserva, HotelBooking Hotel)> SeedCancellableHotelAsync(
        AppDbContext ctx, string reservaStatus)
    {
        var customer = new Customer { FullName = "Cliente candado", IsActive = true };
        var supplier = new Supplier { Name = "Operador candado", IsActive = true };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = "R-CANDADO-C2",
            Name = "Reserva candado C2",
            PayerId = customer.Id,
            Status = reservaStatus,
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
        await ctx.SaveChangesAsync();

        return (reserva, hotel);
    }

    [Fact]
    public async Task CancelServiceAsync_ConfirmedReserva_WithoutAuthorization_RejectsAndDoesNotCancelService()
    {
        await using var ctx = NewContext();
        var (reserva, hotel) = await SeedCancellableHotelAsync(ctx, EstadoReserva.Confirmed);
        var service = BuildCancellationService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CancelServiceAsync(
                new CancelServiceRequest(reserva.PublicId, "Hotel", hotel.PublicId, "Intento sin destrabar"),
                "vendedor-1", "Vendedor", CancellationToken.None));

        // El bypass que esta obra cierra: antes de C2 el servicio quedaba cancelado igual. Ahora no se toca nada.
        var hotelReloaded = await ctx.HotelBookings.AsNoTracking().SingleAsync(h => h.Id == hotel.Id);
        Assert.False(TravelApi.Domain.Reservations.ServiceResolutionRules.IsCancelled(hotelReloaded));
        Assert.Empty(await ctx.ReservaEditAuthorizationChanges.ToListAsync());
    }

    [Fact]
    public async Task CancelServiceAsync_ConfirmedReserva_WithLiveAuthorization_CancelsAndRecordsChange()
    {
        await using var ctx = NewContext();
        var (reserva, hotel) = await SeedCancellableHotelAsync(ctx, EstadoReserva.Confirmed);
        ctx.ReservaEditAuthorizations.Add(new ReservaEditAuthorization
        {
            ReservaId = reserva.Id,
            Reason = "Admin destraba para anular el hotel",
            ExpiresAt = DateTime.UtcNow.AddMinutes(30),
        });
        await ctx.SaveChangesAsync();

        var service = BuildCancellationService(ctx);
        await service.CancelServiceAsync(
            new CancelServiceRequest(reserva.PublicId, "Hotel", hotel.PublicId, "Cliente baja el hotel"),
            "vendedor-1", "Vendedor", CancellationToken.None);

        var hotelReloaded = await ctx.HotelBookings.AsNoTracking().SingleAsync(h => h.Id == hotel.Id);
        Assert.True(TravelApi.Domain.Reservations.ServiceResolutionRules.IsCancelled(hotelReloaded));

        var change = Assert.Single(await ctx.ReservaEditAuthorizationChanges.AsNoTracking().ToListAsync());
        Assert.Equal(ReservaEditAuthorizationOperations.ServiceCancelled, change.Operation);
        Assert.Equal("Hotel", change.EntityType);
        Assert.Equal(hotel.Id, change.EntityId);
        Assert.Equal("vendedor-1", change.PerformedByUserId);
    }

    [Fact]
    public async Task CancelServiceAsync_ReservaInManagement_CancelsWithoutAuthorization()
    {
        // ADR-033/ADR-036: solo Confirmed queda bajo candado de autorizacion. En gestion es la OTRA etapa
        // "operativamente viva" donde CancelServiceAsync es valido (IsCollectableStatus) — sin candado.
        await using var ctx = NewContext();
        var (reserva, hotel) = await SeedCancellableHotelAsync(ctx, EstadoReserva.InManagement);
        var service = BuildCancellationService(ctx);

        await service.CancelServiceAsync(
            new CancelServiceRequest(reserva.PublicId, "Hotel", hotel.PublicId, "Cliente baja el hotel"),
            "vendedor-1", "Vendedor", CancellationToken.None);

        var hotelReloaded = await ctx.HotelBookings.AsNoTracking().SingleAsync(h => h.Id == hotel.Id);
        Assert.True(TravelApi.Domain.Reservations.ServiceResolutionRules.IsCancelled(hotelReloaded));
        // Etapa libre: el candado es no-op, no deja rastro de autorizacion.
        Assert.Empty(await ctx.ReservaEditAuthorizationChanges.ToListAsync());
    }

    // ===================== C4 — ConfirmHotelCostAsync bajo candado =====================

    private static IMapper CreateMapper() =>
        new MapperConfiguration(config => config.AddProfile<MappingProfile>()).CreateMapper();

    private static IHttpContextAccessor BuildHttpContextAccessor(string userId) => new HttpContextAccessor
    {
        HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId) }, "Test"))
        }
    };

    private static IUserPermissionResolver BuildPermissionResolver(string userId, params string[] permissions)
    {
        var mock = new Mock<IUserPermissionResolver>();
        IReadOnlySet<string> set = new HashSet<string>(permissions);
        mock.Setup(r => r.GetPermissionsAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(set);
        return mock.Object;
    }

    private static BookingService BuildBookingService(AppDbContext ctx, string userId = "cajero-1")
    {
        var reservaServiceMock = new Mock<IReservaService>();
        reservaServiceMock.Setup(s => s.UpdateBalanceAsync(It.IsAny<int>())).Returns(Task.CompletedTask);
        reservaServiceMock.Setup(s => s.UpdateBalanceAsync(It.IsAny<int>(), It.IsAny<bool>())).Returns(Task.CompletedTask);
        var supplierServiceMock = new Mock<ISupplierService>();
        supplierServiceMock.Setup(s => s.UpdateBalanceAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var settingsMock = new Mock<IOperationalFinanceSettingsService>();
        settingsMock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings { EnableCatalogFindOrCreate = true });

        return new BookingService(
            new Repository<FlightSegment>(ctx),
            new Repository<HotelBooking>(ctx),
            new Repository<PackageBooking>(ctx),
            new Repository<TransferBooking>(ctx),
            new Repository<AssistanceBooking>(ctx),
            new Repository<Reserva>(ctx),
            new Repository<Supplier>(ctx),
            reservaServiceMock.Object,
            supplierServiceMock.Object,
            ctx,
            CreateMapper(),
            NullLogger<BookingService>.Instance,
            BuildPermissionResolver(userId, Permissions.CobranzasSeeCost),
            BuildHttpContextAccessor(userId),
            settingsMock.Object);
    }

    /// <summary>Reserva + hotel marcado "a confirmar" (CostToConfirm), sin catalogo (RateId null, valido).</summary>
    private static async Task<(Reserva Reserva, HotelBooking Hotel)> SeedHotelToConfirmAsync(
        AppDbContext ctx, string reservaStatus)
    {
        var supplier = new Supplier { Name = "Operador confirm-cost", IsActive = true };
        ctx.Suppliers.Add(supplier);
        var reserva = new Reserva
        {
            NumeroReserva = "R-CANDADO-C4",
            Name = "Reserva candado C4",
            Status = reservaStatus,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var hotel = new HotelBooking
        {
            ReservaId = reserva.Id,
            SupplierId = supplier.Id,
            Status = "Confirmado",
            NetCost = 0m,
            SalePrice = 400m,
            Currency = "ARS",
            CostToConfirm = true,
            CostToConfirmReason = "NoKnownCost",
        };
        ctx.HotelBookings.Add(hotel);
        await ctx.SaveChangesAsync();

        return (reserva, hotel);
    }

    [Fact]
    public async Task ConfirmHotelCostAsync_ConfirmedReserva_WithoutAuthorization_RejectsAndDoesNotConfirmCost()
    {
        await using var ctx = NewContext();
        var (reserva, hotel) = await SeedHotelToConfirmAsync(ctx, EstadoReserva.Confirmed);
        var service = BuildBookingService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ConfirmHotelCostAsync(
                reserva.Id.ToString(), hotel.PublicId.ToString(),
                new ConfirmCostRequest(NetCost: 200m, Tax: 30m), CancellationToken.None));

        var hotelReloaded = await ctx.HotelBookings.AsNoTracking().SingleAsync(h => h.Id == hotel.Id);
        Assert.True(hotelReloaded.CostToConfirm); // sigue marcado: el candado corto ANTES de tocar el costo
        Assert.Equal(0m, hotelReloaded.NetCost);
        Assert.Empty(await ctx.ReservaEditAuthorizationChanges.ToListAsync());
    }

    [Fact]
    public async Task ConfirmHotelCostAsync_ConfirmedReserva_WithLiveAuthorization_ConfirmsAndRecordsChange()
    {
        await using var ctx = NewContext();
        var (reserva, hotel) = await SeedHotelToConfirmAsync(ctx, EstadoReserva.Confirmed);
        ctx.ReservaEditAuthorizations.Add(new ReservaEditAuthorization
        {
            ReservaId = reserva.Id,
            Reason = "Admin destraba para confirmar el costo",
            ExpiresAt = DateTime.UtcNow.AddMinutes(30),
        });
        await ctx.SaveChangesAsync();

        var service = BuildBookingService(ctx);
        var dto = await service.ConfirmHotelCostAsync(
            reserva.Id.ToString(), hotel.PublicId.ToString(),
            new ConfirmCostRequest(NetCost: 200m, Tax: 30m), CancellationToken.None);

        Assert.False(dto.CostToConfirm);
        var hotelReloaded = await ctx.HotelBookings.AsNoTracking().SingleAsync(h => h.Id == hotel.Id);
        Assert.False(hotelReloaded.CostToConfirm);
        Assert.Equal(200m, hotelReloaded.NetCost);

        var change = Assert.Single(await ctx.ReservaEditAuthorizationChanges.AsNoTracking().ToListAsync());
        Assert.Equal(ReservaEditAuthorizationOperations.ServiceCostConfirmed, change.Operation);
        Assert.Equal("HotelBooking", change.EntityType);
        Assert.Equal(hotel.Id, change.EntityId);
    }

    [Fact]
    public async Task ConfirmHotelCostAsync_ReservaNotConfirmed_ConfirmsWithoutAuthorization()
    {
        // Default de la entidad Reserva: EstadoReserva.Budget (etapa libre, sin candado).
        await using var ctx = NewContext();
        var (reserva, hotel) = await SeedHotelToConfirmAsync(ctx, EstadoReserva.Budget);
        var service = BuildBookingService(ctx);

        var dto = await service.ConfirmHotelCostAsync(
            reserva.Id.ToString(), hotel.PublicId.ToString(),
            new ConfirmCostRequest(NetCost: 200m, Tax: 30m), CancellationToken.None);

        Assert.False(dto.CostToConfirm);
        Assert.Empty(await ctx.ReservaEditAuthorizationChanges.ToListAsync());
    }

    // ===================== C3 — anular la reserva ENTERA sigue SIN candado =====================

    private static UserManager<ApplicationUser> BuildUserManager()
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        return new UserManager<ApplicationUser>(
            store.Object, null!, null!,
            Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(),
            null!, null!, null!, null!);
    }

    private static ReservaService BuildReservaService(AppDbContext ctx)
    {
        var settingsMock = new Mock<IOperationalFinanceSettingsService>();
        settingsMock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings());
        var mapper = new Mock<IMapper>();
        mapper.Setup(m => m.Map<ReservaDto>(It.IsAny<Reserva>()))
            .Returns((Reserva r) => new ReservaDto { PublicId = r.PublicId, Name = r.Name, Status = r.Status });

        return new ReservaService(ctx, mapper.Object, settingsMock.Object, BuildUserManager(),
            NullLogger<ReservaService>.Instance);
    }

    [Fact]
    public async Task AnnulWithPaymentsToCreditAsync_ConfirmedReserva_WithoutAuthorization_StillSucceeds()
    {
        // C3 (matriz candado 2026-07-22): el circuito de anular la RESERVA ENTERA es propio y NO pasa por
        // el candado de autorizacion — tiene sus propios frenos fiscales (factura viva, plata al operador).
        // Esta reserva no tiene factura ni pagos: nada bloquea la baja directa.
        await using var ctx = NewContext();
        var reserva = new Reserva
        {
            NumeroReserva = "R-CANDADO-C3",
            Name = "Reserva candado C3",
            Status = EstadoReserva.Confirmed,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var service = BuildReservaService(ctx);

        // NO se sembro ninguna ReservaEditAuthorization: si el candado se colara aca, esto tiraria 409.
        await service.AnnulWithPaymentsToCreditAsync(
            reserva.Id.ToString(), "El cliente decidio no viajar mas", actorUserId: null, actorUserName: null,
            CancellationToken.None);

        var reservaReloaded = await ctx.Reservas.AsNoTracking().SingleAsync(r => r.Id == reserva.Id);
        Assert.Equal(EstadoReserva.Cancelled, reservaReloaded.Status);
        Assert.Empty(await ctx.ReservaEditAuthorizationChanges.ToListAsync());
    }
}
