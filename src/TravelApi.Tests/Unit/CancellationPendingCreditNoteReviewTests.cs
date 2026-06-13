using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Moq;
using TravelApi.Authorization;
using TravelApi.Controllers;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-009/ADR-025 (read-model, 2026-06-13) — tests UNIT de la bandeja "Notas de credito por revisar"
/// (<c>GetCancellationsPendingCreditNoteReviewAsync</c>). Verifica que SOLO lista los BCs en
/// <c>ManualReviewPending</c>/<c>RequiresManualReview</c> (no los demas estados), que proyecta los datos
/// que la bandeja necesita (reserva, cliente, fecha de entrada, monto/moneda de la NC) y que tolera el BC
/// sin liquidacion poblada (monto null).
///
/// <para>DbContext InMemory + mocks de las deps del ctor (el metodo es solo-lectura, no las usa). Mismo
/// enfoque que <see cref="CancellationDeferredPenaltyTests"/>.</para>
/// </summary>
public class CancellationPendingCreditNoteReviewTests
{
    private static AppDbContext NewDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"adr025-pending-ncreview-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    private static BookingCancellationService BuildService(AppDbContext ctx)
    {
        // El metodo bajo prueba es solo-lectura: no toca ninguna de estas deps. Las mockeamos vacias
        // solo para satisfacer el ctor.
        return new BookingCancellationService(
            ctx,
            new Mock<IInvoiceService>().Object,
            new Mock<IApprovalRequestService>().Object,
            new Mock<IAuditService>().Object,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BookingCancellationService>.Instance,
            new Mock<IOperationalFinanceSettingsService>().Object,
            new Mock<IFiscalLiquidationCalculator>().Object,
            new Mock<IAdminUserCountService>().Object);
    }

    /// <summary>
    /// Crea una reserva + (opcional) cliente pagador y un BC en el estado pedido. El monto/moneda de la
    /// NC se setea via el owned VO FiscalLiquidation cuando <paramref name="amount"/> no es null.
    /// </summary>
    private static async Task<BookingCancellation> SeedBcAsync(
        AppDbContext ctx,
        int reservaId,
        BookingCancellationStatus status,
        DateTime? enteredReviewAt = null,
        decimal? amount = null,
        string currency = "ARS",
        string? payerName = null)
    {
        Customer? payer = null;
        if (payerName is not null)
        {
            payer = new Customer { Id = reservaId * 10, FullName = payerName };
            ctx.Customers.Add(payer);
        }

        var reserva = new Reserva
        {
            Id = reservaId,
            NumeroReserva = $"F-2026-{reservaId:D4}",
            Name = $"Reserva {reservaId}",
            Status = EstadoReserva.Confirmed,
            PayerId = payer?.Id
        };
        ctx.Reservas.Add(reserva);

        var bc = new BookingCancellation
        {
            Id = reservaId, // 1:1 con la reserva en estos tests
            PublicId = Guid.NewGuid(),
            ReservaId = reservaId,
            CustomerId = payer?.Id ?? 0,
            SupplierId = 0,
            OriginatingInvoiceId = 0,
            Status = status,
            Reason = "test",
            DraftedByUserId = "u-1",
            ConfirmedWithClientAt = enteredReviewAt,
            FiscalLiquidation = amount.HasValue
                ? new FiscalLiquidation { FiscalAmountToCredit = amount.Value, Currency = currency }
                : null
        };
        ctx.BookingCancellations.Add(bc);

        await ctx.SaveChangesAsync();
        return bc;
    }

