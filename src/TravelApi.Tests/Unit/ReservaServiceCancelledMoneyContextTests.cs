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
/// Verifica el comportamiento del detalle de reserva (GetReservaByIdAsync -> ApplyEconomicFlags) para
/// reservas ANULADAS: NO deben mostrar "vencida con deuda" por un saldo congelado, y sí deben exponer el
/// contexto de plata real (saldo a favor pendiente / multa cobrable / dato inconsistente).
/// Bug fijado: auditoría 2026-07-04.
/// </summary>
public class ReservaServiceCancelledMoneyContextTests
{
    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
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
            new(ClaimTypes.Role, "Admin"), // Admin: ve costos, sin masking
        };
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
        };

        var resolver = new Mock<IUserPermissionResolver>();
        IReadOnlySet<string> perms = new HashSet<string> { Permissions.CobranzasSeeCost };
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

    // Reserva anulada, viaje TERMINADO (EndDate en el pasado), con el Balance pedido.
    private static async Task<Reserva> SeedCancelledAsync(AppDbContext context, decimal balance)
    {
        var reserva = new Reserva
        {
            Id = 1,
            NumeroReserva = "F-2026-0001",
            Name = "Reserva anulada",
            Status = EstadoReserva.Cancelled,
            StartDate = new DateTime(2026, 05, 01, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2026, 06, 01, 0, 0, 0, DateTimeKind.Utc), // ya terminó
            Balance = balance,
        };
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();
        return reserva;
    }

    // Agrega una cancelación con (o sin) Nota de Débito de multa viva sobre la reserva.
    private static Task AddCancellationAsync(AppDbContext context, int reservaId, bool withLiveDebitNote)
        => AddCancellationRawAsync(
            context, reservaId,
            // Con multa viva: penalidad confirmada + ND emitida. Sin multa: default conservador (NotApplicable).
            penalty: withLiveDebitNote ? PenaltyStatus.Confirmed : PenaltyStatus.Estimated,
            debitNote: withLiveDebitNote ? DebitNoteStatus.Issued : DebitNoteStatus.NotApplicable);

    // Variante explícita: permite fijar el estado exacto de la penalidad y de la ND (para casos de borde).
    private static async Task AddCancellationRawAsync(
        AppDbContext context, int reservaId, PenaltyStatus penalty, DebitNoteStatus debitNote)
    {
        var bc = new BookingCancellation
        {
            ReservaId = reservaId,
            Reason = "Cliente anuló el viaje",
            DraftedByUserId = "vendedor-1",
            ConfirmedWithClientAt = DateTime.UtcNow.AddDays(-20),
            PenaltyStatus = penalty,
            DebitNoteStatus = debitNote,
        };
        context.BookingCancellations.Add(bc);
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task CancelledWithFrozenPositiveBalance_DoesNotShowOverdueDebt()
    {
        await using var context = CreateContext();
        var reserva = await SeedCancelledAsync(context, balance: 1000m);
        await AddCancellationAsync(context, reserva.Id, withLiveDebitNote: true);
        var service = CreateService(context);

        var dto = await service.GetReservaByIdAsync(reserva.PublicId.ToString(), CancellationToken.None);

        // El bug: una anulada con deuda congelada mostraba el chip rojo "Vencida con deuda".
        Assert.False(dto.HasOverdueDebt);
    }

    [Fact]
    public async Task CancelledWithLiveDebitNote_IsPenaltyReceivable()
    {
        await using var context = CreateContext();
        var reserva = await SeedCancelledAsync(context, balance: 1000m);
        await AddCancellationAsync(context, reserva.Id, withLiveDebitNote: true);
        var service = CreateService(context);

        var dto = await service.GetReservaByIdAsync(reserva.PublicId.ToString(), CancellationToken.None);

        Assert.Equal("MultaPorCobrar", dto.CancelledMoneyContext);
    }

    [Fact]
    public async Task CancelledWithPositiveBalance_NoDebitNote_IsInconsistent()
    {
        await using var context = CreateContext();
        var reserva = await SeedCancelledAsync(context, balance: 1000m);
        await AddCancellationAsync(context, reserva.Id, withLiveDebitNote: false);
        var service = CreateService(context);

        var dto = await service.GetReservaByIdAsync(reserva.PublicId.ToString(), CancellationToken.None);

        Assert.False(dto.HasOverdueDebt);
        Assert.Equal("Inconsistente", dto.CancelledMoneyContext);
    }

    [Fact]
    public async Task CancelledWithNegativeBalance_IsClientCreditPending()
    {
        await using var context = CreateContext();
        var reserva = await SeedCancelledAsync(context, balance: -500m);
        // Saldo a favor: no importa si hay ND o no, gana el crédito del cliente.
        await AddCancellationAsync(context, reserva.Id, withLiveDebitNote: false);
        var service = CreateService(context);

        var dto = await service.GetReservaByIdAsync(reserva.PublicId.ToString(), CancellationToken.None);

        Assert.Equal("SaldoAFavorPendiente", dto.CancelledMoneyContext);
    }

    [Fact]
    public async Task ConfirmedReservation_HasNullCancelledMoneyContext()
    {
        // Una reserva viva (no anulada) NO expone contexto de plata de anulación.
        await using var context = CreateContext();
        var reserva = new Reserva
        {
            Id = 1,
            NumeroReserva = "F-2026-0009",
            Name = "Reserva viva",
            Status = EstadoReserva.Confirmed,
            EndDate = new DateTime(2026, 06, 01, 0, 0, 0, DateTimeKind.Utc),
            Balance = 1000m,
        };
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();
        var service = CreateService(context);

        var dto = await service.GetReservaByIdAsync(reserva.PublicId.ToString(), CancellationToken.None);

        Assert.Null(dto.CancelledMoneyContext);
        // Y como es cobrable + terminó + con deuda: sí es "vencida con deuda".
        Assert.True(dto.HasOverdueDebt);
    }

    [Fact]
    public async Task DeferredPenalty_ConfirmedButDebitNoteStillPending_IsPenaltyReceivable()
    {
        // ADR-014 (confirmación diferida de multa): el operador confirmó la multa pero la ND todavía no obtuvo
        // CAE (Pending). Con el predicado AMPLIO, ese saldo positivo se muestra como "multa por cobrar" y NO
        // como dato inconsistente durante la ventana de emisión.
        await using var context = CreateContext();
        var reserva = await SeedCancelledAsync(context, balance: 800m);
        await AddCancellationRawAsync(
            context, reserva.Id,
            penalty: PenaltyStatus.Confirmed,
            debitNote: DebitNoteStatus.Pending);
        var service = CreateService(context);

        var dto = await service.GetReservaByIdAsync(reserva.PublicId.ToString(), CancellationToken.None);

        Assert.False(dto.HasOverdueDebt);
        Assert.Equal("MultaPorCobrar", dto.CancelledMoneyContext);
    }

    [Fact]
    public async Task WaivedPenalty_WithPositiveBalanceAndNoDebitNote_IsInconsistent()
    {
        // El operador NO cobró multa (Waived) y no hay ninguna ND, pero quedó un saldo positivo: no hay nada que
        // lo justifique -> dato roto (lo detectará el vigía).
        await using var context = CreateContext();
        var reserva = await SeedCancelledAsync(context, balance: 800m);
        await AddCancellationRawAsync(
            context, reserva.Id,
            penalty: PenaltyStatus.Waived,
            debitNote: DebitNoteStatus.NotApplicable);
        var service = CreateService(context);

        var dto = await service.GetReservaByIdAsync(reserva.PublicId.ToString(), CancellationToken.None);

        Assert.False(dto.HasOverdueDebt);
        Assert.Equal("Inconsistente", dto.CancelledMoneyContext);
    }

    [Fact]
    public async Task List_MixedPage_OnlyCancelledRowsGetContext()
    {
        // Página mixta (vista "closed" trae Finalizadas Y Anuladas): una fila ANULADA (con multa viva) y una
        // fila FINALIZADA con deuda. Solo la anulada recibe contexto de plata; la Finalizada queda en null (el
        // chip de anulación no aplica; esa usa el chip de deuda normal).
        await using var context = CreateContext();

        var cancelled = new Reserva
        {
            Id = 1, NumeroReserva = "F-2026-0100", Name = "Anulada con multa",
            Status = EstadoReserva.Cancelled, ResponsibleUserId = "vendedor-1", Balance = 500m,
            EndDate = new DateTime(2026, 06, 01, 0, 0, 0, DateTimeKind.Utc),
        };
        var closed = new Reserva
        {
            Id = 2, NumeroReserva = "F-2026-0101", Name = "Finalizada con deuda",
            Status = EstadoReserva.Closed, ResponsibleUserId = "vendedor-1", Balance = 500m,
            EndDate = new DateTime(2026, 06, 01, 0, 0, 0, DateTimeKind.Utc),
        };
        context.Reservas.AddRange(cancelled, closed);
        await context.SaveChangesAsync();
        await AddCancellationRawAsync(context, cancelled.Id, PenaltyStatus.Confirmed, DebitNoteStatus.Issued);

        var service = CreateService(context);

        var page = await service.GetReservasAsync(
            new ReservaListQuery { View = "closed" }, CancellationToken.None);

        var cancelledRow = page.Items.Single(i => i.NumeroReserva == "F-2026-0100");
        var closedRow = page.Items.Single(i => i.NumeroReserva == "F-2026-0101");
        Assert.Equal("MultaPorCobrar", cancelledRow.CancelledMoneyContext);
        Assert.Null(closedRow.CancelledMoneyContext);
    }

    [Fact]
    public async Task List_NoCancelledRows_LeavesContextNull()
    {
        // Sin filas anuladas en la página, el helper corta temprano (no consulta cancelaciones) y ninguna fila
        // recibe contexto.
        await using var context = CreateContext();
        context.Reservas.AddRange(
            new Reserva
            {
                Id = 1, NumeroReserva = "F-2026-0200", Name = "Viva A",
                Status = EstadoReserva.Confirmed, ResponsibleUserId = "vendedor-1", Balance = 100m,
            },
            new Reserva
            {
                Id = 2, NumeroReserva = "F-2026-0201", Name = "Viva B",
                Status = EstadoReserva.InManagement, ResponsibleUserId = "vendedor-1", Balance = 0m,
            });
        await context.SaveChangesAsync();

        var service = CreateService(context);

        var page = await service.GetReservasAsync(new ReservaListQuery(), CancellationToken.None);

        Assert.All(page.Items, row => Assert.Null(row.CancelledMoneyContext));
    }
}
