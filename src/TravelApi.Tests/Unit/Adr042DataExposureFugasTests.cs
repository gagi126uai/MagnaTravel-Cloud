using System;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Controllers;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Data-exposure follow-up de ADR-042 (2026-07-03): las 3 fugas de mensajes tecnicos al usuario.
/// FUGA 1 (bandeja de NDs pendientes) + FUGA 3 (bodies de error del controller). FUGA 2 (notificacion de
/// anulacion fallida) se cubre por la deteccion del <c>ArcaErrorSanitizer</c> (ver Adr042ArcaErrorSanitizationTests):
/// el mensaje se arma con <c>SanitizeArcaError(Observaciones)</c>, cuya clasificacion ya esta blindada ahi.
/// </summary>
public class Adr042DataExposureFugasTests
{
    private const string GenericArca = "AFIP rechazó la factura. Revisá los datos fiscales o volvé a intentar.";

    // =========================================================================
    // FUGA 1 — bandeja de NDs pendientes: DebitNoteArcaErrorMessage saneado en el DTO
    // =========================================================================

    private static AppDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"fuga1-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static BookingCancellationService BuildBcService(AppDbContext ctx)
    {
        var settingsMock = new Mock<IOperationalFinanceSettingsService>();
        settingsMock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings { EnableNewCancellationFlow = true, OperatorRefundTimeoutDays = 60 });

