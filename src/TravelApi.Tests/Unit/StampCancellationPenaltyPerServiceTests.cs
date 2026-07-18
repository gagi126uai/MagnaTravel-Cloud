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
using TravelApi.Infrastructure.Services;
using TravelApi.Application.Constants;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-048 T4 (review backend 2026-07-17, B1+B2): cobertura de
/// <c>ReservaService.StampCancellationPenaltyPerServiceAsync</c> (etiqueta "Con multa"/"Multa cobrada" por
/// servicio). Antes de esta tanda el metodo no tenia NINGUN test backend.
///
/// <para><b>El caso que atrapa el bug B1</b> (mismatch de alcance): una reserva con DOS cancelaciones no
/// abortadas del MISMO operador — la vieja con un servicio cuya multa quedo confirmada pero NUNCA se
/// verifico su cobro, la nueva (la que alimenta <c>OperatorPenaltySituations</c>) con su multa YA cobrada.
/// Antes del fix, el servicio de la cancelacion VIEJA heredaba "Collected" de la NUEVA. Ver
/// <see cref="MultiCancellation_OlderLineNeverInheritsNewerCancellationCollectedStatus"/>.</para>
/// </summary>
public class StampCancellationPenaltyPerServiceTests
{
    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    /// <summary>
    /// Arma el ReservaService con un <see cref="IBookingCancellationService"/> MOCKEADO: en vez de
    /// reconstruir toda la maquinaria real de <c>BookingCancellationService.GetOperatorPenaltySituationsAsync</c>
    /// (que arma el read-model desde CERO cargas de datos), controlamos directamente que
    /// <c>dto.OperatorPenaltySituations</c> devuelve — es la MISMA tecnica que usa el resto de la suite para
    /// aislar `ReservaService` de sus colaboradores.
    /// </summary>
    private static ReservaService CreateService(
        AppDbContext context, IReadOnlyList<OperatorPenaltySituationDto>? situations = null)
    {
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OperationalFinanceSettings());

