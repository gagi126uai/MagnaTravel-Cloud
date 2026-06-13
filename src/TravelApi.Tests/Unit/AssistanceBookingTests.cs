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
/// Bloque 3 (Asistencia al viajero): tests del riesgo central = "olvido silencioso".
/// Si Asistencia no entra en un calculo de saldo/fechas, el saldo del cliente descuadra
/// SIN tirar error. Por eso estos tests fijan que la asistencia:
///  - suma su venta/costo al saldo de la reserva (UpdateBalanceAsync);
///  - aparece en ReservaDto y ReservaListDto con su venta;
///  - enmascara NetCost a usuarios sin cobranzas.see_cost;
///  - se borra en cascada con la reserva;
///  - participa del calculo de fechas (StartDate/EndDate);
///  - cumple el CRUD basico espejando a Hotel.
/// </summary>
public class AssistanceBookingTests
{
    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    private static IMapper CreateMapper()
        => new MapperConfiguration(config => config.AddProfile<MappingProfile>()).CreateMapper();

    private static UserManager<ApplicationUser> BuildUserManager()
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        store.Setup(s => s.FindByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync((ApplicationUser?)null);
        return new UserManager<ApplicationUser>(
            store.Object, null!, null!,
            Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(),
            null!, null!, null!, null!);
    }

