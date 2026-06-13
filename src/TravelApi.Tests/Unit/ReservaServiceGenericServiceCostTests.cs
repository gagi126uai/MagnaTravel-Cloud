using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Contracts.Files;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Application.Mappings;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// B1 + B2 (ADR-017 §2.7, F1b — fixes de seguridad SIN flag) sobre el ServicioReserva
/// generico de ReservaService:
///   - B1 (create): el alta desde tarifario nacia con costo 0 para callers sin
///     cobranzas.see_cost (el search del tarifario les enmascara NetCost a 0 y el form
///     rebota ese 0). Fix: el server resuelve el costo desde la tarifa.
///   - B2 (update): la asignacion incondicional NetCost = request.NetCost destruia el
///     costo persistido cuando el form de un caller sin permiso re-enviaba el 0 del GET
///     enmascarado. Fix: preservar NetCost y recalcular la ganancia con la venta del request.
/// En ambos casos, el caller CON permiso conserva el comportamiento de siempre (el
/// request manda) y lo persistido en DB es siempre el costo real.
/// </summary>
public class ReservaServiceGenericServiceCostTests
{
    private const string SeeCostPermission = Permissions.CobranzasSeeCost;

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    // Construye el servicio con un caller no-Admin. Si "canSeeCost" es true, el
    // resolver devuelve el permiso cobranzas.see_cost; si es false, devuelve vacio.
    private static ReservaService CreateServiceForUser(AppDbContext context, bool canSeeCost)
    {
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OperationalFinanceSettings());

        const string userId = "vendedor-test";
        var accessor = BuildHttpContextAccessor(userId);
        var resolver = canSeeCost
            ? BuildResolver(userId, SeeCostPermission)
            : BuildResolver(userId); // sin permisos