    [Fact]
    public async Task PendingReview_OnlyListsManualReviewStates()
    {
        await using var ctx = NewDbContext();
        // Uno en cada estado que SI debe aparecer.
        await SeedBcAsync(ctx, 1, BookingCancellationStatus.ManualReviewPending);
        await SeedBcAsync(ctx, 2, BookingCancellationStatus.RequiresManualReview);
        // Estados que NO deben aparecer (muestra de la maquina de estados).
        await SeedBcAsync(ctx, 3, BookingCancellationStatus.Drafted);
        await SeedBcAsync(ctx, 4, BookingCancellationStatus.AwaitingFiscalConfirmation);
        await SeedBcAsync(ctx, 5, BookingCancellationStatus.ManualReviewApproved);
        await SeedBcAsync(ctx, 6, BookingCancellationStatus.ManualReviewRejected);
        await SeedBcAsync(ctx, 7, BookingCancellationStatus.Closed);

        var service = BuildService(ctx);
        var rows = await service.GetCancellationsPendingCreditNoteReviewAsync(CancellationToken.None);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Contains(r.Status,
            new[] { "ManualReviewPending", "RequiresManualReview" }));
        // Las reservas 3..7 (otros estados) no salieron.
        Assert.DoesNotContain(rows, r => r.ReservaNumero == "F-2026-0003");
        Assert.DoesNotContain(rows, r => r.ReservaNumero == "F-2026-0005");
    }

    [Fact]
    public async Task PendingReview_ProjectsReservaCustomerAmountAndDate()
    {
        await using var ctx = NewDbContext();
        var enteredAt = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var bc = await SeedBcAsync(
            ctx, 1, BookingCancellationStatus.ManualReviewPending,
            enteredReviewAt: enteredAt, amount: 1500m, currency: "USD", payerName: "Juan Perez");

        var service = BuildService(ctx);
        var rows = await service.GetCancellationsPendingCreditNoteReviewAsync(CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal(bc.PublicId, row.BookingCancellationPublicId);
        Assert.Equal("F-2026-0001", row.ReservaNumero);
        Assert.Equal("Juan Perez", row.ClienteNombre); // tomo el nombre del pagador
        Assert.Equal("ManualReviewPending", row.Status);
        Assert.Equal(enteredAt, row.EnteredReviewAt);
        Assert.Equal(1500m, row.CreditNoteAmount);
        Assert.Equal("USD", row.CreditNoteCurrency);
        Assert.NotEqual(Guid.Empty, row.ReservaPublicId);
    }

    [Fact]
    public async Task PendingReview_NoLiquidation_AmountIsNull()
    {
        await using var ctx = NewDbContext();
        // BC sin liquidacion poblada (caso borde de datos): el monto debe venir null, no romper.
        await SeedBcAsync(ctx, 1, BookingCancellationStatus.ManualReviewPending, amount: null);

        var service = BuildService(ctx);
        var rows = await service.GetCancellationsPendingCreditNoteReviewAsync(CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Null(row.CreditNoteAmount);
        Assert.Null(row.CreditNoteCurrency);
        // Sin pagador, cae al nombre de la reserva.
        Assert.Equal("Reserva 1", row.ClienteNombre);
    }

    [Fact]
    public async Task PendingReview_EmptyWhenNoneInReview()
    {
        await using var ctx = NewDbContext();
        await SeedBcAsync(ctx, 1, BookingCancellationStatus.Drafted);
        await SeedBcAsync(ctx, 2, BookingCancellationStatus.Closed);

        var service = BuildService(ctx);
        var rows = await service.GetCancellationsPendingCreditNoteReviewAsync(CancellationToken.None);

        Assert.Empty(rows);
    }

    [Fact]
    public async Task PendingReview_OrderedByEntryAscending()
    {
        await using var ctx = NewDbContext();
        await SeedBcAsync(ctx, 1, BookingCancellationStatus.ManualReviewPending,
            enteredReviewAt: new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc));
        await SeedBcAsync(ctx, 2, BookingCancellationStatus.ManualReviewPending,
            enteredReviewAt: new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        var service = BuildService(ctx);
        var rows = await service.GetCancellationsPendingCreditNoteReviewAsync(CancellationToken.None);

        // Mas antiguo primero (prioridad de revision).
        Assert.Equal("F-2026-0002", rows[0].ReservaNumero);
        Assert.Equal("F-2026-0001", rows[1].ReservaNumero);
    }

    /// <summary>
    /// SEC-1 (privacidad, 2026-06-13): esta bandeja es cross-reserva, SIN ownership por fila, y EXPONE el
    /// nombre del cliente. Debe quedar gateada con <c>cobranzas.view_all</c> (back-office ve todo) y NO con
    /// <c>cobranzas.invoice_annul</c>, que el Vendedor tiene para anular SUS facturas. Si alguien afloja el
    /// gate al permiso del Vendedor, le filtraria nombres de clientes de reservas ajenas (fuga horizontal).
    /// Este test bloquea esa regresion leyendo el atributo real del endpoint por reflexion.
    /// </summary>
    [Fact]
    public void Endpoint_IsGatedByBackOfficeViewAll_NotSellerInvoiceAnnul()
    {
        var method = typeof(CancellationsController).GetMethod(
            nameof(CancellationsController.GetPendingCreditNoteReview));
        Assert.NotNull(method);

        var attribute = method!.GetCustomAttribute<RequirePermissionAttribute>();
        Assert.NotNull(attribute);

        // El permiso queda codificado en Policy (PERM:<permiso>). Lo decodificamos con el mismo parser.
        var permissions = RequirePermissionAttribute.TryParsePolicyName(attribute!.Policy!);
        Assert.NotNull(permissions);
        Assert.Contains(Permissions.CobranzasViewAll, permissions!);
        Assert.DoesNotContain(Permissions.CobranzasInvoiceAnnul, permissions!);
    }

    /// <summary>
    /// SEC-1 (privacidad, 2026-06-13): refuerza el invariante de roles del que depende el gate de arriba.
    /// El permiso elegido solo protege la bandeja si el Vendedor NO lo tiene. Si un cambio futuro le agrega
    /// <c>cobranzas.view_all</c> al Vendedor, la proteccion se cae en silencio — este test lo detecta.
    /// </summary>
    [Fact]
    public void Vendedor_DoesNotHaveBackOfficeViewAll()
    {
        Assert.DoesNotContain(Permissions.CobranzasViewAll, Permissions.DefaultVendedor);
        // Sanity: el back-office (Colaborador) y Admin SI lo tienen, asi que la bandeja sigue accesible.
        Assert.Contains(Permissions.CobranzasViewAll, Permissions.DefaultColaborador);
        Assert.Contains(Permissions.CobranzasViewAll, Permissions.DefaultAdmin);
    }
}