    private static IHttpContextAccessor BuildHttpContextAccessor(string userId, params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        foreach (var role in roles) claims.Add(new Claim(ClaimTypes.Role, role));
        return new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
        };
    }

    private static IUserPermissionResolver BuildResolver(string userId, params string[] permissions)
    {
        var mock = new Mock<IUserPermissionResolver>();
        IReadOnlySet<string> set = new HashSet<string>(permissions);
        mock.Setup(r => r.GetPermissionsAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(set);
        return mock.Object;
    }

    // BookingService con un caller dado. Si canSeeCost es true, el resolver devuelve
    // cobranzas.see_cost; sino, vacio (no-Admin para no hacer bypass del masking).
    private static BookingService CreateBookingService(
        AppDbContext context, IMapper mapper, bool canSeeCost,
        IReservaService? reservaServiceOverride = null)
    {
        var reservaService = reservaServiceOverride;
        if (reservaService is null)
        {
            var mock = new Mock<IReservaService>();
            mock.Setup(s => s.UpdateBalanceAsync(It.IsAny<int>())).Returns(Task.CompletedTask);
            // ADR-027: overload nuevo que pasan los paths de edicion (marca "confirmada con cambios").
            mock.Setup(s => s.UpdateBalanceAsync(It.IsAny<int>(), It.IsAny<bool>())).Returns(Task.CompletedTask);
            reservaService = mock.Object;
        }

        var supplierService = new Mock<ISupplierService>();
        supplierService.Setup(s => s.UpdateBalanceAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                       .Returns(Task.CompletedTask);

        const string userId = "vendedor-test";
        var accessor = BuildHttpContextAccessor(userId);
        var resolver = canSeeCost
            ? BuildResolver(userId, Permissions.CobranzasSeeCost)
            : BuildResolver(userId);

        return new BookingService(
            new Repository<FlightSegment>(context),
            new Repository<HotelBooking>(context),
            new Repository<PackageBooking>(context),
            new Repository<TransferBooking>(context),
            new Repository<AssistanceBooking>(context),
            new Repository<Reserva>(context),
            new Repository<Supplier>(context),
            reservaService,
            supplierService.Object,
            context,
            mapper,
            NullLogger<BookingService>.Instance,
            resolver,
            accessor);
    }

    private static ReservaService CreateReservaService(AppDbContext context, IMapper mapper)
    {
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OperationalFinanceSettings());
        // Sin accessor/resolver: ApplyCostMaskingAsync queda fail-closed, pero estos tests
        // que tocan saldo no leen el masking de ReservaDto (lo cubre el test dedicado).
        return new ReservaService(context, mapper, settings.Object, BuildUserManager(),
            NullLogger<ReservaService>.Instance);
    }

    private static CreateAssistanceRequest BuildCreateRequest(Supplier supplier, string? rateId = null) =>
        new(
            SupplierId: supplier.PublicId.ToString(),
            ValidFrom: DateTime.UtcNow.Date.AddDays(10),
            ValidTo: DateTime.UtcNow.Date.AddDays(20),
            Adults: 2,
            Children: 0,
            NetCost: 100m,
            SalePrice: 250m,
            Commission: 150m,
            PolicyNumber: "POL-123",
            PlanType: "Premium 60K",
            CoverageLimit: "USD 60.000",
            CoverageZone: "Mundial",
            ConfirmationNumber: "CONF-AC-1",
            Notes: "Cobertura COVID incluida",
            RateId: rateId,
            WorkflowStatus: "Solicitado");

    // === 6. CRUD basico (espejo de Hotel) ===

    [Fact]
    public async Task CreateAssistanceAsync_PersistsBusinessFieldsAndPrices()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var supplier = new Supplier { Id = 1, Name = "Assist Card" };
        var reserva = new Reserva { Id = 1, NumeroReserva = "F-2026-9001", Name = "Reserva test" };
        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();

        var service = CreateBookingService(context, mapper, canSeeCost: true);
        var created = await service.CreateAssistanceAsync(reserva.Id, BuildCreateRequest(supplier), CancellationToken.None);

        Assert.Equal("POL-123", created.PolicyNumber);
        Assert.Equal("Premium 60K", created.PlanType);
        Assert.Equal("USD 60.000", created.CoverageLimit);
        Assert.Equal("Mundial", created.CoverageZone);
        Assert.Equal(250m, created.SalePrice);
        Assert.Equal(100m, created.NetCost); // canSeeCost: ve el costo real
        Assert.Equal("Assistance", created.SourceKind);

        var stored = await context.AssistanceBookings.SingleAsync();
        Assert.Equal("POL-123", stored.PolicyNumber);
        Assert.Equal(150m, stored.Commission); // comision persiste en la entidad...
        Assert.Equal(2, stored.Adults);
    }

    [Fact]
    public async Task CreateAssistanceAsync_RejectsInvalidValidity()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var supplier = new Supplier { Id = 1, Name = "Assist Card" };
        var reserva = new Reserva { Id = 1, NumeroReserva = "F-2026-9002", Name = "Reserva test" };
        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();

        var service = CreateBookingService(context, mapper, canSeeCost: true);
        var bad = BuildCreateRequest(supplier) with
        {
            ValidFrom = DateTime.UtcNow.Date.AddDays(20),
            ValidTo = DateTime.UtcNow.Date.AddDays(10) // hasta < desde
        };

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.CreateAssistanceAsync(reserva.Id, bad, CancellationToken.None));
    }

    [Fact]
    public async Task GetAssistanceByIdAsync_ReturnsPersistedAssistance()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var supplier = new Supplier { Id = 1, Name = "Assist Card" };
        var reserva = new Reserva { Id = 1, NumeroReserva = "F-2026-9003", Name = "Reserva test" };
        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();

        var service = CreateBookingService(context, mapper, canSeeCost: true);
        var created = await service.CreateAssistanceAsync(reserva.Id, BuildCreateRequest(supplier), CancellationToken.None);

        var fetched = await service.GetAssistanceByIdAsync(reserva.Id.ToString(), created.PublicId.ToString(), CancellationToken.None);
        Assert.Equal(created.PublicId, fetched.PublicId);
        Assert.Equal("Premium 60K", fetched.PlanType);
    }

    [Fact]
    public async Task UpdateAssistanceAsync_ChangesFieldsAndPrice()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var supplier = new Supplier { Id = 1, Name = "Assist Card" };
        var reserva = new Reserva { Id = 1, NumeroReserva = "F-2026-9004", Name = "Reserva test" };
        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();

        var service = CreateBookingService(context, mapper, canSeeCost: true);
        var created = await service.CreateAssistanceAsync(reserva.Id, BuildCreateRequest(supplier), CancellationToken.None);

        var update = new UpdateAssistanceRequest(
            SupplierId: supplier.PublicId.ToString(),
            ValidFrom: DateTime.UtcNow.Date.AddDays(11),
            ValidTo: DateTime.UtcNow.Date.AddDays(25),
            Adults: 3,
            Children: 1,
            NetCost: 120m,
            SalePrice: 400m,
            Commission: 280m,
            Status: "Solicitado",
            PlanType: "Premium 150K",
            WorkflowStatus: "Solicitado");

        var updated = await service.UpdateAssistanceAsync(reserva.Id.ToString(), created.PublicId.ToString(), update, CancellationToken.None);

        Assert.Equal(400m, updated.SalePrice);
        Assert.Equal("Premium 150K", updated.PlanType);
        Assert.Equal(3, updated.Adults);
        var stored = await context.AssistanceBookings.SingleAsync();
        Assert.Equal(400m, stored.SalePrice);
        Assert.Equal(280m, stored.Commission);
    }

    [Fact]
    public async Task DeleteAssistanceAsync_RemovesRow()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var supplier = new Supplier { Id = 1, Name = "Assist Card" };
        // Budget para que el delete guard (solo Budget) permita borrar el servicio.
        var reserva = new Reserva { Id = 1, NumeroReserva = "F-2026-9005", Name = "Reserva test", Status = EstadoReserva.Budget };
        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();

        var service = CreateBookingService(context, mapper, canSeeCost: true);
        var created = await service.CreateAssistanceAsync(reserva.Id, BuildCreateRequest(supplier), CancellationToken.None);

        await service.DeleteAssistanceAsync(reserva.Id.ToString(), created.PublicId.ToString(), CancellationToken.None);

        Assert.Equal(0, await context.AssistanceBookings.CountAsync());
    }

    // === 3. Masking de NetCost ===

    [Fact]
    public async Task CreateAssistanceAsync_WithoutSeeCost_MasksNetCost()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var supplier = new Supplier { Id = 1, Name = "Assist Card" };
        var reserva = new Reserva { Id = 1, NumeroReserva = "F-2026-9006", Name = "Reserva test" };
        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();

        var service = CreateBookingService(context, mapper, canSeeCost: false);
        var created = await service.CreateAssistanceAsync(reserva.Id, BuildCreateRequest(supplier), CancellationToken.None);

        // El costo del proveedor NO viaja a un usuario sin cobranzas.see_cost.
        Assert.Equal(0m, created.NetCost);
        // Pero la venta y el costo real siguen intactos en la entidad persistida.
        var stored = await context.AssistanceBookings.SingleAsync();
        Assert.Equal(100m, stored.NetCost);
        Assert.Equal(250m, created.SalePrice);
    }

    [Fact]
    public async Task GetAndUpdateAssistance_WithoutSeeCost_MaskNetCost()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var supplier = new Supplier { Id = 1, Name = "Assist Card" };
        var reserva = new Reserva { Id = 1, NumeroReserva = "F-2026-9007", Name = "Reserva test" };
        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();

        // Sembramos con un servicio que SI ve costos para tener el costo real en BD.
        var seeder = CreateBookingService(context, mapper, canSeeCost: true);
        var created = await seeder.CreateAssistanceAsync(reserva.Id, BuildCreateRequest(supplier), CancellationToken.None);

        var noCost = CreateBookingService(context, mapper, canSeeCost: false);
        var fetched = await noCost.GetAssistanceByIdAsync(reserva.Id.ToString(), created.PublicId.ToString(), CancellationToken.None);
        Assert.Equal(0m, fetched.NetCost);

        var update = new UpdateAssistanceRequest(
            SupplierId: supplier.PublicId.ToString(),
            ValidFrom: DateTime.UtcNow.Date.AddDays(10),
            ValidTo: DateTime.UtcNow.Date.AddDays(20),
            Adults: 2, Children: 0,
            NetCost: 100m, SalePrice: 250m, Commission: 150m,
            Status: "Solicitado",
            WorkflowStatus: "Solicitado");
        var updated = await noCost.UpdateAssistanceAsync(reserva.Id.ToString(), created.PublicId.ToString(), update, CancellationToken.None);
        Assert.Equal(0m, updated.NetCost);
    }

    // === 1 + 2. Saldo y DTOs (con ReservaService real) ===

    [Fact]
    public async Task UpdateBalanceAsync_IncludesAssistanceSaleAndCost()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var supplier = new Supplier { Id = 1, Name = "Assist Card" };
        var reserva = new Reserva { Id = 1, NumeroReserva = "F-2026-9008", Name = "Reserva test", Status = EstadoReserva.Confirmed };
        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        // Confirmado para que cuente al balance (CountsForReservaBalance).
        context.AssistanceBookings.Add(new AssistanceBooking
        {
            Id = 1, ReservaId = 1, SupplierId = 1,
            ValidFrom = DateTime.UtcNow.Date.AddDays(10),
            ValidTo = DateTime.UtcNow.Date.AddDays(20),
            Status = "Confirmado", NetCost = 100m, SalePrice = 250m, Commission = 150m
        });
        await context.SaveChangesAsync();

        var reservaService = CreateReservaService(context, mapper);
        await reservaService.UpdateBalanceAsync(1);

        var reloaded = await context.Reservas.FindAsync(1);
        Assert.NotNull(reloaded);
        // La venta de la asistencia suma al total de venta y al saldo (no hay pagos).
        Assert.Equal(250m, reloaded!.TotalSale);
        Assert.Equal(100m, reloaded.TotalCost);
        Assert.Equal(250m, reloaded.Balance);
    }

    [Fact]
    public async Task ReservaDto_And_ListDto_ReflectAssistanceSale()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var supplier = new Supplier { Id = 1, Name = "Assist Card" };
        var reserva = new Reserva { Id = 1, NumeroReserva = "F-2026-9009", Name = "Reserva test", Status = EstadoReserva.Confirmed };
        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        context.AssistanceBookings.Add(new AssistanceBooking
        {
            Id = 1, ReservaId = 1, SupplierId = 1,
            ValidFrom = DateTime.UtcNow.Date.AddDays(10),
            ValidTo = DateTime.UtcNow.Date.AddDays(20),
            Status = "Confirmado", PlanType = "Premium", NetCost = 100m, SalePrice = 250m, Commission = 150m
        });
        await context.SaveChangesAsync();

        // ReservaDto: la coleccion AssistanceBookings viaja al detalle.
        var reservaEntity = await context.Reservas
            .Include(r => r.AssistanceBookings).ThenInclude(a => a.Supplier)
            .FirstAsync(r => r.Id == 1);
        var dto = mapper.Map<ReservaDto>(reservaEntity);
        Assert.Single(dto.AssistanceBookings);
        Assert.Equal(250m, dto.AssistanceBookings[0].SalePrice);
        Assert.Equal("Premium", dto.AssistanceBookings[0].PlanType);

        // ReservaListDto: el TotalSale del listado incluye la venta de la asistencia.
        var listDto = mapper.Map<ReservaListDto>(reservaEntity);
        Assert.Equal(250m, listDto.TotalSale);
        Assert.Equal(250m, listDto.Balance);
    }

    // === 4. Cascade delete de la reserva ===

    [Fact]
    public async Task DeleteReservaAsync_WithAssistance_RemovesAssistance()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var supplier = new Supplier { Id = 1, Name = "Assist Card" };
        // Budget sin pagos/facturas: el delete guard de reserva lo permite.
        var reserva = new Reserva { Id = 1, NumeroReserva = "F-2026-9010", Name = "Reserva test", Status = EstadoReserva.Budget };
        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        context.AssistanceBookings.Add(new AssistanceBooking
        {
            Id = 1, ReservaId = 1, SupplierId = 1,
            ValidFrom = DateTime.UtcNow.Date.AddDays(10),
            ValidTo = DateTime.UtcNow.Date.AddDays(20),
            Status = "Solicitado", NetCost = 100m, SalePrice = 250m
        });
        await context.SaveChangesAsync();

        var reservaService = CreateReservaService(context, mapper);
        await reservaService.DeleteReservaAsync(1);

        Assert.Equal(0, await context.Reservas.CountAsync());
        Assert.Equal(0, await context.AssistanceBookings.CountAsync());
    }

    // === 5. Schedule: ValidFrom/ValidTo entran al min/max de fechas ===

    [Fact]
    public async Task ScheduleCalculator_ConsidersAssistanceValidity()
    {
        await using var context = CreateContext();
        var supplier = new Supplier { Id = 1, Name = "Assist Card" };
        var reserva = new Reserva { Id = 1, NumeroReserva = "F-2026-9011", Name = "Reserva test" };
        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);

        var from = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc);
        context.AssistanceBookings.Add(new AssistanceBooking
        {
            Id = 1, ReservaId = 1, SupplierId = 1,
            ValidFrom = from, ValidTo = to,
            Status = "Solicitado", NetCost = 100m, SalePrice = 250m
        });
        await context.SaveChangesAsync();

        var (start, end) = await ReservaScheduleCalculator.ComputeAsync(context, 1);

        Assert.Equal(from, start);
        Assert.Equal(to, end);
    }
}
