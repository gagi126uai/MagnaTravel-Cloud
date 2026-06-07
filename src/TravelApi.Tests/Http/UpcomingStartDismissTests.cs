using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TravelApi.Application.Interfaces;
using TravelApi.Authorization;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Http;

/// <summary>
/// ADR-019 D4: tests E2E de <c>POST /api/alerts/upcoming-starts/{reservaPublicId}/dismiss</c> (el
/// boton "Listo" del aviso "Proximos inicios").
///
/// <para><b>Matriz de status codes (M-B del ADR)</b>: vendedor owner → 204; vendedor NO-owner → 403
/// (filtro de ownership); vendedor con publicId INEXISTENTE → tambien 403 (el filtro corta ANTES del
/// controller — OwnershipResolver devuelve false para inexistente igual que para ajena, sin filtrar
/// existencia); Admin/reservas.view_all con inexistente → 404 del controller; flag OFF → 404.</para>
///
/// <para><b>Nota M4 (idempotencia vs carrera)</b>: el provider InMemory de la factory NO aplica el
/// indice UNIQUE de Postgres. El test de doble POST de aca prueba la LOGICA del upsert (misma fila,
/// estado final identico), NO la carrera de dos POST concurrentes — esa garantia vive en el UNIQUE
/// real y se valida en los tests de integracion Postgres del VPS.</para>
/// </summary>
public class UpcomingStartDismissTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public UpcomingStartDismissTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static System.DateTime Today => TravelApi.Infrastructure.Time.AgencyTimezone.TodayWallClockUtc();

    /// <summary>Prende/apaga el flag de los avisos en la fila de settings compartida de la factory.</summary>
    private async Task SetFlagAsync(bool enabled)
    {
        using var scope = _factory.Services.CreateScope();
        var settingsService = scope.ServiceProvider.GetRequiredService<IOperationalFinanceSettingsService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var settings = await settingsService.GetEntityAsync(CancellationToken.None);
        settings.EnableServiceDeadlineAlerts = enabled;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Sembra una reserva confirmada del vendedor indicado, opcionalmente con un hotel que empieza
    /// en 3 dias (el "primer inicio" que el server va a anclar al descartar).
    /// </summary>
    private async Task<Reserva> SeedReservaAsync(string suffix, string? responsibleUserId, bool withService = true)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var reserva = new Reserva
        {
            PublicId = Guid.NewGuid(),
            Name = "Dismiss " + suffix,
            NumeroReserva = "DIS-" + suffix,
            Status = EstadoReserva.Confirmed,
            ResponsibleUserId = responsibleUserId,
        };
        db.Reservas.Add(reserva);
        await db.SaveChangesAsync();

        if (withService)
        {
            db.HotelBookings.Add(new HotelBooking
            {
                ReservaId = reserva.Id,
                HotelName = "Hotel " + suffix,
                City = "C",
                CheckIn = Today.AddDays(3),
                CheckOut = Today.AddDays(5),
            });
            await db.SaveChangesAsync();
        }

        return reserva;
    }

    private HttpClient CreateClient(string userId, string roles)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserIdHeader, userId);
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserRolesHeader, roles);
        return client;
    }

    private static string DismissUrl(object reservaId) => $"/api/alerts/upcoming-starts/{reservaId}/dismiss";

    private async Task<UpcomingStartAlertDismissal?> GetDismissalAsync(int reservaId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.UpcomingStartAlertDismissals.AsNoTracking()
            .SingleOrDefaultAsync(d => d.ReservaId == reservaId);
    }

    // ===================== happy path + idempotencia =====================

    [Fact]
    public async Task OwnerVendedor_Dismiss_Returns204_AndPersistsServerComputedFirstStart()
    {
        await SetFlagAsync(enabled: true);
        var reserva = await SeedReservaAsync("own1", "vend-dis-1");
        var client = CreateClient("vend-dis-1", "Vendedor");

        var response = await client.PostAsync(DismissUrl(reserva.PublicId), content: null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var row = await GetDismissalAsync(reserva.Id);
        Assert.NotNull(row);
        // El server calculo y anclo el primer inicio el solo (el cliente no mando ninguna fecha).
        Assert.Equal(Today.AddDays(3), row!.DismissedFirstStartDate);
        Assert.Equal("vend-dis-1", row.DismissedByUserId);
    }

    [Fact]
    public async Task DoublePost_Idempotent_SingleRowSameFinalState()
    {
        // OJO M4: InMemory no aplica el UNIQUE — esto prueba la logica del upsert, no la carrera.
        await SetFlagAsync(enabled: true);
        var reserva = await SeedReservaAsync("idem1", "vend-dis-2");
        var client = CreateClient("vend-dis-2", "Vendedor");

        var first = await client.PostAsync(DismissUrl(reserva.PublicId), content: null);
        var second = await client.PostAsync(DismissUrl(reserva.PublicId), content: null);

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await db.UpcomingStartAlertDismissals.AsNoTracking()
            .Where(d => d.ReservaId == reserva.Id)
            .ToListAsync();
        var row = Assert.Single(rows); // una sola fila por reserva
        Assert.Equal(Today.AddDays(3), row.DismissedFirstStartDate);
    }

    [Fact]
    public async Task ReservaWithoutServices_Returns204NoOp_WritesNothing()
    {
        await SetFlagAsync(enabled: true);
        var reserva = await SeedReservaAsync("noop1", "vend-dis-3", withService: false);
        var client = CreateClient("vend-dis-3", "Vendedor");

        var response = await client.PostAsync(DismissUrl(reserva.PublicId), content: null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Null(await GetDismissalAsync(reserva.Id)); // no-op real: ni una fila
    }

    // ===================== autorizacion (filtro de ownership) =====================

    [Fact]
    public async Task NonOwnerVendedor_Returns403()
    {
        await SetFlagAsync(enabled: true);
        var reserva = await SeedReservaAsync("ajena1", "otro-owner");
        var client = CreateClient("vend-dis-4", "Vendedor");

        var response = await client.PostAsync(DismissUrl(reserva.PublicId), content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(await GetDismissalAsync(reserva.Id));
    }

    [Fact]
    public async Task Vendedor_NonexistentPublicId_Returns403_NeverSees404()
    {
        // M-B: para el vendedor comun "no existe" y "no es tuya" son INDISTINGUIBLES — el filtro
        // devuelve 403 antes de llegar al controller (OwnershipResolver no filtra existencia).
        await SetFlagAsync(enabled: true);
        var client = CreateClient("vend-dis-5", "Vendedor");

        var response = await client.PostAsync(DismissUrl(Guid.NewGuid()), content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Admin_NonexistentPublicId_Returns404()
    {
        // El 404 del controller solo lo ve quien bypassea el filtro (Admin / reservas.view_all).
        await SetFlagAsync(enabled: true);
        var client = CreateClient("admin-dis-1", "Admin");

        var response = await client.PostAsync(DismissUrl(Guid.NewGuid()), content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Admin_DismissesForeignReserva_Returns204_AuditedWithAdminUserId()
    {
        // Q1: el descarte es GLOBAL y quien puede VER el aviso puede marcarlo gestionado;
        // la supervision queda en la auditoria (DismissedByUserId), no en el re-aviso.
        await SetFlagAsync(enabled: true);
        var reserva = await SeedReservaAsync("adm1", "algun-vendedor");
        var client = CreateClient("admin-dis-2", "Admin");

        var response = await client.PostAsync(DismissUrl(reserva.PublicId), content: null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var row = await GetDismissalAsync(reserva.Id);
        Assert.NotNull(row);
        Assert.Equal("admin-dis-2", row!.DismissedByUserId); // quedo registrado QUIEN lo apago
    }

    [Fact]
    public async Task ViewAllUser_DismissesForeignReserva_Returns204_AndNonexistent404()
    {
        // reservas.view_all bypassea el filtro igual que Admin (mismo poder de descarte, Q1).
        // El permiso se resuelve contra la DB (UserManager + RolePermissions), no contra claims:
        // hay que sembrar usuario + rol + fila de RolePermission.
        await SetFlagAsync(enabled: true);
        const string supervisorId = "super-dis-1";
        using (var scope = _factory.Services.CreateScope())
        {
            var userMgr = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var roleMgr = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            if (!await roleMgr.RoleExistsAsync("SupervisorDis"))
                await roleMgr.CreateAsync(new IdentityRole("SupervisorDis"));
            if (!await db.RolePermissions.AnyAsync(rp => rp.RoleName == "SupervisorDis" && rp.Permission == Permissions.ReservasViewAll))
            {
                db.RolePermissions.Add(new RolePermission { RoleName = "SupervisorDis", Permission = Permissions.ReservasViewAll });
                await db.SaveChangesAsync();
            }
            if (await userMgr.FindByIdAsync(supervisorId) is null)
            {
                var user = new ApplicationUser
                {
                    Id = supervisorId,
                    UserName = supervisorId + "@t.local",
                    Email = supervisorId + "@t.local",
                    FullName = "Supervisor Dismiss",
                    IsActive = true,
                };
                await userMgr.CreateAsync(user, "Test1234!Aa");
                await userMgr.AddToRoleAsync(user, "SupervisorDis");
            }
        }

        var reserva = await SeedReservaAsync("sup1", "otro-vendedor");
        var client = CreateClient(supervisorId, "SupervisorDis");

        var okResponse = await client.PostAsync(DismissUrl(reserva.PublicId), content: null);
        Assert.Equal(HttpStatusCode.NoContent, okResponse.StatusCode);
        var row = await GetDismissalAsync(reserva.Id);
        Assert.Equal(supervisorId, row!.DismissedByUserId);

        var notFoundResponse = await client.PostAsync(DismissUrl(Guid.NewGuid()), content: null);
        Assert.Equal(HttpStatusCode.NotFound, notFoundResponse.StatusCode);
    }

    // ===================== flag OFF =====================

    [Fact]
    public async Task FlagOff_OwnerGets404_AndNothingIsWritten()
    {
        await SetFlagAsync(enabled: false);
        try
        {
            var reserva = await SeedReservaAsync("off1", "vend-dis-6");
            var client = CreateClient("vend-dis-6", "Vendedor");

            var response = await client.PostAsync(DismissUrl(reserva.PublicId), content: null);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Null(await GetDismissalAsync(reserva.Id));
        }
        finally
        {
            // La fila de settings es compartida por toda la factory: dejarla ON para no acoplar
            // este test con los que corran despues.
            await SetFlagAsync(enabled: true);
        }
    }
}

/// <summary>
/// ADR-019 §5 — el caso 401 "sin identidad" no se puede ejercitar con la CustomWebApplicationFactory
/// (su TestAuthHandler SIEMPRE autentica), asi que se prueba el filtro directo: el mismo
/// RequireOwnershipAttribute que decora el endpoint de dismiss corta con 401 ante un principal
/// sin autenticar o sin claim NameIdentifier, ANTES de tocar resolver alguno.
/// </summary>
public class UpcomingStartDismissAuthorizationFilterTests
{
    private static AuthorizationFilterContext BuildFilterContext(System.Security.Claims.ClaimsPrincipal user)
    {
        var httpContext = new DefaultHttpContext { User = user };
        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        return new AuthorizationFilterContext(actionContext, new List<IFilterMetadata>());
    }

    [Fact]
    public async Task UnauthenticatedPrincipal_Returns401()
    {
        var attribute = new RequireOwnershipAttribute(
            OwnedEntity.Reserva, routeParam: "reservaPublicId", bypassPermission: Permissions.ReservasViewAll);
        // DefaultHttpContext trae un principal con identidad NO autenticada (IsAuthenticated=false).
        var context = BuildFilterContext(new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity()));

        await attribute.OnAuthorizationAsync(context);

        Assert.IsType<UnauthorizedResult>(context.Result);
    }

    [Fact]
    public async Task AuthenticatedWithoutNameIdentifierClaim_Returns401()
    {
        var attribute = new RequireOwnershipAttribute(
            OwnedEntity.Reserva, routeParam: "reservaPublicId", bypassPermission: Permissions.ReservasViewAll);
        // Autenticado pero sin claim NameIdentifier (UserId null): tambien 401, fail-closed.
        var identity = new System.Security.Claims.ClaimsIdentity(authenticationType: "Test");
        var context = BuildFilterContext(new System.Security.Claims.ClaimsPrincipal(identity));

        await attribute.OnAuthorizationAsync(context);

        Assert.IsType<UnauthorizedResult>(context.Result);
    }

    /// <summary>
    /// Pin del decorado del endpoint: si alguien borra o cambia el [RequireOwnership] del dismiss
    /// (entidad, route param o bypass), este test rompe ANTES de que un E2E lo descubra.
    /// </summary>
    [Fact]
    public void DismissEndpoint_CarriesRequireOwnershipWithViewAllBypass()
    {
        var method = typeof(TravelApi.Controllers.AlertsController).GetMethod("DismissUpcomingStart");
        Assert.NotNull(method);

        var attribute = method!.GetCustomAttributes(typeof(RequireOwnershipAttribute), inherit: false)
            .Cast<RequireOwnershipAttribute>()
            .SingleOrDefault();

        Assert.NotNull(attribute);
        Assert.Equal(Permissions.ReservasViewAll, attribute!.BypassPermission);
    }
}