        return new ReservaService(
            context,
            Mock.Of<IMapper>(),
            settings.Object,
            BuildUserManager(),
            NullLogger<ReservaService>.Instance,
            resolver,
            accessor);
    }

    private static IHttpContextAccessor BuildHttpContextAccessor(string userId, params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        foreach (var role in roles) claims.Add(new Claim(ClaimTypes.Role, role));
        return new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
            }
        };
    }

    private static IUserPermissionResolver BuildResolver(string userId, params string[] permissions)
    {
        var mock = new Mock<IUserPermissionResolver>();
        IReadOnlySet<string> set = new HashSet<string>(permissions);
        mock.Setup(r => r.GetPermissionsAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(set);
        return mock.Object;
    }

    private static UserManager<ApplicationUser> BuildUserManager()
    {
        // UserManager mock minimo: estos tests no pasan por CreateReservaAsync, asi que
        // alcanza con un store que devuelve null (mismo patron que ReservaServiceTests).
        var store = new Mock<IUserStore<ApplicationUser>>();
        store
            .Setup(s => s.FindByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ApplicationUser?)null);
        return new UserManager<ApplicationUser>(
            store.Object, null!, null!,
            Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(),
            null!, null!, null!, null!);
    }

    // Seed: reserva + tarifa generica (Excursion) con costo conocido. NetCost 100, Venta 160.
    private static async Task<(Reserva reserva, Rate rate)> SeedReservaAndRateAsync(AppDbContext context)
    {
        var reserva = new Reserva { Id = 1, NumeroReserva = "F-2026-9200", Name = "Reserva test" };
        var rate = new Rate
        {
            Id = 1,
            ServiceType = "Excursion",
            ProductName = "Excursion glaciar",
            NetCost = 100m,
            Tax = 0m,
            SalePrice = 160m,
            Commission = 60m,
            IsActive = true
        };
        context.Reservas.Add(reserva);
        context.Rates.Add(rate);
        await context.SaveChangesAsync();
        return (reserva, rate);
    }

    private static AddServiceRequest BuildAddRequest(decimal netCost, decimal salePrice, string? rateId = null) => new(
        ServiceType: "Excursion",
        SupplierId: null,
        Description: "Excursion glaciar",
        ConfirmationNumber: null,
        DepartureDate: DateTime.UtcNow.AddDays(10),
        ReturnDate: null,
        SalePrice: salePrice,
        NetCost: netCost,
        RateId: rateId);

    // ============================= CREATE desde tarifario (B1) =============================

    [Fact]
    public async Task AddServiceAsync_FromRate_UserWithoutSeeCost_ResolvesNetCostFromRate()
    {
        await using var context = CreateContext();
        var (reserva, rate) = await SeedReservaAndRateAsync(context);
        var noCost = CreateServiceForUser(context, canSeeCost: false);

        // El form del caller sin ver-costos rebota el 0 enmascarado del search del tarifario.
        var (created, _) = await noCost.AddServiceAsync(
            reserva.Id, BuildAddRequest(netCost: 0m, salePrice: 200m, rateId: rate.PublicId.ToString()), CancellationToken.None);

        var stored = await context.Servicios.SingleAsync();
        Assert.Equal(100m, stored.NetCost);          // el costo real lo resolvio el server desde la tarifa
        Assert.Equal(200m - 100m, stored.Commission); // ganancia recalculada con la venta del request
        Assert.Equal(200m, stored.SalePrice);
        Assert.Equal(created.Id, stored.Id);
    }

    [Fact]
    public async Task AddServiceAsync_FromRate_UserWithSeeCost_RequestWins()
    {
        await using var context = CreateContext();
        var (reserva, rate) = await SeedReservaAndRateAsync(context);
        var seeCost = CreateServiceForUser(context, canSeeCost: true);

        await seeCost.AddServiceAsync(
            reserva.Id, BuildAddRequest(netCost: 90m, salePrice: 200m, rateId: rate.PublicId.ToString()), CancellationToken.None);

        var stored = await context.Servicios.SingleAsync();
        // Con permiso: el request manda, como siempre (puede pisar el costo del tarifario).
        Assert.Equal(90m, stored.NetCost);
        Assert.Equal(110m, stored.Commission);
    }

    [Fact]
    public async Task AddServiceAsync_WithoutRate_UserWithoutSeeCost_KeepsRequestValues()
    {
        await using var context = CreateContext();
        var (reserva, _) = await SeedReservaAndRateAsync(context);
        var noCost = CreateServiceForUser(context, canSeeCost: false);

        // Sin tarifa no hay dato real que resolver: queda lo que vino (no se inventa).
        await noCost.AddServiceAsync(
            reserva.Id, BuildAddRequest(netCost: 0m, salePrice: 200m, rateId: null), CancellationToken.None);

        var stored = await context.Servicios.SingleAsync();
        Assert.Equal(0m, stored.NetCost);
        Assert.Equal(200m, stored.Commission);
    }

    // ============================= UPDATE (B2 — Fuga 3 generica) =============================

    private static async Task<ServicioReserva> SeedServiceAsync(AppDbContext context, int reservaId)
    {
        var service = new ServicioReserva
        {
            Id = 10,
            ReservaId = reservaId,
            ServiceType = "Excursion",
            ProductType = "Excursion",
            Description = "Excursion glaciar",
            ConfirmationNumber = "ABC123",
            Status = "Solicitado",
            DepartureDate = DateTime.UtcNow.AddDays(10),
            SalePrice = 150m,
            NetCost = 100m,
            Commission = 50m,
            CreatedAt = DateTime.UtcNow
        };
        context.Servicios.Add(service);
        await context.SaveChangesAsync();
        return service;
    }

    [Fact]
    public async Task UpdateServiceAsync_UserWithoutSeeCost_PreservesNetCostAndRecalculatesCommission()
    {
        await using var context = CreateContext();
        var (reserva, _) = await SeedReservaAndRateAsync(context);
        var service = await SeedServiceAsync(context, reserva.Id);
        var noCost = CreateServiceForUser(context, canSeeCost: false);

        // El update que mandaria el form del caller sin ver-costos: NetCost rebota en 0
        // (el GET se lo enmascaro) y cambia la venta.
        await noCost.UpdateServiceAsync(
            service.Id, BuildAddRequest(netCost: 0m, salePrice: 180m), CancellationToken.None);

        var stored = await context.Servicios.AsNoTracking().SingleAsync();
        Assert.Equal(100m, stored.NetCost);           // costo preservado (el 0 era el masking rebotado)
        Assert.Equal(180m - 100m, stored.Commission); // ganancia recalculada con el costo PRESERVADO
        Assert.Equal(180m, stored.SalePrice);         // la venta SI se aplica (el caller la ve)
    }

    [Fact]
    public async Task UpdateServiceAsync_UserWithSeeCost_AppliesRequestCosts()
    {
        await using var context = CreateContext();
        var (reserva, _) = await SeedReservaAndRateAsync(context);
        var service = await SeedServiceAsync(context, reserva.Id);
        var seeCost = CreateServiceForUser(context, canSeeCost: true);

        await seeCost.UpdateServiceAsync(
            service.Id, BuildAddRequest(netCost: 120m, salePrice: 180m), CancellationToken.None);

        var stored = await context.Servicios.AsNoTracking().SingleAsync();
        // Con permiso: el request manda, igual que siempre.
        Assert.Equal(120m, stored.NetCost);
        Assert.Equal(60m, stored.Commission);
        Assert.Equal(180m, stored.SalePrice);
    }

    // ============================= masking del DTO de RESPUESTA (B1 ADR-017 F1b) ==============
    // Los tests de arriba usan los overloads internos (int) + Mock<IMapper>, por eso solo
    // verifican la ENTIDAD en DB. Estos usan los wrappers publicos (string) con MAPPER REAL
    // para verificar el DTO que viaja en el body HTTP del POST/PUT: a un caller sin
    // cobranzas.see_cost no le puede llegar NetCost/Commission/Tax aunque el costo real
    // siga persistido (cierra la asimetria con el GET de detalle, que ya enmascaraba).

    private static IMapper CreateRealMapper()
        => new MapperConfiguration(config => config.AddProfile<MappingProfile>()).CreateMapper();

    // Variante con mapper REAL para inspeccionar el DTO devuelto. isAdmin agrega el rol
    // "Admin" (bypass del masking por rol). canSeeCost agrega el permiso cobranzas.see_cost.
    private static ReservaService CreateServiceWithRealMapper(AppDbContext context, bool canSeeCost, bool isAdmin = false)
    {
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OperationalFinanceSettings());

        const string userId = "vendedor-test";
        var accessor = isAdmin
            ? BuildHttpContextAccessor(userId, "Admin")
            : BuildHttpContextAccessor(userId);
        var resolver = canSeeCost
            ? BuildResolver(userId, SeeCostPermission)
            : BuildResolver(userId);

        return new ReservaService(
            context,
            CreateRealMapper(),
            settings.Object,
            BuildUserManager(),
            NullLogger<ReservaService>.Instance,
            resolver,
            accessor);
    }

    [Fact]
    public async Task AddServiceAsync_ResponseDto_UserWithoutSeeCost_MasksCostFields()
    {
        await using var context = CreateContext();
        var (reserva, rate) = await SeedReservaAndRateAsync(context);
        var noCost = CreateServiceWithRealMapper(context, canSeeCost: false);

        var result = await noCost.AddServiceAsync(
            reserva.PublicId.ToString(),
            BuildAddRequest(netCost: 0m, salePrice: 200m, rateId: rate.PublicId.ToString()),
            CancellationToken.None);

        // El DTO del body NO debe revelar costo/ganancia/impuesto al caller sin permiso.
        Assert.Equal(0m, result.Servicio.NetCost);
        Assert.Equal(0m, result.Servicio.Commission);
        Assert.Equal(0m, result.Servicio.Tax);
        Assert.Equal(200m, result.Servicio.SalePrice); // la venta nunca se enmascara

        // Pero el costo REAL resuelto server-side SI quedo persistido en DB.
        var stored = await context.Servicios.AsNoTracking().SingleAsync();
        Assert.Equal(100m, stored.NetCost);
    }

    [Fact]
    public async Task AddServiceAsync_ResponseDto_UserWithSeeCost_ReturnsRealCostFields()
    {
        await using var context = CreateContext();
        var (reserva, rate) = await SeedReservaAndRateAsync(context);
        var seeCost = CreateServiceWithRealMapper(context, canSeeCost: true);

        var result = await seeCost.AddServiceAsync(
            reserva.PublicId.ToString(),
            BuildAddRequest(netCost: 90m, salePrice: 200m, rateId: rate.PublicId.ToString()),
            CancellationToken.None);

        // Con permiso: el DTO trae los valores reales (el request manda).
        Assert.Equal(90m, result.Servicio.NetCost);
        Assert.Equal(110m, result.Servicio.Commission);
        Assert.Equal(200m, result.Servicio.SalePrice);
    }

    [Fact]
    public async Task AddServiceAsync_ResponseDto_Admin_ReturnsRealCostFields()
    {
        await using var context = CreateContext();
        var (reserva, rate) = await SeedReservaAndRateAsync(context);
        // Admin sin el permiso explicito: el bypass por rol debe dejarlo ver los costos
        // (garantiza que el fail-closed no enmascara de mas).
        var admin = CreateServiceWithRealMapper(context, canSeeCost: false, isAdmin: true);

        var result = await admin.AddServiceAsync(
            reserva.PublicId.ToString(),
            BuildAddRequest(netCost: 90m, salePrice: 200m, rateId: rate.PublicId.ToString()),
            CancellationToken.None);

        Assert.Equal(90m, result.Servicio.NetCost);
        Assert.Equal(110m, result.Servicio.Commission);
    }

    [Fact]
    public async Task UpdateServiceAsync_ResponseDto_UserWithoutSeeCost_MasksCostFields()
    {
        await using var context = CreateContext();
        var (reserva, _) = await SeedReservaAndRateAsync(context);
        var service = await SeedServiceAsync(context, reserva.Id);
        var noCost = CreateServiceWithRealMapper(context, canSeeCost: false);

        var dto = await noCost.UpdateServiceAsync(
            service.PublicId.ToString(),
            BuildAddRequest(netCost: 0m, salePrice: 180m),
            CancellationToken.None);

        // El DTO del body PUT no debe revelar costo/ganancia/impuesto al caller sin permiso.
        Assert.Equal(0m, dto.NetCost);
        Assert.Equal(0m, dto.Commission);
        Assert.Equal(0m, dto.Tax);
        Assert.Equal(180m, dto.SalePrice);

        // El costo real PRESERVADO sigue en DB (el 0 del request era el masking rebotado).
        var stored = await context.Servicios.AsNoTracking().SingleAsync();
        Assert.Equal(100m, stored.NetCost);
    }

    [Fact]
    public async Task UpdateServiceAsync_ResponseDto_UserWithSeeCost_ReturnsRealCostFields()
    {
        await using var context = CreateContext();
        var (reserva, _) = await SeedReservaAndRateAsync(context);
        var service = await SeedServiceAsync(context, reserva.Id);
        var seeCost = CreateServiceWithRealMapper(context, canSeeCost: true);

        var dto = await seeCost.UpdateServiceAsync(
            service.PublicId.ToString(),
            BuildAddRequest(netCost: 120m, salePrice: 180m),
            CancellationToken.None);

        Assert.Equal(120m, dto.NetCost);
        Assert.Equal(60m, dto.Commission);
        Assert.Equal(180m, dto.SalePrice);
    }

    [Fact]
    public async Task UpdateServiceAsync_ResponseDto_Admin_ReturnsRealCostFields()
    {
        await using var context = CreateContext();
        var (reserva, _) = await SeedReservaAndRateAsync(context);
        var service = await SeedServiceAsync(context, reserva.Id);
        var admin = CreateServiceWithRealMapper(context, canSeeCost: false, isAdmin: true);

        var dto = await admin.UpdateServiceAsync(
            service.PublicId.ToString(),
            BuildAddRequest(netCost: 120m, salePrice: 180m),
            CancellationToken.None);

        Assert.Equal(120m, dto.NetCost);
        Assert.Equal(60m, dto.Commission);
    }

    // ===== ADR-026 (vencimientos): fecha limite de pago al operador del servicio generico =====
    // El generico tiene la columna + la alarma pero le faltaba el campo en AddServiceRequest
    // (auditoria 2026-06-12): la fecha nunca llegaba por la ruta real de escritura, asi que la
    // alarma de pago al operador nunca disparaba para este tipo. Estos tests cubren esa ruta.

    [Fact]
    public async Task AddServiceAsync_PersistsOperatorPaymentDeadline_NormalizedToUtcMidnight()
    {
        await using var context = CreateContext();
        var (reserva, _) = await SeedReservaAndRateAsync(context);
        var seeCost = CreateServiceForUser(context, canSeeCost: true);

        var request = new AddServiceRequest(
            ServiceType: "Excursion",
            SupplierId: null,
            Description: "Excursion glaciar",
            ConfirmationNumber: null,
            DepartureDate: DateTime.UtcNow.AddDays(10),
            ReturnDate: null,
            SalePrice: 160m,
            NetCost: 100m,
            RateId: null,
            OperatorPaymentDeadline: new DateTime(2026, 7, 15, 9, 30, 0, DateTimeKind.Unspecified));

        await seeCost.AddServiceAsync(reserva.Id, request, CancellationToken.None);

        var stored = await context.Servicios.AsNoTracking().SingleAsync();
        Assert.NotNull(stored.OperatorPaymentDeadline);
        Assert.Equal(new DateTime(2026, 7, 15), stored.OperatorPaymentDeadline!.Value.Date);
        Assert.Equal(DateTimeKind.Utc, stored.OperatorPaymentDeadline!.Value.Kind); // fecha de pared
        Assert.Equal(TimeSpan.Zero, stored.OperatorPaymentDeadline!.Value.TimeOfDay); // medianoche
    }

    [Fact]
    public async Task UpdateServiceAsync_WithoutDeadline_PreservesStoredDeadline()
    {
        await using var context = CreateContext();
        var (reserva, _) = await SeedReservaAndRateAsync(context);
        var service = await SeedServiceAsync(context, reserva.Id);
        service.OperatorPaymentDeadline = DateTime.SpecifyKind(new DateTime(2026, 7, 15), DateTimeKind.Utc);
        await context.SaveChangesAsync();
        var seeCost = CreateServiceForUser(context, canSeeCost: true);

        // El form que NO manda la fecha (default null) no debe borrar la fecha cargada (anti-pisado).
        await seeCost.UpdateServiceAsync(
            service.Id, BuildAddRequest(netCost: 120m, salePrice: 180m), CancellationToken.None);

        var stored = await context.Servicios.AsNoTracking().SingleAsync();
        Assert.NotNull(stored.OperatorPaymentDeadline);
        Assert.Equal(new DateTime(2026, 7, 15), stored.OperatorPaymentDeadline!.Value.Date);
    }

    [Fact]
    public async Task UpdateServiceAsync_WithDeadline_UpdatesIt()
    {
        await using var context = CreateContext();
        var (reserva, _) = await SeedReservaAndRateAsync(context);
        var service = await SeedServiceAsync(context, reserva.Id);
        var seeCost = CreateServiceForUser(context, canSeeCost: true);

        var request = new AddServiceRequest(
            ServiceType: "Excursion",
            SupplierId: null,
            Description: "Excursion glaciar",
            ConfirmationNumber: null,
            DepartureDate: DateTime.UtcNow.AddDays(10),
            ReturnDate: null,
            SalePrice: 180m,
            NetCost: 120m,
            RateId: null,
            OperatorPaymentDeadline: new DateTime(2026, 8, 1, 14, 0, 0, DateTimeKind.Unspecified));

        await seeCost.UpdateServiceAsync(service.Id, request, CancellationToken.None);

        var stored = await context.Servicios.AsNoTracking().SingleAsync();
        Assert.Equal(new DateTime(2026, 8, 1), stored.OperatorPaymentDeadline!.Value.Date);
        Assert.Equal(DateTimeKind.Utc, stored.OperatorPaymentDeadline!.Value.Kind);
    }
}