        return new BookingCancellationService(
            ctx, Mock.Of<IInvoiceService>(), Mock.Of<IApprovalRequestService>(), Mock.Of<IAuditService>(),
            NullLogger<BookingCancellationService>.Instance, settingsMock.Object,
            Mock.Of<IFiscalLiquidationCalculator>(), Mock.Of<IAdminUserCountService>());
    }

    [Fact]
    public async Task Fuga1_BandejaNds_ArcaErrorMessageTecnico_seSanea()
    {
        await using var ctx = NewDbContext();

        var reserva = new Reserva { NumeroReserva = "F-2026-1042", Name = "Reserva", Status = EstadoReserva.Cancelled };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        // BC con ND FALLIDA cuyo motivo de ARCA es ruido tecnico (XML SOAP). DebitNoteInvoiceId no-null para que
        // NO caiga en la rama de ND huerfana (que exige DebitNoteInvoiceId == null).
        ctx.BookingCancellations.Add(new BookingCancellation
        {
            ReservaId = reserva.Id,
            CustomerId = 1,
            SupplierId = 1,
            OriginatingInvoiceId = 1,
            CreditNoteInvoiceId = 999,
            DebitNoteInvoiceId = 888,
            DebitNoteStatus = DebitNoteStatus.Failed,
            DebitNoteArcaErrorMessage = "<soap:Fault><faultstring>Object reference not set</faultstring></soap:Fault>",
            Status = BookingCancellationStatus.AwaitingOperatorRefund,
            Reason = "cancelacion",
            DraftedByUserId = "u",
            FiscalSnapshot = new FiscalSnapshot { CurrencyAtEvent = "ARS", Source = ExchangeRateSource.Manual, ExchangeRateAtOriginalInvoice = 1m, FetchedAt = DateTime.UtcNow },
        });
        await ctx.SaveChangesAsync();

        var service = BuildBcService(ctx);
        var rows = await service.GetCancellationsWithMissingDebitNoteAsync(CancellationToken.None);

        var row = Assert.Single(rows);
        // El motivo tecnico NO llega crudo: se reemplaza por el generico. Nada de XML ni "Object reference".
        Assert.Equal(GenericArca, row.ArcaErrorMessage);
        Assert.DoesNotContain("<soap", row.ArcaErrorMessage);
        Assert.DoesNotContain("Object reference", row.ArcaErrorMessage);
    }

    [Fact]
    public async Task Fuga1_BandejaNds_MotivoAfipPlano_seConserva()
    {
        await using var ctx = NewDbContext();
        var reserva = new Reserva { NumeroReserva = "F-2026-1043", Name = "Reserva", Status = EstadoReserva.Cancelled };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        ctx.BookingCancellations.Add(new BookingCancellation
        {
            ReservaId = reserva.Id, CustomerId = 1, SupplierId = 1, OriginatingInvoiceId = 1,
            CreditNoteInvoiceId = 999, DebitNoteInvoiceId = 888, DebitNoteStatus = DebitNoteStatus.Failed,
            DebitNoteArcaErrorMessage = "CUIT del emisor sin habilitación",
            Status = BookingCancellationStatus.AwaitingOperatorRefund, Reason = "cancelacion", DraftedByUserId = "u",
            FiscalSnapshot = new FiscalSnapshot { CurrencyAtEvent = "ARS", Source = ExchangeRateSource.Manual, ExchangeRateAtOriginalInvoice = 1m, FetchedAt = DateTime.UtcNow },
        });
        await ctx.SaveChangesAsync();

        var service = BuildBcService(ctx);
        var rows = await service.GetCancellationsWithMissingDebitNoteAsync(CancellationToken.None);

        var row = Assert.Single(rows);
        // El motivo de AFIP en texto plano (util para el vendedor) se conserva.
        Assert.Equal("CUIT del emisor sin habilitación", row.ArcaErrorMessage);
    }

    [Fact]
    public async Task Fuga1_BandejaNds_MotivoPersistidoPorElJob_seSanea()
    {
        // Espejo del bug 2026-07-13: ahora el job del CAE (via CancellationDebitNoteReconciliation) es el que
        // persiste el motivo de rechazo de ARCA en DebitNoteArcaErrorMessage, no solo la lectura de la bandeja.
        // Este test recorre esa VIA NUEVA de punta a punta: reconciliamos una ND rechazada con Observaciones
        // tecnicas crudas (SOAP fault) y verificamos que, al proyectar la bandeja, ese motivo sale SANEADO
        // (no filtra XML ni jerga tecnica al usuario).
        await using var ctx = NewDbContext();

        var reserva = new Reserva { NumeroReserva = "F-2026-1046", Name = "Reserva", Status = EstadoReserva.Cancelled };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        // ND rechazada por ARCA con ruido tecnico en Observaciones (lo que el job recibiria de WSFE).
        var debitNote = new Invoice
        {
            TipoComprobante = 2,
            Resultado = "R",
            Observaciones = "<soap:Fault><faultstring>Object reference not set</faultstring></soap:Fault>",
            ReservaId = reserva.Id,
        };
        ctx.Invoices.Add(debitNote);
        await ctx.SaveChangesAsync();

        // BC con la ND vinculada todavia en Pending (esperando el CAE async).
        ctx.BookingCancellations.Add(new BookingCancellation
        {
            ReservaId = reserva.Id,
            CustomerId = 1,
            SupplierId = 1,
            OriginatingInvoiceId = 1,
            CreditNoteInvoiceId = 999,
            DebitNoteInvoiceId = debitNote.Id,
            DebitNoteStatus = DebitNoteStatus.Pending,
            Status = BookingCancellationStatus.AwaitingOperatorRefund,
            Reason = "cancelacion",
            DraftedByUserId = "u",
            FiscalSnapshot = new FiscalSnapshot { CurrencyAtEvent = "ARS", Source = ExchangeRateSource.Manual, ExchangeRateAtOriginalInvoice = 1m, FetchedAt = DateTime.UtcNow },
        });
        await ctx.SaveChangesAsync();

        // VIA NUEVA: el job reconcilia la ND rechazada -> persiste el motivo crudo en DebitNoteArcaErrorMessage.
        int changed = await CancellationDebitNoteReconciliation.ReconcileLinkedCancellationFromDebitNoteAsync(
            ctx, debitNote, NullLogger.Instance, CancellationToken.None);
        Assert.Equal(1, changed);

        // La proyeccion de la bandeja debe sanear ese motivo tecnico (MapPendingDebitNoteRow).
        var service = BuildBcService(ctx);
        var rows = await service.GetCancellationsWithMissingDebitNoteAsync(CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal(GenericArca, row.ArcaErrorMessage);
        Assert.DoesNotContain("<soap", row.ArcaErrorMessage);
        Assert.DoesNotContain("Object reference", row.ArcaErrorMessage);
    }

    // =========================================================================
    // FUGA 3 — bodies de error del controller: business pasa, tecnico -> generico
    // =========================================================================

    private static CancellationsController BuildController(IBookingCancellationService bcService)
    {
        var controller = new CancellationsController(
            bcService, Mock.Of<IOwnershipResolver>(), Mock.Of<IUserPermissionResolver>(),
            NullLogger<CancellationsController>.Instance);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                // Admin: UserIsAllowedOverReservaAsync devuelve true sin tocar el ownership resolver.
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, "admin-1"),
                    new Claim(ClaimTypes.Role, "Admin"),
                }, "Test")),
            },
        };
        return controller;
    }

    private static string? MessageOf(ActionResult<BookingCancellationDto> result)
    {
        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        return (string?)conflict.Value!.GetType().GetProperty("message")!.GetValue(conflict.Value);
    }

    [Theory]
    [InlineData("Sequence contains no elements.")]
    [InlineData("The instance of entity type 'BookingCancellation' cannot be tracked because another instance with the key value '{Id: 42}' is already being tracked.")]
    public async Task Fuga3_Draft_InvalidOperationTecnica_DevuelveGenerico_SinFiltrar(string rawMessage)
    {
        var bcService = new Mock<IBookingCancellationService>();
        bcService
            .Setup(s => s.DraftAsync(It.IsAny<DraftCancellationRequest>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(rawMessage));

        var controller = BuildController(bcService.Object);
        var result = await controller.Draft(
            new DraftCancellationRequest(Guid.NewGuid(), "Cliente se arrepiente de la reserva"), CancellationToken.None);

        var message = MessageOf(result);
        Assert.Equal("No se pudo completar la operación. Volvé a intentar.", message);
        Assert.DoesNotContain("Sequence contains", message);
        Assert.DoesNotContain("entity type", message);
        Assert.DoesNotContain("BookingCancellation", message);
        Assert.DoesNotContain("Id:", message);
    }

    [Fact]
    public async Task Fuga3_Draft_InvalidOperationDeNegocio_seConserva()
    {
        // Texto de negocio limpio y util para el vendedor: se muestra tal cual (whitelist por blocklist).
        const string businessMessage = "La reserva F-2026-1042 no tiene factura activa para anular.";
        var bcService = new Mock<IBookingCancellationService>();
        bcService
            .Setup(s => s.DraftAsync(It.IsAny<DraftCancellationRequest>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(businessMessage));

        var controller = BuildController(bcService.Object);
        var result = await controller.Draft(
            new DraftCancellationRequest(Guid.NewGuid(), "Cliente se arrepiente de la reserva"), CancellationToken.None);

        Assert.Equal(businessMessage, MessageOf(result));
    }

    // =========================================================================
    // FUGA B6 — rama 400 (ArgumentException) tambien saneada
    // =========================================================================

    private static string? BadRequestMessageOf(ActionResult<BookingCancellationDto> result)
    {
        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        return (string?)badRequest.Value!.GetType().GetProperty("message")!.GetValue(badRequest.Value);
    }

    [Fact]
    public async Task FugaB6_Draft_ArgumentNullDelBinding_DevuelveGenerico_SinValueCannotBeNull()
    {
        // ArgumentNullException del framework: "Value cannot be null. (Parameter 'req')" NO debe llegar al body.
        var bcService = new Mock<IBookingCancellationService>();
        bcService
            .Setup(s => s.DraftAsync(It.IsAny<DraftCancellationRequest>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentNullException("req"));

        var controller = BuildController(bcService.Object);
        var result = await controller.Draft(
            new DraftCancellationRequest(Guid.NewGuid(), "Cliente se arrepiente de la reserva"), CancellationToken.None);

        var message = BadRequestMessageOf(result);
        Assert.Equal("Los datos enviados no son válidos. Revisá el formulario y volvé a intentar.", message);
        Assert.DoesNotContain("Value cannot be null", message);
        Assert.DoesNotContain("Parameter", message);
    }

    [Fact]
    public async Task FugaB6_Draft_ArgumentExceptionDeNegocio_seConserva_SinSufijoParameter()
    {
        // Un mensaje de validacion en criollo se conserva, pero el sufijo del framework
        // " (Parameter 'request')" se recorta (es un nombre interno).
        var bcService = new Mock<IBookingCancellationService>();
        bcService
            .Setup(s => s.DraftAsync(It.IsAny<DraftCancellationRequest>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("La fecha de confirmación no puede ser una fecha futura.", "request"));

        var controller = BuildController(bcService.Object);
        var result = await controller.Draft(
            new DraftCancellationRequest(Guid.NewGuid(), "Cliente se arrepiente de la reserva"), CancellationToken.None);

        var message = BadRequestMessageOf(result);
        Assert.Equal("La fecha de confirmación no puede ser una fecha futura.", message);
        Assert.DoesNotContain("Parameter", message);
        Assert.DoesNotContain("request", message);
    }
}
