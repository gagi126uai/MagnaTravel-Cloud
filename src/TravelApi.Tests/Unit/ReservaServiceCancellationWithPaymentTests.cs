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
using TravelApi.Application.Interfaces;
using TravelApi.Application.Mappings;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// B1.15 Fase 2a (Decision 6) — Cancelacion de reservas con cobros/facturas.
/// Pinea: si el actor NO es Admin, exige reservas.cancel; si la reserva tiene
/// pagos o facturas, exige ademas reservas.cancel_with_payment.
/// </summary>
public class ReservaServiceCancellationWithPaymentTests
{
    private readonly DbContextOptions<AppDbContext> _dbOptions;
    private readonly IMapper _mapper;
    private readonly Mock<IOperationalFinanceSettingsService> _settingsServiceMock;

    public ReservaServiceCancellationWithPaymentTests()
    {
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        // AutoMapper real porque UpdateStatusAsync(string, ...) llama a
        // GetReservaByIdAsync que mapea Reserva -> ReservaDto.
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
        foreach (var r in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, r));
        }
        var ctx = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) };
        return new HttpContextAccessor { HttpContext = ctx };
    }

    private static IUserPermissionResolver BuildResolver(string userId, params string[] permissions)
    {
        var mock = new Mock<IUserPermissionResolver>();
        IReadOnlySet<string> set = new HashSet<string>(permissions);
        mock.Setup(r => r.GetPermissionsAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(set);
        return mock.Object;
    }

    private ReservaService BuildService(AppDbContext context, IHttpContextAccessor accessor, IUserPermissionResolver resolver)
        => new(context, _mapper, _settingsServiceMock.Object, BuildUserManager(),
               NullLogger<ReservaService>.Instance, resolver, accessor);

    private static async Task SeedReserva(AppDbContext ctx, bool withPayment, bool withInvoice)
    {
        ctx.Reservas.Add(new Reserva
        {
            Id = 1,
            NumeroReserva = "F-2026-0001",
            Name = "Reserva test",
            Status = EstadoReserva.Confirmed,
            ResponsibleUserId = "vendedor-1"
        });
        if (withPayment)
        {
            ctx.Payments.Add(new Payment
            {
                Id = 1,
                ReservaId = 1,
                Amount = 100m,
                Status = "Paid",
                PublicId = Guid.NewGuid()
            });
        }
        if (withInvoice)
        {
            ctx.Invoices.Add(new Invoice
            {
                Id = 1,
                ReservaId = 1,
                ImporteTotal = 100m,
                PublicId = Guid.NewGuid()
            });
        }
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task Cancel_without_payments_with_cancel_permission_succeeds()
    {
        await using var ctx = new AppDbContext(_dbOptions);
        await SeedReserva(ctx, withPayment: false, withInvoice: false);
        var accessor = BuildContextAccessor("vendedor-1", "Vendedor");
        var resolver = BuildResolver("vendedor-1", Permissions.ReservasCancel);
        var service = BuildService(ctx, accessor, resolver);

        var publicId = (await ctx.Reservas.AsNoTracking().FirstAsync()).PublicId;

        var dto = await service.UpdateStatusAsync(publicId.ToString(), EstadoReserva.Cancelled, "vendedor-1", CancellationToken.None);

        Assert.Equal(EstadoReserva.Cancelled, dto.Status);
    }

    [Fact]
    public async Task Cancel_without_cancel_permission_throws_403()
    {
        await using var ctx = new AppDbContext(_dbOptions);
        await SeedReserva(ctx, withPayment: false, withInvoice: false);
        var accessor = BuildContextAccessor("vendedor-1", "Vendedor");
        var resolver = BuildResolver("vendedor-1"); // sin reservas.cancel
        var service = BuildService(ctx, accessor, resolver);

        var publicId = (await ctx.Reservas.AsNoTracking().FirstAsync()).PublicId;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.UpdateStatusAsync(publicId.ToString(), EstadoReserva.Cancelled, "vendedor-1", CancellationToken.None));
    }

    [Fact]
    public async Task Cancel_with_payments_without_cancel_with_payment_throws_403()
    {
        await using var ctx = new AppDbContext(_dbOptions);
        await SeedReserva(ctx, withPayment: true, withInvoice: false);
        var accessor = BuildContextAccessor("vendedor-1", "Vendedor");
        var resolver = BuildResolver("vendedor-1", Permissions.ReservasCancel);
        var service = BuildService(ctx, accessor, resolver);

        var publicId = (await ctx.Reservas.AsNoTracking().FirstAsync()).PublicId;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.UpdateStatusAsync(publicId.ToString(), EstadoReserva.Cancelled, "vendedor-1", CancellationToken.None));
    }

    [Fact]
    public async Task Cancel_with_payments_and_full_permissions_succeeds()
    {
        await using var ctx = new AppDbContext(_dbOptions);
        await SeedReserva(ctx, withPayment: true, withInvoice: false);
        var accessor = BuildContextAccessor("colab-1", "Colaborador");
        var resolver = BuildResolver("colab-1", Permissions.ReservasCancel, Permissions.ReservasCancelWithPayment);
        var service = BuildService(ctx, accessor, resolver);

        var publicId = (await ctx.Reservas.AsNoTracking().FirstAsync()).PublicId;

        var dto = await service.UpdateStatusAsync(publicId.ToString(), EstadoReserva.Cancelled, "colab-1", CancellationToken.None);
        Assert.Equal(EstadoReserva.Cancelled, dto.Status);
    }

    [Fact]
    public async Task Cancel_with_invoices_without_cancel_with_payment_throws_403()
    {
        await using var ctx = new AppDbContext(_dbOptions);
        await SeedReserva(ctx, withPayment: false, withInvoice: true);
        var accessor = BuildContextAccessor("vendedor-1", "Vendedor");
        var resolver = BuildResolver("vendedor-1", Permissions.ReservasCancel);
        var service = BuildService(ctx, accessor, resolver);

        var publicId = (await ctx.Reservas.AsNoTracking().FirstAsync()).PublicId;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.UpdateStatusAsync(publicId.ToString(), EstadoReserva.Cancelled, "vendedor-1", CancellationToken.None));
    }

    [Fact]
    public async Task Cancel_as_Admin_bypasses_permission_checks()
    {
        await using var ctx = new AppDbContext(_dbOptions);
        await SeedReserva(ctx, withPayment: true, withInvoice: true);
        // Admin sin permisos explicitos en el resolver — el service bypassa por rol.
        var accessor = BuildContextAccessor("admin-1", "Admin");
        var resolver = BuildResolver("admin-1");
        var service = BuildService(ctx, accessor, resolver);

        var publicId = (await ctx.Reservas.AsNoTracking().FirstAsync()).PublicId;
        var dto = await service.UpdateStatusAsync(publicId.ToString(), EstadoReserva.Cancelled, "admin-1", CancellationToken.None);
        Assert.Equal(EstadoReserva.Cancelled, dto.Status);
    }

    [Fact]
    public async Task Cancel_when_actorUserId_is_null_preserves_legacy_behavior()
    {
        // Sin actor concreto (path de tests unitarios sin auth real), no se aplica
        // el chequeo de cancel/cancel_with_payment. Comportamiento legacy preservado.
        await using var ctx = new AppDbContext(_dbOptions);
        await SeedReserva(ctx, withPayment: true, withInvoice: false);
        var accessor = BuildContextAccessor("ignored");
        var resolver = BuildResolver("ignored");
        var service = BuildService(ctx, accessor, resolver);

        var publicId = (await ctx.Reservas.AsNoTracking().FirstAsync()).PublicId;
        var dto = await service.UpdateStatusAsync(publicId.ToString(), EstadoReserva.Cancelled, actorUserId: null, CancellationToken.None);
        Assert.Equal(EstadoReserva.Cancelled, dto.Status);
    }
}
