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
using TravelApi.Application.Interfaces;
using TravelApi.Application.Mappings;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// B1.15 Fase 2a (FIX 7 — fiscal critico): UpdateStatusAsync forward bloquea
/// cancelar una reserva con factura AFIP CAE vivo (no anulada via NC aprobada).
///
/// Reglas:
///  - Invoice con CAE no nulo y AnnulmentStatus != Succeeded -> throw.
///  - Invoice con CAE no nulo y AnnulmentStatus = Succeeded -> permite cancelar.
///  - Invoice con CAE no nulo y AnnulmentStatus = Pending -> bloquea.
///  - Reserva sin invoices -> permite cancelar.
///
/// El guard es simetrico al de RevertStatusAsync (linea 722) y aplica a TODOS
/// los actores, incluido Admin (es bloqueo fiscal, no de permisos).
/// </summary>
public class ReservaServiceCancellationCaeGuardTests
{
    private readonly DbContextOptions<AppDbContext> _dbOptions;
    private readonly IMapper _mapper;
    private readonly Mock<IOperationalFinanceSettingsService> _settingsServiceMock;

    public ReservaServiceCancellationCaeGuardTests()
    {
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _mapper = new MapperConfiguration(c => c.AddProfile<MappingProfile>()).CreateMapper();
        _settingsServiceMock = new Mock<IOperationalFinanceSettingsService>();
        _settingsServiceMock
            .Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings());
    }

    private static UserManager<ApplicationUser> BuildUserManager()
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        return new UserManager<ApplicationUser>(
            store.Object, null!, null!,
            Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(),
            null!, null!, null!, null!);
    }

    private static IHttpContextAccessor BuildContextAccessor(string userId, params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        foreach (var r in roles) claims.Add(new Claim(ClaimTypes.Role, r));
        var ctx = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) };
        return new HttpContextAccessor { HttpContext = ctx };
    }

    private static IUserPermissionResolver BuildResolver(string userId, params string[] permissions)
    {
        var mock = new Mock<IUserPermissionResolver>();
        IReadOnlySet<string> set = new HashSet<string>(permissions);
        mock.Setup(r => r.GetPermissionsAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(set);
        return mock.Object;
    }

    private ReservaService BuildService(AppDbContext context)
    {
        var accessor = BuildContextAccessor("admin-1", "Admin");
        var resolver = BuildResolver("admin-1");
        return new(context, _mapper, _settingsServiceMock.Object, BuildUserManager(),
                   NullLogger<ReservaService>.Instance, resolver, accessor);
    }

    private static async Task SeedReservaWithInvoiceAsync(
        AppDbContext ctx,
        string? cae,
        AnnulmentStatus annulmentStatus = AnnulmentStatus.None)
    {
        ctx.Reservas.Add(new Reserva
        {
            Id = 1,
            NumeroReserva = "F-CAE-0001",
            Name = "Reserva con factura",
            Status = EstadoReserva.Confirmed,
            ResponsibleUserId = "vendedor-1"
        });
        ctx.Invoices.Add(new Invoice
        {
            Id = 1,
            ReservaId = 1,
            ImporteTotal = 100m,
            CAE = cae,
            AnnulmentStatus = annulmentStatus,
            PublicId = Guid.NewGuid()
        });
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task Cancel_Reserva_WithLiveCaeInvoice_Throws_InvalidOperationException()
    {
        await using var ctx = new AppDbContext(_dbOptions);
        await SeedReservaWithInvoiceAsync(ctx, cae: "12345678901234", annulmentStatus: AnnulmentStatus.None);

        var service = BuildService(ctx);
        var publicId = (await ctx.Reservas.AsNoTracking().FirstAsync()).PublicId;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdateStatusAsync(publicId.ToString(), EstadoReserva.Cancelled, "admin-1", CancellationToken.None));

        Assert.Contains("CAE", ex.Message);
        // El estado se preserva.
        var reserva = await ctx.Reservas.AsNoTracking().FirstAsync();
        Assert.Equal(EstadoReserva.Confirmed, reserva.Status);
    }

    [Fact]
    public async Task Cancel_Reserva_WithCaeInvoiceAndPendingAnnulment_Throws()
    {
        await using var ctx = new AppDbContext(_dbOptions);
        await SeedReservaWithInvoiceAsync(ctx, cae: "12345678901234", annulmentStatus: AnnulmentStatus.Pending);

        var service = BuildService(ctx);
        var publicId = (await ctx.Reservas.AsNoTracking().FirstAsync()).PublicId;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdateStatusAsync(publicId.ToString(), EstadoReserva.Cancelled, "admin-1", CancellationToken.None));
    }

    [Fact]
    public async Task Cancel_Reserva_WithCaeInvoiceAndFailedAnnulment_Throws()
    {
        await using var ctx = new AppDbContext(_dbOptions);
        await SeedReservaWithInvoiceAsync(ctx, cae: "12345678901234", annulmentStatus: AnnulmentStatus.Failed);

        var service = BuildService(ctx);
        var publicId = (await ctx.Reservas.AsNoTracking().FirstAsync()).PublicId;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdateStatusAsync(publicId.ToString(), EstadoReserva.Cancelled, "admin-1", CancellationToken.None));
    }

    [Fact]
    public async Task Cancel_Reserva_WithAnnulledCaeInvoice_Succeeds()
    {
        await using var ctx = new AppDbContext(_dbOptions);
        await SeedReservaWithInvoiceAsync(ctx, cae: "12345678901234", annulmentStatus: AnnulmentStatus.Succeeded);

        var service = BuildService(ctx);
        var publicId = (await ctx.Reservas.AsNoTracking().FirstAsync()).PublicId;

        var dto = await service.UpdateStatusAsync(publicId.ToString(), EstadoReserva.Cancelled, "admin-1", CancellationToken.None);

        Assert.Equal(EstadoReserva.Cancelled, dto.Status);
    }

    [Fact]
    public async Task Cancel_Reserva_WithoutAnyInvoice_Succeeds()
    {
        await using var ctx = new AppDbContext(_dbOptions);
        ctx.Reservas.Add(new Reserva
        {
            Id = 1,
            NumeroReserva = "F-CAE-NONE",
            Name = "Reserva sin factura",
            Status = EstadoReserva.Confirmed,
            ResponsibleUserId = "vendedor-1"
        });
        await ctx.SaveChangesAsync();

        var service = BuildService(ctx);
        var publicId = (await ctx.Reservas.AsNoTracking().FirstAsync()).PublicId;

        var dto = await service.UpdateStatusAsync(publicId.ToString(), EstadoReserva.Cancelled, "admin-1", CancellationToken.None);

        Assert.Equal(EstadoReserva.Cancelled, dto.Status);
    }

    [Fact]
    public async Task Cancel_Reserva_WithInvoiceWithoutCae_Succeeds()
    {
        // Invoice sin CAE = no fue confirmada por AFIP -> no hay riesgo fiscal.
        await using var ctx = new AppDbContext(_dbOptions);
        await SeedReservaWithInvoiceAsync(ctx, cae: null, annulmentStatus: AnnulmentStatus.None);

        var service = BuildService(ctx);
        var publicId = (await ctx.Reservas.AsNoTracking().FirstAsync()).PublicId;

        var dto = await service.UpdateStatusAsync(publicId.ToString(), EstadoReserva.Cancelled, "admin-1", CancellationToken.None);

        Assert.Equal(EstadoReserva.Cancelled, dto.Status);
    }

    // ===== Fix NC 2026-05-30: las Notas de Credito NO cuentan como "factura viva" =====
    // El guard de cancelacion debe comportarse igual que MutationGuards: una NC nace
    // para anular/corregir, nunca se anula a si misma; contarla bloqueaba la reserva
    // para siempre tras una NC TOTAL.

    private static void AddReserva(AppDbContext ctx)
    {
        ctx.Reservas.Add(new Reserva
        {
            Id = 1,
            NumeroReserva = "F-NC-0001",
            Name = "Reserva con NC",
            Status = EstadoReserva.Confirmed,
            ResponsibleUserId = "vendedor-1"
        });
    }

    private static Invoice MakeInvoice(int id, int tipoComprobante, string? cae, AnnulmentStatus status)
        => new()
        {
            Id = id,
            ReservaId = 1,
            ImporteTotal = 100m,
            CAE = cae,
            AnnulmentStatus = status,
            TipoComprobante = tipoComprobante,
            PublicId = Guid.NewGuid()
        };

    [Fact]
    public async Task Cancel_Reserva_AnnulledInvoicePlusTotalCreditNote_Succeeds()
    {
        // EL FIX: factura original anulada (Succeeded) + NC TOTAL viva (tipo 8, CAE,
        // None). La NC se excluye -> LIBERA (antes quedaba bloqueada para siempre).
        await using var ctx = new AppDbContext(_dbOptions);
        AddReserva(ctx);
        ctx.Invoices.Add(MakeInvoice(1, tipoComprobante: 6, cae: "11111111111111", status: AnnulmentStatus.Succeeded));
        ctx.Invoices.Add(MakeInvoice(2, tipoComprobante: 8, cae: "22222222222222", status: AnnulmentStatus.None));
        await ctx.SaveChangesAsync();

        var service = BuildService(ctx);
        var publicId = (await ctx.Reservas.AsNoTracking().FirstAsync()).PublicId;

        var dto = await service.UpdateStatusAsync(publicId.ToString(), EstadoReserva.Cancelled, "admin-1", CancellationToken.None);

        Assert.Equal(EstadoReserva.Cancelled, dto.Status);
    }

    [Fact]
    public async Task Cancel_Reserva_LiveInvoicePlusPartialCreditNote_Throws()
    {
        // NC PARCIAL: la factura original sigue viva (None) -> BLOQUEA. La NC excluida
        // no cambia el resultado (decision del dueño: bloqueo total en parcial).
        await using var ctx = new AppDbContext(_dbOptions);
        AddReserva(ctx);
        ctx.Invoices.Add(MakeInvoice(1, tipoComprobante: 6, cae: "11111111111111", status: AnnulmentStatus.None));
        ctx.Invoices.Add(MakeInvoice(2, tipoComprobante: 8, cae: "22222222222222", status: AnnulmentStatus.None));
        await ctx.SaveChangesAsync();

        var service = BuildService(ctx);
        var publicId = (await ctx.Reservas.AsNoTracking().FirstAsync()).PublicId;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdateStatusAsync(publicId.ToString(), EstadoReserva.Cancelled, "admin-1", CancellationToken.None));
        Assert.Contains("CAE", ex.Message);
    }

    [Fact]
    public async Task Cancel_Reserva_OnlyCreditNoteNoLiveInvoice_Succeeds()
    {
        // Solo una NC con CAE y ninguna factura viva -> LIBERA.
        await using var ctx = new AppDbContext(_dbOptions);
        AddReserva(ctx);
        ctx.Invoices.Add(MakeInvoice(1, tipoComprobante: 8, cae: "22222222222222", status: AnnulmentStatus.None));
        await ctx.SaveChangesAsync();

        var service = BuildService(ctx);
        var publicId = (await ctx.Reservas.AsNoTracking().FirstAsync()).PublicId;

        var dto = await service.UpdateStatusAsync(publicId.ToString(), EstadoReserva.Cancelled, "admin-1", CancellationToken.None);

        Assert.Equal(EstadoReserva.Cancelled, dto.Status);
    }

    [Fact]
    public async Task Cancel_Reserva_NewInvoiceAfterCreditNote_Throws()
    {
        // Falso negativo a evitar: una FACTURA nueva emitida DESPUES de una NC sigue
        // siendo factura y DEBE bloquear. No se excluye por convivir con una NC.
        await using var ctx = new AppDbContext(_dbOptions);
        AddReserva(ctx);
        ctx.Invoices.Add(MakeInvoice(1, tipoComprobante: 6, cae: "11111111111111", status: AnnulmentStatus.Succeeded)); // 1a factura anulada
        ctx.Invoices.Add(MakeInvoice(2, tipoComprobante: 8, cae: "22222222222222", status: AnnulmentStatus.None));      // NC
        ctx.Invoices.Add(MakeInvoice(3, tipoComprobante: 6, cae: "33333333333333", status: AnnulmentStatus.None));      // factura NUEVA viva
        await ctx.SaveChangesAsync();

        var service = BuildService(ctx);
        var publicId = (await ctx.Reservas.AsNoTracking().FirstAsync()).PublicId;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdateStatusAsync(publicId.ToString(), EstadoReserva.Cancelled, "admin-1", CancellationToken.None));
    }
}
