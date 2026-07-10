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
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Application.Mappings;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Reservations;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Issue 2a (2026-06-28): el detalle de la reserva ofrece la accion "Confirmar multa / Cerrar sin multa"
/// (capacidad <c>CanConfirmOperatorPenalty</c>) SOLO a quien tiene el permiso
/// <c>cancellations.classify_agency_penalty</c> (o es Admin). Antes la capacidad ignoraba el permiso: un
/// vendedor veia los botones, los clickeaba, y el backend rebotaba 409 filtrando el slug del permiso. Ahora
/// los botones no se ofrecen a quien no puede usarlos.
///
/// <para>Issue 1 (2026-06-28): ademas el detalle expone <c>OperatorPenaltyOutcome</c> (None/Pending/Confirmed/
/// Waived) para que la ficha pinte "Cerrada sin multa del operador" al cargar, sin pedir aparte el detalle de
/// la cancelacion. Ese campo es informativo y NO depende del permiso (es un estado, no una accion).</para>
/// </summary>
public class ReservaServiceOperatorPenaltyCapabilityTests
{
    private readonly DbContextOptions<AppDbContext> _dbOptions;
    private readonly IMapper _mapper;
    private readonly Mock<IOperationalFinanceSettingsService> _settingsServiceMock;

    public ReservaServiceOperatorPenaltyCapabilityTests()
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

