using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Application.Mappings;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Exceptions;
using TravelApi.Domain.Reservations;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Repositories;
using TravelApi.Infrastructure.Reservations;
using TravelApi.Infrastructure.Services;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Cancellation.Integration;

/// <summary>
/// Fix B1 "circuito proveedor" (review 2026-07-21): cross-check contra Postgres real de que
/// <c>BookingService.PersistServiceEditAndRefreshSupplierBalanceAsync</c> es realmente atómico. El caso
/// que ANTES del fix dejaba un commit parcial: subir el <c>NetCost</c> de un servicio (o reasignarlo)
/// elimina el sobrepago del operador, pero ese sobrepago YA se aplicó como saldo a favor a otra reserva
/// -> <c>SupplierCreditReconciler</c> rechaza con <c>INV-SUPCREDIT-001</c>. InMemory no puede validar el
/// rollback real (no soporta transacciones); acá se valida contra Postgres que la edición del servicio
/// NO queda guardada cuando el reconciler rechaza.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CostBelowPaidReconcilerRollbackIntegrationTests : IClassFixture<PostgresIntegrationFixture>
{
    private readonly PostgresIntegrationFixture _fixture;

    public CostBelowPaidReconcilerRollbackIntegrationTests(PostgresIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task SubirCostoQueEliminaSaldoAFavorYaAplicado_Rechaza409_NoPersisteLaEdicion()
    {
        var (reservaId, hotelId, supplierPublicId) = await SeedHotelWithAlreadyAppliedOverpaymentAsync();

        await using var ctx = _fixture.CreateDbContext();
        var booking = BuildBookingService(ctx);

        var request = BuildHotelUpdate(supplierPublicId.ToString(), netCost: 50_000m);

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            booking.UpdateHotelAsync(reservaId, hotelId, request, CancellationToken.None));

        Assert.Equal("INV-SUPCREDIT-001", ex.InvariantCode);

        // La transacción enrolló TODO: el costo del hotel sigue en 30.000, NO quedó la mitad guardada.
        await using var verifyCtx = _fixture.CreateDbContext();
        var reloaded = await verifyCtx.HotelBookings.AsNoTracking().FirstAsync(h => h.Id == hotelId);
        Assert.Equal(30_000m, reloaded.NetCost);
    }

    // ---------- seed ----------

    /// <summary>
    /// Arma: hotel confirmado con costo 30.000, pagado 50.000 al operador (sobrepago 20.000), y ese
    /// sobrepago YA consumido por completo (<c>RemainingBalance = 0</c>) — simula que se aplicó como
    /// saldo a favor a otra reserva. Subir el costo de vuelta a 50.000 elimina el sobrepago objetivo y el
    /// reconciler no tiene de dónde drenarlo.
    /// </summary>
    private async Task<(int ReservaId, int HotelId, Guid SupplierPublicId)> SeedHotelWithAlreadyAppliedOverpaymentAsync()
    {
        await using var ctx = _fixture.CreateDbContext();

        var customer = new Customer { FullName = "Cliente B1 rollback", IsActive = true };
        var supplier = new Supplier { Name = "Operador B1 rollback", IsActive = true };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = "F-B1RB-" + Guid.NewGuid().ToString("N")[..6],
            Name = "Reserva B1 rollback",
            PayerId = customer.Id,
            Status = EstadoReserva.Confirmed,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var hotel = new HotelBooking
        {
            ReservaId = reserva.Id, SupplierId = supplier.Id, Status = "Confirmado",
            HotelName = "Hotel Test", City = "Bariloche", Country = "Argentina",
            NetCost = 30_000m, SalePrice = 60_000m, Currency = "ARS",
            CheckIn = DateTime.UtcNow.Date.AddDays(10), CheckOut = DateTime.UtcNow.Date.AddDays(12),
        };
        ctx.HotelBookings.Add(hotel);

        ctx.SupplierPayments.Add(new SupplierPayment
        {
            SupplierId = supplier.Id, ReservaId = reserva.Id, Amount = 50_000m, Currency = "ARS",
            ServiceRecordKind = ServicePaymentRecordKinds.Hotel, ServicePublicId = hotel.PublicId,
            PaidAt = DateTime.UtcNow, Method = "Transfer",
        });
        await ctx.SaveChangesAsync();

        await SupplierDebtPersister.PersistAsync(ctx, supplier.Id);
        await ctx.SaveChangesAsync(); // compra 30.000, pagado 50.000 -> sobrepago objetivo 20.000

        // Simula que ese sobrepago YA se aplicó a otra reserva: el entry existe pero sin nada para drenar.
        ctx.SupplierCreditEntries.Add(new SupplierCreditEntry
        {
            SupplierId = supplier.Id, Currency = "ARS",
            CreditedAmount = 20_000m, RemainingBalance = 0m, IsFullyConsumed = true,
        });
        await ctx.SaveChangesAsync();

        return (reserva.Id, hotel.Id, supplier.PublicId);
    }

    private static UpdateHotelRequest BuildHotelUpdate(string supplierPublicId, decimal netCost)
        => new(
            SupplierId: supplierPublicId, HotelName: "Hotel Test", StarRating: 4, City: "Bariloche", Country: "Argentina",
            CheckIn: DateTime.UtcNow.Date.AddDays(10), CheckOut: DateTime.UtcNow.Date.AddDays(12),
            RoomType: "Doble", MealPlan: "Desayuno", Adults: 2, Children: 0, Rooms: 1, ConfirmationNumber: null,
            NetCost: netCost, SalePrice: netCost * 1.5m, Commission: netCost * 0.5m, Status: "Confirmado", Notes: null,
            RoomingAssignments: null, RateId: null, WorkflowStatus: "Confirmado",
            // El costo SUBE (30.000 -> 50.000): nunca queda por debajo de lo pagado, así que el aviso de
            // Tanda P2 no aplica acá — lo que se está probando es el rollback del reconciler.
            ConfirmCostBelowPaid: false);

    /// <summary>
    /// cobranzas.see_cost SIEMPRE otorgado: sin esto, <c>ResolveUpdateCostFieldsAsync</c> trata al caller
    /// como "sin permiso" (fail-closed, ver BookingService) y PRESERVA el NetCost persistido en vez de
    /// tomar el del request — el test necesita que el costo nuevo del request se aplique de verdad.
    /// </summary>
    private static IUserPermissionResolver SeeCostResolver()
    {
        var mock = new Mock<IUserPermissionResolver>();
        IReadOnlySet<string> set = new HashSet<string> { Permissions.CobranzasSeeCost };
        mock.Setup(r => r.GetPermissionsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(set);
        return mock.Object;
    }

    private static IHttpContextAccessor AdminContext()
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "admin-1"),
            new(ClaimTypes.Role, "Admin"),
        };
        var ctx = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) };
        return new HttpContextAccessor { HttpContext = ctx };
    }

    private static BookingService BuildBookingService(AppDbContext context)
    {
        var reservaServiceMock = new Mock<IReservaService>();
        reservaServiceMock.Setup(s => s.UpdateBalanceAsync(It.IsAny<int>())).Returns(Task.CompletedTask);
        reservaServiceMock.Setup(s => s.UpdateBalanceAsync(It.IsAny<int>(), It.IsAny<bool>())).Returns(Task.CompletedTask);
        reservaServiceMock.Setup(s => s.UpdateBalanceAsync(It.IsAny<int>(),
                It.IsAny<TravelApi.Application.Contracts.Reservations.PendingServiceChange?>()))
            .Returns(Task.CompletedTask);

        // SupplierService REAL (no mockeado): este test valida el reconciler + el persister reales contra
        // Postgres, así que necesita el cálculo real de "pagado al operador" y de la deuda por moneda.
        var supplierService = new SupplierService(
            context, auditService: null, httpContextAccessor: null, logger: null, permissionResolver: null);

        return new BookingService(
            new Repository<FlightSegment>(context),
            new Repository<HotelBooking>(context),
            new Repository<PackageBooking>(context),
            new Repository<TransferBooking>(context),
            new Repository<AssistanceBooking>(context),
            new Repository<Reserva>(context),
            new Repository<Supplier>(context),
            reservaServiceMock.Object,
            supplierService,
            context,
            new MapperConfiguration(c => c.AddProfile<MappingProfile>()).CreateMapper(),
            NullLogger<BookingService>.Instance,
            SeeCostResolver(),
            AdminContext(),
            settingsService: null,
            auditService: null,
            cancellationService: null);
    }
}
