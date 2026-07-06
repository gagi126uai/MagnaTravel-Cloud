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
            // Con multa viva: penalidad confirmada + monto positivo + ND en la ventana de emisión diferida
            // (Pending). Es la rama 2 GENUINA de LiveDebitNotePredicate; NO usamos Issued acá porque una ND
            // Issued solo es viva si además tiene su factura vinculada y no anulada (rama 1) — eso se prueba en
            // los tests dedicados con SeedDebitNoteInvoiceAsync. Sin multa: default conservador (Estimated /
            // NotApplicable).
            penalty: withLiveDebitNote ? PenaltyStatus.Confirmed : PenaltyStatus.Estimated,
            debitNote: withLiveDebitNote ? DebitNoteStatus.Pending : DebitNoteStatus.NotApplicable,
            penaltyAmount: withLiveDebitNote ? 500m : (decimal?)null);

    // Variante explícita: permite fijar el estado exacto de la penalidad, de la ND, el monto congelado y (opcional)
    // la factura de la ND vinculada (para los casos de borde del predicado de "multa viva").
    private static async Task AddCancellationRawAsync(
        AppDbContext context, int reservaId, PenaltyStatus penalty, DebitNoteStatus debitNote,
        decimal? penaltyAmount = null, string? penaltyCurrencyAtEvent = null, int? debitNoteInvoiceId = null)
    {
        var bc = new BookingCancellation
        {
            ReservaId = reservaId,
            Reason = "Cliente anuló el viaje",
            DraftedByUserId = "vendedor-1",
            ConfirmedWithClientAt = DateTime.UtcNow.AddDays(-20),
            PenaltyStatus = penalty,
            DebitNoteStatus = debitNote,
            PenaltyAmountAtEvent = penaltyAmount,
            PenaltyCurrencyAtEvent = penaltyCurrencyAtEvent,
            DebitNoteInvoiceId = debitNoteInvoiceId,
        };
        context.BookingCancellations.Add(bc);
        await context.SaveChangesAsync();
    }

    // Siembra una factura de Nota de Débito con el estado de anulación pedido y devuelve su Id (para vincularla
    // como DebitNoteInvoiceId y probar la guarda de "factura anulada = fuera del cartel").
    private static async Task<int> SeedDebitNoteInvoiceAsync(AppDbContext context, AnnulmentStatus annulmentStatus)
    {
        var nd = new Invoice
        {
            TipoComprobante = 12, // ND C
            PuntoDeVenta = 1,
            NumeroComprobante = 500,
            Resultado = "A",
            CAE = "77777777",
            AnnulmentStatus = annulmentStatus,
        };
        context.Invoices.Add(nd);
        await context.SaveChangesAsync();
        return nd.Id;
    }

    // Siembra una línea de plata POR MONEDA materializada (ReservaMoneyByCurrency) con el saldo pedido. Es la que
    // lee el listado (FillPorMonedaForListAsync) para netear la multa contra lo ya cobrado en esa moneda.
    private static async Task SeedMoneyByCurrencyAsync(
        AppDbContext context, int reservaId, string currency, decimal balance)
    {
        context.ReservaMoneyByCurrency.Add(new ReservaMoneyByCurrency
        {
            ReservaId = reservaId,
            Currency = currency,
            ConfirmedSale = balance, // coherencia mínima; el neteo solo lee Balance.
            Balance = balance,
        });
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
            debitNote: DebitNoteStatus.Pending,
            penaltyAmount: 800m); // multa confirmada con monto: rama de emisión diferida = viva.
        var service = CreateService(context);

        var dto = await service.GetReservaByIdAsync(reserva.PublicId.ToString(), CancellationToken.None);

        Assert.False(dto.HasOverdueDebt);
        Assert.Equal("MultaPorCobrar", dto.CancelledMoneyContext);
    }

    [Fact]
    public async Task ConfirmedPenalty_WithFailedDebitNote_IsPenaltyUnderReview()
    {
        // Fix "multa fantasma": la multa se confirmó pero su ND FALLÓ su emisión. Ya NO es "por cobrar" (no hay
        // comprobante válido): es "en revisión" (la destraba el back-office). El cartel de deuda no se pinta.
        await using var context = CreateContext();
        var reserva = await SeedCancelledAsync(context, balance: 800m);
        await AddCancellationRawAsync(
            context, reserva.Id,
            penalty: PenaltyStatus.Confirmed,
            debitNote: DebitNoteStatus.Failed,
            penaltyAmount: 800m);
        var service = CreateService(context);

        var dto = await service.GetReservaByIdAsync(reserva.PublicId.ToString(), CancellationToken.None);

        Assert.False(dto.HasOverdueDebt);
        Assert.Equal("MultaEnRevision", dto.CancelledMoneyContext);
        // El monto de la multa NO se expone cuando está en revisión (solo en "MultaPorCobrar").
        Assert.Null(dto.CancelledPenaltyAmount);
    }

    [Fact]
    public async Task ConfirmedPenalty_WithManualReviewDebitNote_IsPenaltyUnderReview()
    {
        // Igual que Failed: una ND derivada a resolución manual es "en revisión", no "por cobrar".
        await using var context = CreateContext();
        var reserva = await SeedCancelledAsync(context, balance: 800m);
        await AddCancellationRawAsync(
            context, reserva.Id,
            penalty: PenaltyStatus.Confirmed,
            debitNote: DebitNoteStatus.ManualReview,
            penaltyAmount: 800m);
        var service = CreateService(context);

        var dto = await service.GetReservaByIdAsync(reserva.PublicId.ToString(), CancellationToken.None);

        Assert.Equal("MultaEnRevision", dto.CancelledMoneyContext);
    }

    [Fact]
    public async Task ConfirmedPenalty_WithZeroAmount_IsNotLive_IsInconsistent()
    {
        // CAMBIO INTENCIONAL (fix "multa fantasma"): antes la rama "PenaltyStatus==Confirmed" pelada hacía viva la
        // multa. Ahora la rama de emisión diferida exige monto > 0. Confirmed + monto 0 (sin ND) ya NO es viva:
        // con saldo positivo cae en "Inconsistente" (dato roto: hay saldo pero ningún respaldo real de multa).
        await using var context = CreateContext();
        var reserva = await SeedCancelledAsync(context, balance: 800m);
        await AddCancellationRawAsync(
            context, reserva.Id,
            penalty: PenaltyStatus.Confirmed,
            debitNote: DebitNoteStatus.NotApplicable,
            penaltyAmount: 0m);
        var service = CreateService(context);

        var dto = await service.GetReservaByIdAsync(reserva.PublicId.ToString(), CancellationToken.None);

        Assert.Equal("Inconsistente", dto.CancelledMoneyContext);
    }

    [Fact]
    public async Task IssuedDebitNote_WithLinkedInvoice_IsPenaltyReceivable()
    {
        // ND emitida (Issued) con su factura vinculada y NO anulada: es la rama 1 del predicado (respaldo firme).
        await using var context = CreateContext();
        var reserva = await SeedCancelledAsync(context, balance: 1000m);
        var ndInvoiceId = await SeedDebitNoteInvoiceAsync(context, AnnulmentStatus.None);
        await AddCancellationRawAsync(
            context, reserva.Id,
            penalty: PenaltyStatus.Estimated, // sin la rama 2: probamos SOLO la rama 1 (ND emitida no anulada).
            debitNote: DebitNoteStatus.Issued,
            debitNoteInvoiceId: ndInvoiceId);
        var service = CreateService(context);

        var dto = await service.GetReservaByIdAsync(reserva.PublicId.ToString(), CancellationToken.None);

        Assert.Equal("MultaPorCobrar", dto.CancelledMoneyContext);
    }

    [Fact]
    public async Task IssuedDebitNote_WithAnnulledInvoice_IsNotLive_IsInconsistent()
    {
        // ND emitida pero su factura fue ANULADA (AnnulmentStatus.Succeeded): la rama 1 la excluye. Con penalidad
        // Estimated (rama 2 también off), deja de ser viva -> saldo positivo sin respaldo = Inconsistente.
        await using var context = CreateContext();
        var reserva = await SeedCancelledAsync(context, balance: 1000m);
        var ndInvoiceId = await SeedDebitNoteInvoiceAsync(context, AnnulmentStatus.Succeeded);
        await AddCancellationRawAsync(
            context, reserva.Id,
            penalty: PenaltyStatus.Estimated,
            debitNote: DebitNoteStatus.Issued,
            debitNoteInvoiceId: ndInvoiceId);
        var service = CreateService(context);

        var dto = await service.GetReservaByIdAsync(reserva.PublicId.ToString(), CancellationToken.None);

        Assert.Equal("Inconsistente", dto.CancelledMoneyContext);
    }

    [Fact]
    public async Task PenaltyReceivable_ExposesPenaltyAmountAndCurrency()
    {
        // El monto/moneda de la multa acompaña SOLO al caso "MultaPorCobrar". La moneda se normaliza de ARCA
        // ("DOL") a ISO ("USD") para mostrarla.
        await using var context = CreateContext();
        var reserva = await SeedCancelledAsync(context, balance: 1000m);
        await AddCancellationRawAsync(
            context, reserva.Id,
            penalty: PenaltyStatus.Confirmed,
            debitNote: DebitNoteStatus.Pending,
            penaltyAmount: 1000m,
            penaltyCurrencyAtEvent: "DOL"); // espacio ARCA -> se normaliza a USD.
        var service = CreateService(context);

        var dto = await service.GetReservaByIdAsync(reserva.PublicId.ToString(), CancellationToken.None);

        Assert.Equal("MultaPorCobrar", dto.CancelledMoneyContext);
        Assert.Equal(1000m, dto.CancelledPenaltyAmount);
        Assert.Equal("USD", dto.CancelledPenaltyCurrency);
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
        // Multa viva genuina por la rama 2 (ventana de emisión diferida): Confirmed + Pending + monto > 0.
        await AddCancellationRawAsync(
            context, cancelled.Id, PenaltyStatus.Confirmed, DebitNoteStatus.Pending, penaltyAmount: 400m);

        var service = CreateService(context);

        var page = await service.GetReservasAsync(
            new ReservaListQuery { View = "closed" }, CancellationToken.None);

        var cancelledRow = page.Items.Single(i => i.NumeroReserva == "F-2026-0100");
        var closedRow = page.Items.Single(i => i.NumeroReserva == "F-2026-0101");
        Assert.Equal("MultaPorCobrar", cancelledRow.CancelledMoneyContext);
        Assert.Null(closedRow.CancelledMoneyContext);
    }

    [Fact]
    public async Task List_DistinguishesLiveFromUnderReview_AndExposesPenaltyAmountOnlyForLive()
    {
        // Página con dos anuladas: una con multa VIVA (por cobrar, con monto) y otra con multa EN REVISIÓN (ND
        // fallida). El listado debe pintar tokens distintos y exponer el monto SOLO en la viva.
        await using var context = CreateContext();

        var live = new Reserva
        {
            Id = 1, NumeroReserva = "F-2026-0300", Name = "Anulada multa viva",
            Status = EstadoReserva.Cancelled, ResponsibleUserId = "vendedor-1", Balance = 700m,
            EndDate = new DateTime(2026, 06, 01, 0, 0, 0, DateTimeKind.Utc),
        };
        var underReview = new Reserva
        {
            Id = 2, NumeroReserva = "F-2026-0301", Name = "Anulada multa en revisión",
            Status = EstadoReserva.Cancelled, ResponsibleUserId = "vendedor-1", Balance = 700m,
            EndDate = new DateTime(2026, 06, 01, 0, 0, 0, DateTimeKind.Utc),
        };
        context.Reservas.AddRange(live, underReview);
        await context.SaveChangesAsync();
        await AddCancellationRawAsync(
            context, live.Id, PenaltyStatus.Confirmed, DebitNoteStatus.Pending,
            penaltyAmount: 700m, penaltyCurrencyAtEvent: "PES");
        await AddCancellationRawAsync(
            context, underReview.Id, PenaltyStatus.Confirmed, DebitNoteStatus.Failed, penaltyAmount: 700m);

        var service = CreateService(context);

        var page = await service.GetReservasAsync(
            new ReservaListQuery { View = "closed" }, CancellationToken.None);

        var liveRow = page.Items.Single(i => i.NumeroReserva == "F-2026-0300");
        var underReviewRow = page.Items.Single(i => i.NumeroReserva == "F-2026-0301");

        Assert.Equal("MultaPorCobrar", liveRow.CancelledMoneyContext);
        Assert.Equal(700m, liveRow.CancelledPenaltyAmount);
        Assert.Equal("ARS", liveRow.CancelledPenaltyCurrency);

        Assert.Equal("MultaEnRevision", underReviewRow.CancelledMoneyContext);
        Assert.Null(underReviewRow.CancelledPenaltyAmount);
    }

    [Fact]
    public async Task IssuedDebitNote_AnnulledInvoice_ButPenaltyStillConfirmed_IsNotLive_IsInconsistent()
    {
        // Caso de borde que el pase final de la Tanda 1 cierra: una ND EMITIDA cuya factura fue ANULADA después
        // (la rama 1 la excluye por AnnulmentStatus.Succeeded) pero con la multa TODAVÍA Confirmed + monto > 0.
        // Antes, la rama 2 del predicado ("Confirmed + monto>0 + distinto de Failed/ManualReview") la admitía
        // porque Issued no es Failed ni ManualReview -> el cartel "multa por cobrar" seguía pegado sobre un
        // comprobante anulado, socavando el guard fiscal de la rama 1. Con la rama 2 acotada a NotApplicable/Pending,
        // Issued queda gobernado SOLO por la rama 1 (que la excluye). No es Live ni UnderReview (Issued no es
        // Failed/ManualReview): con saldo positivo cae en "Inconsistente" -> NO se pinta cartel; lo vigila el
        // watchdog interno (la bandeja), que es lo correcto para una ND emitida-luego-anulada con multa sin resolver.
        await using var context = CreateContext();
        var reserva = await SeedCancelledAsync(context, balance: 1000m);
        var ndInvoiceId = await SeedDebitNoteInvoiceAsync(context, AnnulmentStatus.Succeeded);
        await AddCancellationRawAsync(
            context, reserva.Id,
            penalty: PenaltyStatus.Confirmed,
            debitNote: DebitNoteStatus.Issued,
            penaltyAmount: 500m,
            debitNoteInvoiceId: ndInvoiceId);
        var service = CreateService(context);

        var dto = await service.GetReservaByIdAsync(reserva.PublicId.ToString(), CancellationToken.None);

        Assert.Equal("Inconsistente", dto.CancelledMoneyContext);
        // Y no se expone monto de multa (no es "por cobrar").
        Assert.Null(dto.CancelledPenaltyAmount);
    }

    [Fact]
    public async Task List_PenaltyReceivable_ShowsPendingNetOfCollected()
    {
        // Fix "monto del cartel = lo PENDIENTE de cobro": la multa bruta es 3500, pero el cliente ya pagó parte y
        // el saldo por moneda quedó en 2500. El cartel debe mostrar 2500 (lo que falta cobrar), NO el bruto 3500.
        await using var context = CreateContext();
        var reserva = new Reserva
        {
            Id = 1, NumeroReserva = "F-2026-0400", Name = "Anulada multa parcialmente cobrada",
            Status = EstadoReserva.Cancelled, ResponsibleUserId = "vendedor-1", Balance = 2500m,
            EndDate = new DateTime(2026, 06, 01, 0, 0, 0, DateTimeKind.Utc),
        };
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();
        await SeedMoneyByCurrencyAsync(context, reserva.Id, "ARS", balance: 2500m);
        await AddCancellationRawAsync(
            context, reserva.Id, PenaltyStatus.Confirmed, DebitNoteStatus.Pending,
            penaltyAmount: 3500m, penaltyCurrencyAtEvent: "PES");

        var service = CreateService(context);
        var page = await service.GetReservasAsync(
            new ReservaListQuery { View = "closed" }, CancellationToken.None);

        var row = page.Items.Single(i => i.NumeroReserva == "F-2026-0400");
        Assert.Equal("MultaPorCobrar", row.CancelledMoneyContext);
        Assert.Equal(2500m, row.CancelledPenaltyAmount); // 3500 bruto - 1000 ya cobrado = 2500 pendiente.
        Assert.Equal("ARS", row.CancelledPenaltyCurrency);
    }

    [Fact]
    public async Task List_PenaltyReceivable_NoCollection_ShowsGrossPenalty()
    {
        // Sin cobros: el saldo por moneda == la multa entera (3500). El cartel muestra 3500 (min(3500, 3500)).
        await using var context = CreateContext();
        var reserva = new Reserva
        {
            Id = 1, NumeroReserva = "F-2026-0401", Name = "Anulada multa sin cobrar",
            Status = EstadoReserva.Cancelled, ResponsibleUserId = "vendedor-1", Balance = 3500m,
            EndDate = new DateTime(2026, 06, 01, 0, 0, 0, DateTimeKind.Utc),
        };
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();
        await SeedMoneyByCurrencyAsync(context, reserva.Id, "ARS", balance: 3500m);
        await AddCancellationRawAsync(
            context, reserva.Id, PenaltyStatus.Confirmed, DebitNoteStatus.Pending,
            penaltyAmount: 3500m, penaltyCurrencyAtEvent: "PES");

        var service = CreateService(context);
        var page = await service.GetReservasAsync(
            new ReservaListQuery { View = "closed" }, CancellationToken.None);

        var row = page.Items.Single(i => i.NumeroReserva == "F-2026-0401");
        Assert.Equal("MultaPorCobrar", row.CancelledMoneyContext);
        Assert.Equal(3500m, row.CancelledPenaltyAmount);
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
