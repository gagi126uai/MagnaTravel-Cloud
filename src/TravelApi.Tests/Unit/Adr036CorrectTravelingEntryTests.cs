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
using TravelApi.Domain.Reservations;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using TravelApi.Infrastructure.Services.Reservations;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-036 (2026-06-22): "Sacar de viaje" — correccion de EXCEPCION de una reserva que entro a "En viaje"
/// por error. Cubre el service <c>CorrectTravelingEntryAsync</c>, la coherencia con el job nocturno (la
/// reserva corregida NO se vuelve a promover, pero el flujo normal sigue intacto), la capacidad pura
/// <c>CanCorrectTravelingEntry</c> y el cruce capacidad/endpoint.
///
/// <para>El PERMISO (reservas.correct_traveling, solo Admin) lo enforza el controller con [RequirePermission];
/// estos tests cubren la logica de NEGOCIO del service y de la capacidad, que NO es bypasseable por nadie.</para>
/// </summary>
public class Adr036CorrectTravelingEntryTests
{
    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static ReservaService CreateService(AppDbContext context)
    {
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OperationalFinanceSettings());

        const string userId = "admin-test";
        // Admin: ve costos, sin masking; el DTO sale completo para asertar capacidades / IsUnderCorrection.
        var accessor = BuildHttpContextAccessor(userId, "Admin");
        var resolver = BuildResolver(userId, Permissions.CobranzasSeeCost);

