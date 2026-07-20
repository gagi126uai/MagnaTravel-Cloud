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
/// Tanda 4 del plan "contrato pantalla-motor" (2026-07-20, M2/M4): el swap de
/// <c>dto.ServiceCancellationBlockReason</c> en <c>ReservaService.GetReservaByIdAsync</c>. Antes ese
/// campo se calculaba con la regla VIEJA (factura CAE viva O voucher emitido, <c>GetReservaCancellationBlockReasonAsync</c>)
/// y frenaba de mas: reservas facturadas-sin-voucher aparecian "no se puede anular" aunque el guard real
/// que corre al anular un servicio (<c>BookingCancellationService.CancelServiceAsync</c>, ADR-044 T5) ya
/// las dejaba anular sin problema. El swap pasa a usar <c>GetReservaVoucherOnlyBlockReasonAsync</c> (SOLO
/// voucher emitido), la MISMA fuente que usa el guard real, para que la pantalla y el motor nunca diverjan.
///
/// <para>Casos cubiertos:
/// <list type="bullet">
/// <item>Reserva facturada (CAE viva) pero SIN voucher -> el campo tiene que dar <c>null</c> (ya NO frena).</item>
/// <item>Reserva CON voucher emitido -> el campo sigue bloqueado (esa regla no cambio).</item>
/// </list></para>
/// </summary>
public class ReservaServiceCancellationBlockReasonSwapTests
{
    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
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

    private static ReservaService CreateService(AppDbContext context)
    {
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OperationalFinanceSettings());

        const string userId = "admin-test";
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId),
            new(ClaimTypes.Role, "Admin"),
        };
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
        };

        var resolver = new Mock<IUserPermissionResolver>();
        IReadOnlySet<string> perms = new HashSet<string>();
        resolver.Setup(r => r.GetPermissionsAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(perms);

        return new ReservaService(
            context,
            new MapperConfiguration(c => c.AddProfile<MappingProfile>()).CreateMapper(),
            settings.Object,
            BuildUserManager(),
            NullLogger<ReservaService>.Instance,
            resolver.Object,
            accessor);
    }

    // Reserva "En gestion" (Confirmed) lista para pre-bloquear/no pre-bloquear la anulacion de servicios.
    private static async Task SeedReservaAsync(AppDbContext context, int id)
    {
        context.Reservas.Add(new Reserva
        {
            Id = id,
            NumeroReserva = $"F-2026-{id:D4}",
            Name = "Reserva swap T4",
            Status = EstadoReserva.Confirmed,
        });
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task ReservaFacturadaSinVoucher_NoBloqueaMas()
    {
        // Antes del swap esta reserva daba block reason "factura CAE viva" (regla vieja). El motor real ya
        // permite anular sus servicios (con NC de por medio) desde ADR-044 T5, asi que la pantalla tiene que
        // dejar de frenar de mas.
        await using var context = CreateContext();
        await SeedReservaAsync(context, id: 1);
        context.Invoices.Add(new Invoice
        {
            Id = 50,
            ReservaId = 1,
            CAE = "012345",
            AnnulmentStatus = AnnulmentStatus.None,
            TipoComprobante = 6, // Factura B, no es NC
            ImporteTotal = 100m,
            ImporteNeto = 82.64m,
            ImporteIva = 17.36m,
        });
        await context.SaveChangesAsync();

        var service = CreateService(context);
        var dto = await service.GetReservaByIdAsync("1", CancellationToken.None);

        Assert.Null(dto.ServiceCancellationBlockReason);
    }

    [Fact]
    public async Task ReservaConVoucherEmitido_SigueBloqueada()
    {
        // El candado de voucher NO cambio: un voucher ya entregado al cliente sigue frenando la anulacion.
        await using var context = CreateContext();
        await SeedReservaAsync(context, id: 2);
        context.Vouchers.Add(new Voucher
        {
            Id = 60,
            ReservaId = 2,
            FileName = "voucher.pdf",
            Status = VoucherStatuses.Issued,
        });
        await context.SaveChangesAsync();

        var service = CreateService(context);
        var dto = await service.GetReservaByIdAsync("2", CancellationToken.None);

        Assert.NotNull(dto.ServiceCancellationBlockReason);
        Assert.Contains("anular", dto.ServiceCancellationBlockReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("vouchers emitidos", dto.ServiceCancellationBlockReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReservaSinFacturaNiVoucher_NoBloquea()
    {
        // Caso base: nada que frene -> se puede anular sin restricciones fiscales.
        await using var context = CreateContext();
        await SeedReservaAsync(context, id: 3);

        var service = CreateService(context);
        var dto = await service.GetReservaByIdAsync("3", CancellationToken.None);

        Assert.Null(dto.ServiceCancellationBlockReason);
    }
}