        const string userId = "admin-test";
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId),
            new(ClaimTypes.Role, "Admin"), // Admin: ve costos y puede clasificar la multa, sin masking.
        };
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
        };

        var resolver = new Mock<IUserPermissionResolver>();
        IReadOnlySet<string> perms = new HashSet<string> { Permissions.CobranzasSeeCost };
        resolver.Setup(r => r.GetPermissionsAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(perms);

        Mock<IBookingCancellationService>? cancellationServiceMock = null;
        if (situations is not null)
        {
            cancellationServiceMock = new Mock<IBookingCancellationService>();
            cancellationServiceMock
                .Setup(s => s.GetOperatorPenaltySituationsAsync(
                    It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
                .ReturnsAsync(situations);
        }

        return new ReservaService(
            context,
            new MapperConfiguration(c => c.AddProfile<MappingProfile>()).CreateMapper(),
            settings.Object,
            BuildUserManager(),
            NullLogger<ReservaService>.Instance,
            resolver.Object,
            accessor,
            autoStateService: null,
            auditService: null,
            cancellationService: cancellationServiceMock?.Object);
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

    /// <summary>Siembra una reserva con UN hotel ANULADO (workflowStatus "Cancelado") de un operador dado.</summary>
    private static async Task<(Reserva Reserva, Supplier Supplier, HotelBooking Hotel)> SeedReservaConHotelAnuladoAsync(
        AppDbContext ctx, string numeroReserva = "R-T4-B1")
    {
        var customer = new Customer { FullName = "Cliente T4", IsActive = true };
        var supplier = new Supplier { Name = "Operador T4", IsActive = true };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = numeroReserva,
            Name = "Reserva con servicio anulado",
            PayerId = customer.Id,
            Status = EstadoReserva.Confirmed, // anulacion PARCIAL: la reserva sigue viva, un servicio anulado.
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var hotel = new HotelBooking
        {
            ReservaId = reserva.Id,
            SupplierId = supplier.Id,
            Status = "Cancelado", // WorkflowStatusHelper.MapGenericStatus -> Cancelado (ServiceResolutionRules.IsCancelled).
            NetCost = 500m,
            SalePrice = 1000m,
            Currency = "ARS",
        };
        ctx.HotelBookings.Add(hotel);
        await ctx.SaveChangesAsync();

        return (reserva, supplier, hotel);
    }

    /// <summary>
    /// Siembra una cancelacion (BookingCancellation) no abortada, con UNA linea que referencia el servicio
    /// dado. <paramref name="draftedAt"/> controla el orden DraftedAt DESC que decide cual cancelacion es la
    /// "correlacionada" (la que <c>StampCancellationPenaltyPerServiceAsync</c> cruza con
    /// <c>OperatorPenaltySituations</c>).
    /// </summary>
    private static async Task<BookingCancellationLine> SeedCancellationWithLineAsync(
        AppDbContext ctx, Reserva reserva, Supplier supplier, int serviceId, DateTime draftedAt,
        PenaltyStatus penaltyStatus, decimal? penaltyAmount,
        CancellableServiceTable serviceTable = CancellableServiceTable.Hotel)
    {
        var bc = new BookingCancellation
        {
            ReservaId = reserva.Id,
            CustomerId = reserva.PayerId!.Value,
            SupplierId = supplier.Id,
            Status = BookingCancellationStatus.Closed,
            Reason = "Cancelacion de prueba T4",
            DraftedAt = draftedAt,
            DraftedByUserId = "vendedor-1",
            FiscalSnapshot = new FiscalSnapshot
            {
                Source = ExchangeRateSource.Manual, ExchangeRateAtOriginalInvoice = 1m,
                CurrencyAtEvent = "ARS", FetchedAt = draftedAt,
            },
        };
        var line = new BookingCancellationLine
        {
            SupplierId = supplier.Id,
            ServiceTable = serviceTable,
            ServiceId = serviceId,
            Scope = BookingCancellationLineScope.Partial,
            Currency = "ARS",
            LineSaleAmount = 1000m,
            PenaltyStatus = penaltyStatus,
            PenaltyAmount = penaltyAmount,
        };
        bc.Lines.Add(line);
        ctx.BookingCancellations.Add(bc);
        await ctx.SaveChangesAsync();
        return line;
    }

    private static OperatorPenaltySituationDto Situacion(
        Guid supplierPublicId, string state, bool isFullyCollected)
        => new()
        {
            State = state,
            SupplierPublicId = supplierPublicId,
            IsFullyCollected = isFullyCollected,
        };

    // ─────────────────────────────────────────────────────────────────────────────────
    // Happy path
    // ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ConfirmedPenalty_OperatorFullyCollected_StampsCollected()
    {
        await using var ctx = CreateContext();
        var (reserva, supplier, hotel) = await SeedReservaConHotelAnuladoAsync(ctx);
        await SeedCancellationWithLineAsync(
            ctx, reserva, supplier, hotel.Id, DateTime.UtcNow,
            PenaltyStatus.Confirmed, penaltyAmount: 300m);

        var service = CreateService(ctx, situations: new[]
        {
            Situacion(supplier.PublicId, "Done", isFullyCollected: true),
        });

        var dto = await service.GetReservaByIdAsync(reserva.PublicId.ToString(), CancellationToken.None);

        Assert.Equal("Collected", Assert.Single(dto.HotelBookings).CancellationPenaltyState);
    }

    [Fact]
    public async Task ConfirmedPenalty_OperatorNotYetCollected_StampsPending()
    {
        await using var ctx = CreateContext();
        var (reserva, supplier, hotel) = await SeedReservaConHotelAnuladoAsync(ctx);
        await SeedCancellationWithLineAsync(
            ctx, reserva, supplier, hotel.Id, DateTime.UtcNow,
            PenaltyStatus.Confirmed, penaltyAmount: 300m);

        var service = CreateService(ctx, situations: new[]
        {
            Situacion(supplier.PublicId, "Done", isFullyCollected: false),
        });

        var dto = await service.GetReservaByIdAsync(reserva.PublicId.ToString(), CancellationToken.None);

        Assert.Equal("Pending", Assert.Single(dto.HotelBookings).CancellationPenaltyState);
    }

    [Fact]
    public async Task ServiceWithoutAnyCancellationLine_LeavesCancellationPenaltyStateNull()
    {
        await using var ctx = CreateContext();
        var (reserva, _, _) = await SeedReservaConHotelAnuladoAsync(ctx);
        // Sin BookingCancellation en absoluto — servicio anulado pero SIN multa en juego.

        var service = CreateService(ctx, situations: Array.Empty<OperatorPenaltySituationDto>());

        var dto = await service.GetReservaByIdAsync(reserva.PublicId.ToString(), CancellationToken.None);

        Assert.Null(Assert.Single(dto.HotelBookings).CancellationPenaltyState);
    }

    // ─────────────────────────────────────────────────────────────────────────────────
    // B1 — el bug real: dos cancelaciones del MISMO operador, una vieja y una nueva
    // ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task MultiCancellation_OlderLineNeverInheritsNewerCancellationCollectedStatus()
    {
        await using var ctx = CreateContext();
        var customer = new Customer { FullName = "Cliente T4 multi-BC", IsActive = true };
        var supplier = new Supplier { Name = "Operador con 2 anulaciones", IsActive = true };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = "R-T4-MULTIBC",
            Name = "Reserva con 2 cancelaciones del mismo operador",
            PayerId = customer.Id,
            Status = EstadoReserva.Confirmed,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        // Servicio A: anulado hace RATO, en una cancelacion VIEJA (DraftedAt mas antiguo).
        var hotelViejo = new HotelBooking
        {
            ReservaId = reserva.Id, SupplierId = supplier.Id, Status = "Cancelado",
            NetCost = 300m, SalePrice = 600m, Currency = "ARS",
        };
        // Servicio B: anulado DESPUES, en una cancelacion NUEVA (la que alimenta OperatorPenaltySituations).
        var hotelNuevo = new HotelBooking
        {
            ReservaId = reserva.Id, SupplierId = supplier.Id, Status = "Cancelado",
            NetCost = 400m, SalePrice = 800m, Currency = "ARS",
        };
        ctx.HotelBookings.AddRange(hotelViejo, hotelNuevo);
        await ctx.SaveChangesAsync();

        var haceUnMes = DateTime.UtcNow.AddDays(-30);
        var hoy = DateTime.UtcNow;

        // Cancelacion VIEJA: multa confirmada, pero (en la realidad) su ND todavia no se cobro.
        await SeedCancellationWithLineAsync(
            ctx, reserva, supplier, hotelViejo.Id, haceUnMes,
            PenaltyStatus.Confirmed, penaltyAmount: 300m);

        // Cancelacion NUEVA (la mas reciente -> la correlacionada): multa confirmada Y YA COBRADA.
        await SeedCancellationWithLineAsync(
            ctx, reserva, supplier, hotelNuevo.Id, hoy,
            PenaltyStatus.Confirmed, penaltyAmount: 400m);

        // El mock refleja SOLO la cancelacion mas reciente (igual que la implementacion real de
        // BookingCancellationService.GetOperatorPenaltySituationsAsync, que arma el read-model sobre UNA
        // sola cancelacion): "cobrada por completo" = true, porque la ND de la anulacion NUEVA ya se pago.
        var service = CreateService(ctx, situations: new[]
        {
            Situacion(supplier.PublicId, "Done", isFullyCollected: true),
        });

        var dto = await service.GetReservaByIdAsync(reserva.PublicId.ToString(), CancellationToken.None);

        var filaVieja = dto.HotelBookings.Single(h => h.PublicId == hotelViejo.PublicId);
        var filaNueva = dto.HotelBookings.Single(h => h.PublicId == hotelNuevo.PublicId);

        // EL BUG (antes del fix): filaVieja.CancellationPenaltyState daba "Collected" (heredado de la
        // cancelacion nueva) siendo MENTIRA — su propia ND nunca se verifico como cobrada.
        Assert.Equal("Pending", filaVieja.CancellationPenaltyState);
        // La fila de la cancelacion CORRELACIONADA (la nueva) si refleja el cobro real.
        Assert.Equal("Collected", filaNueva.CancellationPenaltyState);
    }

    // ─────────────────────────────────────────────────────────────────────────────────
    // DC1 — multa EN TRAMITE (Estimated, sin confirmar) tambien es "Con multa" (Pending)
    // ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PendingDecision_WithoutConfirmedAmountYet_StampsPending()
    {
        await using var ctx = CreateContext();
        var (reserva, supplier, hotel) = await SeedReservaConHotelAnuladoAsync(ctx);
        // La linea nace en Estimated (default) SIN monto todavia — el reparto por linea recien pasa al
        // confirmar la multa (ver XML-doc del metodo bajo prueba).
        await SeedCancellationWithLineAsync(
            ctx, reserva, supplier, hotel.Id, DateTime.UtcNow,
            PenaltyStatus.Estimated, penaltyAmount: null);

        var service = CreateService(ctx, situations: new[]
        {
            Situacion(supplier.PublicId, "PendingDecision", isFullyCollected: false),
        });

        var dto = await service.GetReservaByIdAsync(reserva.PublicId.ToString(), CancellationToken.None);

        Assert.Equal("Pending", Assert.Single(dto.HotelBookings).CancellationPenaltyState);
    }

    [Fact]
    public async Task NoPendingDecisionAndNoConfirmedPenalty_LeavesCancellationPenaltyStateNull()
    {
        // Multa Estimated, pero el operador NO figura con "PendingDecision" en las situaciones (por ej.
        // porque el gate compartido — flag OFF, o falta la NC con CAE — todavia no habilita la pregunta).
        // Sin confirmar Y sin decision pendiente reconocida: no hay nada que informar todavia.
        await using var ctx = CreateContext();
        var (reserva, supplier, hotel) = await SeedReservaConHotelAnuladoAsync(ctx);
        await SeedCancellationWithLineAsync(
            ctx, reserva, supplier, hotel.Id, DateTime.UtcNow,
            PenaltyStatus.Estimated, penaltyAmount: null);

        var service = CreateService(ctx, situations: Array.Empty<OperatorPenaltySituationDto>());

        var dto = await service.GetReservaByIdAsync(reserva.PublicId.ToString(), CancellationToken.None);

        Assert.Null(Assert.Single(dto.HotelBookings).CancellationPenaltyState);
    }

    // ─────────────────────────────────────────────────────────────────────────────────
    // Degradacion silenciosa — sin IBookingCancellationService inyectado (tests legacy / falla del endpoint)
    // ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NoCancellationServiceInjected_ConfirmedPenaltyDefaultsToPending_NeverCollected()
    {
        await using var ctx = CreateContext();
        var (reserva, supplier, hotel) = await SeedReservaConHotelAnuladoAsync(ctx);
        await SeedCancellationWithLineAsync(
            ctx, reserva, supplier, hotel.Id, DateTime.UtcNow,
            PenaltyStatus.Confirmed, penaltyAmount: 300m);

        // situations: null -> CreateService NO inyecta IBookingCancellationService (mismo caso que un test
        // unitario viejo, o el service real ausente): dto.OperatorPenaltySituations queda vacio.
        var service = CreateService(ctx, situations: null);

        var dto = await service.GetReservaByIdAsync(reserva.PublicId.ToString(), CancellationToken.None);

        // Multa real y confirmada -> SI se informa "Con multa", pero jamas "Collected" sin poder
        // verificarlo (degradacion segura, spec: "nunca Collected sin correlacion exacta").
        Assert.Equal("Pending", Assert.Single(dto.HotelBookings).CancellationPenaltyState);
    }
}