        return new ReservaService(
            context,
            new MapperConfiguration(c => c.AddProfile<MappingProfile>()).CreateMapper(),
            settings.Object,
            BuildUserManager(),
            NullLogger<ReservaService>.Instance,
            resolver,
            accessor);
    }

    private static ReservaLifecycleAutomationService CreateJob(AppDbContext context)
    {
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OperationalFinanceSettings());
        var engine = new ReservaAutoStateService(context, NullLogger<ReservaAutoStateService>.Instance);
        return new ReservaLifecycleAutomationService(
            context, NullLogger<ReservaLifecycleAutomationService>.Instance, settings.Object, engine);
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
        mock.Setup(r => r.GetPermissionsAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(set);
        return mock.Object;
    }

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

    /// <summary>
    /// Reserva En viaje SALDADA (Balance 0) con un servicio confirmado y StartDate hoy. Es el estado tipico
    /// de una reserva que entro a "En viaje" (por el candado de pago de ADR-036, siempre llega saldada).
    /// </summary>
    private static async Task<Reserva> SeedTravelingAsync(AppDbContext context, decimal balance = 0m)
    {
        var reserva = new Reserva
        {
            Id = 1,
            NumeroReserva = "R-36-1",
            Name = "Reserva en viaje",
            Status = EstadoReserva.Traveling,
            ResponsibleUserId = "admin-test",
            StartDate = DateTime.UtcNow.Date,
            Balance = balance
        };
        context.Reservas.Add(reserva);
        // Un servicio confirmado: una reserva real En viaje tiene servicios (el job exige al menos uno
        // para promover, y el saneamiento cierra las vacias). Lo dejamos para que el control del job sea fiel.
        context.HotelBookings.Add(new HotelBooking
        {
            Id = 10, ReservaId = 1, HotelName = "H", Status = "Confirmado", ConfirmedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
        return reserva;
    }

    private static CorrectTravelingEntryRequest ValidRequest() =>
        new("Fecha cargada mal, el viaje todavia no salio");

    // ============================= (1) Camino feliz =============================

    [Fact]
    public async Task CorrectTravelingEntry_FromTravelingNoCaeNoVoucher_ReturnsToConfirmed_ClearsStartDate_LogsCorrection()
    {
        await using var context = CreateContext();
        var reserva = await SeedTravelingAsync(context);
        // Sembramos una franja de regresion para verificar que se limpia (no debe quedar huerfana).
        reserva.LastRegressionReason = "Algo viejo";
        reserva.LastRegressionAt = DateTime.UtcNow.AddDays(-1);
        await context.SaveChangesAsync();

        var sut = CreateService(context);
        var dto = await sut.CorrectTravelingEntryAsync(
            reserva.PublicId.ToString(), ValidRequest(), "admin-test", "Admin Test", CancellationToken.None);

        var stored = await context.Reservas.AsNoTracking().SingleAsync(r => r.Id == reserva.Id);
        Assert.Equal(EstadoReserva.Confirmed, stored.Status);
        Assert.Null(stored.StartDate);                  // StartDate borrado (sale del filtro del job)
        Assert.Null(stored.LastRegressionReason);       // franja naranja limpiada
        Assert.Null(stored.LastRegressionAt);

        // Rastro auditable con Direction = "Correction" (valor nuevo, exclusivo de esta accion).
        var log = await context.ReservaStatusChangeLogs.AsNoTracking()
            .SingleAsync(l => l.ReservaId == reserva.Id);
        Assert.Equal("Correction", log.Direction);
        Assert.Equal(EstadoReserva.Traveling, log.FromStatus);
        Assert.Equal(EstadoReserva.Confirmed, log.ToStatus);
        Assert.Equal("admin-test", log.ByUserId);
        Assert.False(string.IsNullOrWhiteSpace(log.Reason));

        // DTO: vuelve Confirmed y el front la ve "En corrección" (Confirmed + StartDate null).
        Assert.Equal(EstadoReserva.Confirmed, dto.Status);
        Assert.True(dto.IsUnderCorrection);
    }

    // ============================= (2) Coherencia con el job nocturno =============================

    [Fact]
    public async Task AfterCorrection_NightlyJob_DoesNotRePromoteToTraveling()
    {
        await using var context = CreateContext();
        var reserva = await SeedTravelingAsync(context);

        // Sacamos de viaje -> Confirmed con StartDate null.
        await CreateService(context).CorrectTravelingEntryAsync(
            reserva.PublicId.ToString(), ValidRequest(), "admin-test", "Admin Test", CancellationToken.None);

        // El job corre esa misma noche: NO debe volver a promover (StartDate null la saca de candidatos).
        var promoted = await CreateJob(context).AutoTransitionConfirmedToTravelingAsync(CancellationToken.None);

        Assert.Equal(0, promoted);
        var stored = await context.Reservas.AsNoTracking().SingleAsync(r => r.Id == reserva.Id);
        Assert.Equal(EstadoReserva.Confirmed, stored.Status);
    }

    [Fact]
    public async Task NightlyJob_NormalConfirmed_WithStartDateTodayAndPaid_StillPromotes()
    {
        // Control: el flujo normal NO se rompio. Una Confirmed comun, saldada, con StartDate <= hoy y un
        // servicio cargado, SI se promueve a Traveling.
        await using var context = CreateContext();
        context.Reservas.Add(new Reserva
        {
            Id = 2, NumeroReserva = "R-36-2", Name = "Normal", Status = EstadoReserva.Confirmed,
            StartDate = DateTime.UtcNow.Date, Balance = 0m
        });
        context.HotelBookings.Add(new HotelBooking
        {
            Id = 20, ReservaId = 2, HotelName = "H", Status = "Confirmado", ConfirmedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var promoted = await CreateJob(context).AutoTransitionConfirmedToTravelingAsync(CancellationToken.None);

        Assert.Equal(1, promoted);
        var stored = await context.Reservas.AsNoTracking().SingleAsync(r => r.Id == 2);
        Assert.Equal(EstadoReserva.Traveling, stored.Status);
    }

    // ============================= (3) Bloqueo fiscal: factura con CAE vivo =============================

    [Fact]
    public async Task CorrectTravelingEntry_WithLiveCae_NotNc_Throws_NoStateChange_NotEvenAdmin()
    {
        await using var context = CreateContext();
        var reserva = await SeedTravelingAsync(context);
        // Factura B (tipo 6, NO es NC) con CAE vivo y sin anular -> bloquea (no bypasseable ni por Admin).
        context.Invoices.Add(new Invoice
        {
            Id = 100, ReservaId = 1, TipoComprobante = 6, CAE = "70000000000001",
            AnnulmentStatus = AnnulmentStatus.None
        });
        await context.SaveChangesAsync();

        var sut = CreateService(context);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.CorrectTravelingEntryAsync(
                reserva.PublicId.ToString(), ValidRequest(), "admin-test", "Admin Test", CancellationToken.None));

        var stored = await context.Reservas.AsNoTracking().SingleAsync(r => r.Id == reserva.Id);
        Assert.Equal(EstadoReserva.Traveling, stored.Status);   // no cambio de estado
        Assert.NotNull(stored.StartDate);                        // StartDate intacta
        Assert.Empty(context.ReservaStatusChangeLogs);           // no se logueo nada
    }

    [Fact]
    public async Task CorrectTravelingEntry_WithOnlyCreditNote_IsNotBlockedByCae()
    {
        // El criterio EXCLUYE las NC: una reserva cuya unica huella fiscal es una NC (CAE) NO se bloquea por
        // eso (la NC resta, no mantiene viva la reserva). Sacar de viaje debe funcionar.
        await using var context = CreateContext();
        var reserva = await SeedTravelingAsync(context);
        // NC tipo 8 (B) con CAE: es nota de credito -> NO cuenta como factura viva.
        context.Invoices.Add(new Invoice
        {
            Id = 101, ReservaId = 1, TipoComprobante = 8, CAE = "70000000000002",
            AnnulmentStatus = AnnulmentStatus.None
        });
        await context.SaveChangesAsync();

        var sut = CreateService(context);
        var dto = await sut.CorrectTravelingEntryAsync(
            reserva.PublicId.ToString(), ValidRequest(), "admin-test", "Admin Test", CancellationToken.None);

        Assert.Equal(EstadoReserva.Confirmed, dto.Status);
        var stored = await context.Reservas.AsNoTracking().SingleAsync(r => r.Id == reserva.Id);
        Assert.Equal(EstadoReserva.Confirmed, stored.Status);
    }

    // ============================= (4) Bloqueo por voucher vivo =============================

    [Fact]
    public async Task CorrectTravelingEntry_WithLiveVoucher_Throws_NoStateChange()
    {
        await using var context = CreateContext();
        var reserva = await SeedTravelingAsync(context);
        context.Vouchers.Add(new Voucher { Id = 200, ReservaId = 1, Status = VoucherStatuses.Issued });
        await context.SaveChangesAsync();

        var sut = CreateService(context);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.CorrectTravelingEntryAsync(
                reserva.PublicId.ToString(), ValidRequest(), "admin-test", "Admin Test", CancellationToken.None));

        var stored = await context.Reservas.AsNoTracking().SingleAsync(r => r.Id == reserva.Id);
        Assert.Equal(EstadoReserva.Traveling, stored.Status);
    }

    // ============================= (5) Estado != Traveling: 409 idempotente =============================

    [Theory]
    [InlineData(EstadoReserva.Confirmed)]
    [InlineData(EstadoReserva.Closed)]
    [InlineData(EstadoReserva.InManagement)]
    public async Task CorrectTravelingEntry_WhenNotTraveling_Throws(string status)
    {
        await using var context = CreateContext();
        context.Reservas.Add(new Reserva
        {
            Id = 3, NumeroReserva = "R-36-3", Name = "No en viaje", Status = status,
            StartDate = DateTime.UtcNow.Date
        });
        await context.SaveChangesAsync();
        var publicId = (await context.Reservas.AsNoTracking().SingleAsync(r => r.Id == 3)).PublicId;

        var sut = CreateService(context);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.CorrectTravelingEntryAsync(
                publicId.ToString(), ValidRequest(), "admin-test", "Admin Test", CancellationToken.None));
    }

    // ============================= (6) Motivo obligatorio (>= 10 chars), sin excepcion =============================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("corto")]      // 5 chars
    [InlineData("nueve cha")]  // 9 chars
    public async Task CorrectTravelingEntry_ReasonTooShort_ThrowsArgument_NoStateChange(string? reason)
    {
        await using var context = CreateContext();
        var reserva = await SeedTravelingAsync(context);

        var sut = CreateService(context);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            sut.CorrectTravelingEntryAsync(
                reserva.PublicId.ToString(), new CorrectTravelingEntryRequest(reason),
                "admin-test", "Admin Test", CancellationToken.None));

        var stored = await context.Reservas.AsNoTracking().SingleAsync(r => r.Id == reserva.Id);
        Assert.Equal(EstadoReserva.Traveling, stored.Status); // motivo invalido no cambia nada
    }

    // ============================= (7) Capacidad pura =============================

    [Fact]
    public void Capability_CanCorrectTravelingEntry_AllowedOnlyInTravelingWithoutCaeOrVoucher()
    {
        // Traveling sin CAE ni voucher -> allowed.
        var ok = ReservaCapabilityPolicy.For(new ReservaCapabilityContext(
            EstadoReserva.Traveling, Balance: 0m, HasLiveCae: false, HasLiveVoucher: false,
            HasLiveEditAuth: false, HasAnyPayment: true)); // cobros NO bloquean
        Assert.True(ok.CanCorrectTravelingEntry.Allowed);

        // Traveling con CAE -> bloqueado por factura.
        var withCae = ReservaCapabilityPolicy.For(new ReservaCapabilityContext(
            EstadoReserva.Traveling, 0m, HasLiveCae: true, HasLiveVoucher: false, false, false));
        Assert.False(withCae.CanCorrectTravelingEntry.Allowed);
        Assert.Equal(ReservaCapabilityPolicy.CorrectTravelingBlockedByCaeReason, withCae.CanCorrectTravelingEntry.Reason);

        // Traveling con voucher -> bloqueado por voucher.
        var withVoucher = ReservaCapabilityPolicy.For(new ReservaCapabilityContext(
            EstadoReserva.Traveling, 0m, HasLiveCae: false, HasLiveVoucher: true, false, false));
        Assert.False(withVoucher.CanCorrectTravelingEntry.Allowed);
        Assert.Equal(ReservaCapabilityPolicy.CorrectTravelingBlockedByVoucherReason, withVoucher.CanCorrectTravelingEntry.Reason);

        // Otro estado (Confirmed) -> no aplica.
        var confirmed = ReservaCapabilityPolicy.For(new ReservaCapabilityContext(
            EstadoReserva.Confirmed, 0m, false, false, false, false));
        Assert.False(confirmed.CanCorrectTravelingEntry.Allowed);
        Assert.Equal(ReservaCapabilityPolicy.CorrectTravelingOnlyFromTravelingReason, confirmed.CanCorrectTravelingEntry.Reason);
    }

    // ============================= (8) Cruce capacidad <-> endpoint =============================

    [Fact]
    public async Task CrossCheck_WhenCapabilityAllowed_EndpointDoesNotReject()
    {
        // Si la capacidad dice allowed (Traveling, sin CAE/voucher), el service NO debe rechazar por
        // estado/CAE/voucher: la capacidad y el guard no pueden divergir.
        await using var context = CreateContext();
        var reserva = await SeedTravelingAsync(context);
        var sut = CreateService(context);

        // Confirmamos que la capacidad esta allowed para este estado real.
        var caps = ReservaCapabilityPolicy.For(new ReservaCapabilityContext(
            EstadoReserva.Traveling, 0m, HasLiveCae: false, HasLiveVoucher: false, false, true));
        Assert.True(caps.CanCorrectTravelingEntry.Allowed);

        // El endpoint pasa (no tira por estado/CAE/voucher; el motivo valido cubre el unico gate restante).
        var dto = await sut.CorrectTravelingEntryAsync(
            reserva.PublicId.ToString(), ValidRequest(), "admin-test", "Admin Test", CancellationToken.None);
        Assert.Equal(EstadoReserva.Confirmed, dto.Status);
    }
}