    /// <summary>
    /// Mock de la cancelacion que devuelve el outcome pedido para CUALQUIER reserva. Stubea DOS metodos:
    /// el viejo <c>GetOperatorPenaltyOutcomeAsync</c> (que estos tests ya usaban) y el nuevo
    /// <c>GetOperatorPenaltySituationAsync</c> de la spec "el paso de multa vive en la ficha" (2026-07-08), que
    /// <c>ReservaService.GetReservaByIdAsync</c> ahora tambien llama para armar el read-model detallado. Si solo se
    /// stubea el metodo viejo, Moq (MockBehavior.Loose) devuelve null para el nuevo -> el service lo detecta y cae
    /// al DTO por defecto en "None", lo que haria que estos tests reciban SIEMPRE "None" sin importar el outcome
    /// pedido. Por eso mapeamos el outcome grueso al <see cref="OperatorPenaltySituationState"/> fino equivalente
    /// (la relacion inversa de <see cref="OperatorPenaltySituationRules.ToOutcome"/>), para que ambos metodos
    /// cuenten la misma historia.
    /// </summary>
    private static IBookingCancellationService BuildCancellationService(OperatorPenaltyOutcome outcome)
    {
        var mock = new Mock<IBookingCancellationService>();
        mock.Setup(s => s.GetOperatorPenaltyOutcomeAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(outcome);

        var situationState = outcome switch
        {
            OperatorPenaltyOutcome.Pending => OperatorPenaltySituationState.PendingDecision,
            OperatorPenaltyOutcome.Waived => OperatorPenaltySituationState.Waived,
            // Confirmed colapsa a varios sub-estados finos; "Done" (ND emitida) alcanza para estos tests, que solo
            // miran el outcome grueso resultante, no el detalle de la ND.
            OperatorPenaltyOutcome.Confirmed => OperatorPenaltySituationState.Done,
            _ => OperatorPenaltySituationState.None,
        };
        mock.Setup(s => s.GetOperatorPenaltySituationAsync(
                It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperatorPenaltySituationDto { State = situationState.ToString() });

        // ADR-044 T1 (2026-07-10): ReservaService ahora deriva el singular + outcome de la version LISTA (un solo
        // calculo del principal por request). Stubeamos la lista para que su PRIMER elemento cuente la misma
        // historia que el singular. Para el outcome None devolvemos lista VACIA (la implementacion real devuelve
        // Array.Empty cuando no hay nada que mostrar), lo que deja el DTO en sus defaults (singular "None").
        IReadOnlyList<OperatorPenaltySituationDto> situations = situationState == OperatorPenaltySituationState.None
            ? Array.Empty<OperatorPenaltySituationDto>()
            : new[] { new OperatorPenaltySituationDto { State = situationState.ToString() } };
        mock.Setup(s => s.GetOperatorPenaltySituationsAsync(
                It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(situations);

        return mock.Object;
    }

    private ReservaService BuildService(
        AppDbContext context,
        IHttpContextAccessor accessor,
        IUserPermissionResolver resolver,
        OperatorPenaltyOutcome outcome)
        => new(context, _mapper, _settingsServiceMock.Object, BuildUserManager(),
               NullLogger<ReservaService>.Instance, resolver, accessor,
               autoStateService: null, auditService: null,
               cancellationService: BuildCancellationService(outcome));

    private static async Task SeedReserva(AppDbContext ctx)
    {
        ctx.Reservas.Add(new Reserva
        {
            Id = 1,
            NumeroReserva = "F-2026-0001",
            Name = "Reserva con cancelacion",
            Status = EstadoReserva.PendingOperatorRefund,
            ResponsibleUserId = "vendedor-1",
        });
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task PendingPenalty_userWithClassifyPermission_seesButtons()
    {
        await using var ctx = new AppDbContext(_dbOptions);
        await SeedReserva(ctx);
        var accessor = BuildContextAccessor("colab-1", "Colaborador");
        var resolver = BuildResolver("colab-1", Permissions.CancellationsClassifyAgencyPenalty);
        var service = BuildService(ctx, accessor, resolver, OperatorPenaltyOutcome.Pending);

        var dto = await service.GetReservaByIdAsync("1", CancellationToken.None);

        Assert.True(dto.Capabilities.CanConfirmOperatorPenalty.Allowed);
        Assert.Equal("Pending", dto.Capabilities.OperatorPenaltyOutcome);
    }

    [Fact]
    public async Task PendingPenalty_userWithoutClassifyPermission_doesNotSeeButtons()
    {
        await using var ctx = new AppDbContext(_dbOptions);
        await SeedReserva(ctx);
        // Vendedor con permiso de cancelar pero SIN classify_agency_penalty.
        var accessor = BuildContextAccessor("vendedor-1", "Vendedor");
        var resolver = BuildResolver("vendedor-1", Permissions.ReservasCancel);
        var service = BuildService(ctx, accessor, resolver, OperatorPenaltyOutcome.Pending);

        var dto = await service.GetReservaByIdAsync("1", CancellationToken.None);

        // El boton NO se ofrece (la raiz del fix: antes lo veia y rebotaba 409 con el slug).
        Assert.False(dto.Capabilities.CanConfirmOperatorPenalty.Allowed);
        // Pero el estado informativo sigue visible (es status, no accion).
        Assert.Equal("Pending", dto.Capabilities.OperatorPenaltyOutcome);
    }

    [Fact]
    public async Task PendingPenalty_admin_seesButtonsEvenWithoutPermission()
    {
        await using var ctx = new AppDbContext(_dbOptions);
        await SeedReserva(ctx);
        // Admin por rol, SIN el permiso explicito en el resolver.
        var accessor = BuildContextAccessor("admin-1", "Admin");
        var resolver = BuildResolver("admin-1" /* sin permisos */);
        var service = BuildService(ctx, accessor, resolver, OperatorPenaltyOutcome.Pending);

        var dto = await service.GetReservaByIdAsync("1", CancellationToken.None);

        Assert.True(dto.Capabilities.CanConfirmOperatorPenalty.Allowed);
    }

    [Fact]
    public async Task WaivedOutcome_exposedAndNoButton()
    {
        await using var ctx = new AppDbContext(_dbOptions);
        await SeedReserva(ctx);
        var accessor = BuildContextAccessor("colab-1", "Colaborador");
        var resolver = BuildResolver("colab-1", Permissions.CancellationsClassifyAgencyPenalty);
        var service = BuildService(ctx, accessor, resolver, OperatorPenaltyOutcome.Waived);

        var dto = await service.GetReservaByIdAsync("1", CancellationToken.None);

        // El front lee esto para mostrar "Cerrada sin multa del operador".
        Assert.Equal("Waived", dto.Capabilities.OperatorPenaltyOutcome);
        // Ya no hay nada pendiente de confirmar -> sin boton (aunque el usuario tenga el permiso).
        Assert.False(dto.Capabilities.CanConfirmOperatorPenalty.Allowed);
    }

    [Fact]
    public async Task NoCancellation_outcomeIsNone()
    {
        await using var ctx = new AppDbContext(_dbOptions);
        await SeedReserva(ctx);
        var accessor = BuildContextAccessor("colab-1", "Colaborador");
        var resolver = BuildResolver("colab-1", Permissions.CancellationsClassifyAgencyPenalty);
        var service = BuildService(ctx, accessor, resolver, OperatorPenaltyOutcome.None);

        var dto = await service.GetReservaByIdAsync("1", CancellationToken.None);

        Assert.Equal("None", dto.Capabilities.OperatorPenaltyOutcome);
        Assert.False(dto.Capabilities.CanConfirmOperatorPenalty.Allowed);
    }
}
