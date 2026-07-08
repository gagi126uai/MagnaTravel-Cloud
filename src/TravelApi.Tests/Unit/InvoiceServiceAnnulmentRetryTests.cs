using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Hangfire;
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
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Bug de produccion (2026-07-07): anular una factura dispara ProcessAnnulmentJob, que emite la NC total contra
/// AFIP. Si AFIP no responde (DNS caido), la NC quedaba PENDING; el reintento de Hangfire re-corria el job entero,
/// el guard anti-doble-emision solo veia NC con Resultado="A" (no la PENDING), volvia a llamar CreatePendingInvoice
/// y chocaba contra el unique index "una sola PENDING por reserva" -> contexto envenenado -> anulacion a medias.
///
/// <para>Estos tests cubren las 3 patas del fix (todas en <see cref="InvoiceService.ProcessAnnulmentJob"/>):</para>
/// <list type="bullet">
///   <item><b>F1</b> reintento idempotente: el reintento RETOMA la NC pendiente en vez de crear una segunda.</item>
///   <item><b>F2</b> "AFIP no respondio" (PENDING/red) NO es un rechazo "R": no marca el BC como rechazado y el
///     aviso al usuario no filtra el detalle tecnico (hostname).</item>
///   <item><b>F3</b> el catch despoisona el contexto y remedia (marca Failed + avisa) sin reventar.</item>
/// </list>
///
/// <para>OJO: EF InMemory NO aplica el unique index de Postgres, asi que NO reproducimos el 23505 real. Testeamos
/// el COMPORTAMIENTO observable (no se crea una segunda NC, no se llama OnArcaFailed, el aviso no filtra tecnico).</para>
/// </summary>
public class InvoiceServiceAnnulmentRetryTests
{
    private readonly DbContextOptions<AppDbContext> _dbOptions;
    private readonly IMapper _mapper;
    private readonly Mock<IOperationalFinanceSettingsService> _settingsServiceMock;
    private readonly Mock<IBackgroundJobClient> _jobClientMock;
    private readonly Mock<IAfipService> _afipMock;
    private readonly Mock<IInvoicePdfService> _pdfMock;
    private readonly Mock<IInvoiceAnnulmentBcBridge> _bcBridgeMock;

    public InvoiceServiceAnnulmentRetryTests()
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

        _jobClientMock = new Mock<IBackgroundJobClient>();
        _afipMock = new Mock<IAfipService>();
        _pdfMock = new Mock<IInvoicePdfService>();
        _bcBridgeMock = new Mock<IInvoiceAnnulmentBcBridge>();
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

    /// <summary>
    /// IServiceProvider minimo que solo conoce el bridge de cancelacion. Devuelve null para
    /// INotificationService, asi CreateNotification cae al path que persiste en el context InMemory
    /// (y podemos leer el aviso que veria el usuario).
    /// </summary>
    private sealed class SingleServiceProvider : IServiceProvider
    {
        private readonly IInvoiceAnnulmentBcBridge _bridge;
        public SingleServiceProvider(IInvoiceAnnulmentBcBridge bridge) => _bridge = bridge;
        public object? GetService(Type serviceType)
            => serviceType == typeof(IInvoiceAnnulmentBcBridge) ? _bridge : null;
    }

    private InvoiceService BuildService(AppDbContext context)
        => new(context,
               new EntityReferenceResolver(context),
               _afipMock.Object,
               _pdfMock.Object,
               _mapper,
               _jobClientMock.Object,
               NullLogger<InvoiceService>.Instance,
               _settingsServiceMock.Object,
               BuildUserManager(),
               permissionResolver: null,
               httpContextAccessor: null,
               approvalService: null,
               approvalPolicyService: null,
               serviceProvider: new SingleServiceProvider(_bcBridgeMock.Object));

    /// <summary>Siembra una reserva y su factura de venta (tipo B, Resultado "A") lista para anular (Pending).</summary>
    private static async Task<Invoice> SeedSaleInvoicePendingAnnulmentAsync(AppDbContext context)
    {
        var reserva = new Reserva
        {
            Id = 1,
            PublicId = Guid.NewGuid(),
            NumeroReserva = "F-ANNUL-0001",
            Name = "Reserva a anular",
            Status = EstadoReserva.Confirmed,
            ResponsibleUserId = "vendedor-A",
            TotalSale = 60_000m,
            Balance = 0m,
        };
        context.Reservas.Add(reserva);

        var original = new Invoice
        {
            Id = 10,
            ReservaId = reserva.Id,
            TipoComprobante = 6, // Factura B -> NC B (8)
            PuntoDeVenta = 1,
            NumeroComprobante = 1001,
            Resultado = "A",
            CAE = "CAE-VENTA",
            MonId = "PES",
            MonCotiz = 1m,
            ImporteTotal = 60_000m,
            ImporteNeto = 60_000m,
            ImporteIva = 0m,
            AnnulmentStatus = AnnulmentStatus.Pending,
            CreatedAt = DateTime.UtcNow,
        };
        context.Invoices.Add(original);
        await context.SaveChangesAsync();
        return original;
    }

    /// <summary>
    /// Configura el mock de AFIP: CreatePendingInvoice persiste una NC PENDING (como el AfipService real) y
    /// ProcessInvoiceJob la lleva al resultado indicado por los delegados. Devuelve el Id fijo de la NC creada.
    /// </summary>
    private void SetupAfipCreatePendingCreditNote(AppDbContext context, int originalId, int creditNoteId)
    {
        _afipMock
            .Setup(a => a.CreatePendingInvoice(It.IsAny<int>(), It.IsAny<CreateInvoiceRequest>()))
            .ReturnsAsync((int reservaId, CreateInvoiceRequest _) =>
            {
                var nc = new Invoice
                {
                    Id = creditNoteId,
                    ReservaId = reservaId,
                    OriginalInvoiceId = originalId,
                    TipoComprobante = 8, // NC B
                    PuntoDeVenta = 1,
                    Resultado = "PENDING",
                    MonId = "PES",
                    MonCotiz = 1m,
                    ImporteTotal = 60_000m,
                    AnnulmentStatus = AnnulmentStatus.None,
                    CreatedAt = DateTime.UtcNow,
                };
                context.Invoices.Add(nc);
                context.SaveChanges();
                return nc;
            });
    }

    /// <summary>ProcessInvoiceJob que deja la NC en Resultado="PENDING" (AFIP no respondio; sin throw).</summary>
    private void SetupProcessInvoiceJobLeavesPending(AppDbContext context)
    {
        _afipMock
            .Setup(a => a.ProcessInvoiceJob(It.IsAny<int>()))
            .Returns((int id) =>
            {
                var nc = context.Invoices.Single(i => i.Id == id);
                nc.Resultado = "PENDING";
                nc.Observaciones = "AFIP respondió con un error de red o XML inválido. Reintentá en unos segundos.";
                context.SaveChanges();
                return Task.CompletedTask;
            });
    }

    /// <summary>ProcessInvoiceJob que aprueba la NC (Resultado="A" + CAE), como cuando AFIP ya responde bien.</summary>
    private void SetupProcessInvoiceJobApproves(AppDbContext context)
    {
        _afipMock
            .Setup(a => a.ProcessInvoiceJob(It.IsAny<int>()))
            .Returns((int id) =>
            {
                var nc = context.Invoices.Single(i => i.Id == id);
                nc.Resultado = "A";
                nc.CAE = "CAE-NC-APROBADA";
                nc.NumeroComprobante = 8500;
                nc.IssuedAt = DateTime.UtcNow;
                context.SaveChanges();
                return Task.CompletedTask;
            });
    }

    // =========================================================================================
    // F1 — reintento idempotente
    // =========================================================================================

    /// <summary>
    /// F1: el primer intento falla tecnico (AFIP deja la NC PENDING) y la segunda corrida del job NO crea una
    /// segunda NC — RETOMA la existente. Si en la segunda corrida AFIP aprueba, la factura original queda Succeeded.
    /// </summary>
    [Fact]
    public async Task ProcessAnnulmentJob_Retry_ReusesPendingCreditNote_DoesNotCreateSecond()
    {
        await using var context = new AppDbContext(_dbOptions);
        var original = await SeedSaleInvoicePendingAnnulmentAsync(context);

        SetupAfipCreatePendingCreditNote(context, original.Id, creditNoteId: 500);

        // Primera corrida: AFIP no responde -> la NC queda PENDING -> el helper lanza -> el job marca Failed y re-tira.
        SetupProcessInvoiceJobLeavesPending(context);
        var service = BuildService(context);

        var firstError = await Record.ExceptionAsync(() =>
            service.ProcessAnnulmentJob(original.Id, "vendedor-A", approvalRequestId: null));
        Assert.NotNull(firstError); // el job re-tira para que Hangfire reintente

        // Ya hay UNA NC (PENDING) para esta factura, y la factura quedo Failed (best-effort del catch).
        Assert.Equal(1, await context.Invoices.CountAsync(i => i.OriginalInvoiceId == original.Id));
        var afterFirst = await context.Invoices.AsNoTracking().FirstAsync(i => i.Id == original.Id);
        Assert.Equal(AnnulmentStatus.Failed, afterFirst.AnnulmentStatus);

        // Segunda corrida: ahora AFIP aprueba. F1 debe RETOMAR la NC pendiente (no crear otra).
        SetupProcessInvoiceJobApproves(context);

        await service.ProcessAnnulmentJob(original.Id, "vendedor-A", approvalRequestId: null);

        // Sigue habiendo UNA sola NC (se retomo, no se duplico).
        Assert.Equal(1, await context.Invoices.CountAsync(i => i.OriginalInvoiceId == original.Id));
        // CreatePendingInvoice se llamo UNA sola vez en total (solo en el primer intento).
        _afipMock.Verify(a => a.CreatePendingInvoice(It.IsAny<int>(), It.IsAny<CreateInvoiceRequest>()), Times.Once);
        // La factura original quedo definitivamente anulada.
        var afterSecond = await context.Invoices.AsNoTracking().FirstAsync(i => i.Id == original.Id);
        Assert.Equal(AnnulmentStatus.Succeeded, afterSecond.AnnulmentStatus);
        // El bridge se sincronizo (exito), nunca se marco rechazo.
        _bcBridgeMock.Verify(b => b.OnArcaSucceededAsync(original.Id, 500, It.IsAny<CancellationToken>()), Times.Once);
        _bcBridgeMock.Verify(b => b.OnArcaFailedAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// F1 — candado del reviewer (2026-07-07): sobre una MISMA factura pueden convivir una NC PARCIAL PENDING
    /// (mismo TipoComprobante que la total) y una ND de multa PENDING (otro tipo), ambas con el mismo
    /// OriginalInvoiceId. El reuso idempotente de la anulacion TOTAL NO debe retomar ninguna de esas: la NC
    /// parcial se descarta por IdempotencyKey != null, la ND por TipoComprobante. La anulacion total debe crear
    /// SU PROPIA NC total nueva y no tocar los otros comprobantes pendientes.
    /// </summary>
    [Fact]
    public async Task ProcessAnnulmentJob_DoesNotReusePartialCreditNoteNorDebitNote()
    {
        await using var context = new AppDbContext(_dbOptions);
        var original = await SeedSaleInvoicePendingAnnulmentAsync(context); // Factura B (tipo 6) -> NC total tipo 8

        // NC PARCIAL PENDING para la misma factura: MISMO tipo (8) que la NC total, pero con huella de
        // idempotencia grabada (asi la marca ProcessPartialCreditNoteJob antes de POSTear). Debe EXCLUIRSE.
        context.Invoices.Add(new Invoice
        {
            Id = 550,
            ReservaId = original.ReservaId,
            OriginalInvoiceId = original.Id,
            TipoComprobante = 8, // NC B (igual que la total)
            Resultado = "PENDING",
            IdempotencyKey = "inv|partial-hash-abc",
            MonId = "PES",
            MonCotiz = 1m,
            ImporteTotal = 20_000m, // parcial (< total)
            CreatedAt = DateTime.UtcNow,
        });
        // ND de multa PENDING para la misma factura: OTRO tipo (7). Debe EXCLUIRSE por TipoComprobante.
        context.Invoices.Add(new Invoice
        {
            Id = 560,
            ReservaId = original.ReservaId,
            OriginalInvoiceId = original.Id,
            TipoComprobante = 7, // ND B
            Resultado = "PENDING",
            MonId = "PES",
            MonCotiz = 1m,
            ImporteTotal = 5_000m,
            CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        // La anulacion total crea SU NC total nueva (id 600, sin IdempotencyKey) y AFIP la aprueba.
        SetupAfipCreatePendingCreditNote(context, original.Id, creditNoteId: 600);
        SetupProcessInvoiceJobApproves(context);

        var service = BuildService(context);
        await service.ProcessAnnulmentJob(original.Id, "vendedor-A", approvalRequestId: null);

        // Creo su propia NC total (no retomo la parcial ni la ND): CreatePendingInvoice se llamo.
        _afipMock.Verify(a => a.CreatePendingInvoice(It.IsAny<int>(), It.IsAny<CreateInvoiceRequest>()), Times.Once);

        // La NC parcial y la ND siguen PENDING, intactas (no se procesaron como si fueran la total).
        var partial = await context.Invoices.AsNoTracking().FirstAsync(i => i.Id == 550);
        Assert.Equal("PENDING", partial.Resultado);
        var debitNote = await context.Invoices.AsNoTracking().FirstAsync(i => i.Id == 560);
        Assert.Equal("PENDING", debitNote.Resultado);

        // La NC total nueva (600) es la que se aprobo, y la factura original quedo anulada por ELLA.
        var totalNc = await context.Invoices.AsNoTracking().FirstAsync(i => i.Id == 600);
        Assert.Equal("A", totalNc.Resultado);
        var afterOriginal = await context.Invoices.AsNoTracking().FirstAsync(i => i.Id == original.Id);
        Assert.Equal(AnnulmentStatus.Succeeded, afterOriginal.AnnulmentStatus);
        _bcBridgeMock.Verify(b => b.OnArcaSucceededAsync(original.Id, 600, It.IsAny<CancellationToken>()), Times.Once);
    }

    // =========================================================================================
    // F2 — "AFIP no respondio" no es un rechazo
    // =========================================================================================

    /// <summary>
    /// F2: rechazo fiscal REAL de AFIP (Resultado="R"). Comportamiento intacto: marca Failed, avisa y notifica al
    /// BC que el ARCA rechazo (OnArcaFailedAsync).
    /// </summary>
    [Fact]
    public async Task ProcessAnnulmentJob_AfipRejects_MarksFailed_AndCallsOnArcaFailed()
    {
        await using var context = new AppDbContext(_dbOptions);
        var original = await SeedSaleInvoicePendingAnnulmentAsync(context);

        SetupAfipCreatePendingCreditNote(context, original.Id, creditNoteId: 501);
        _afipMock
            .Setup(a => a.ProcessInvoiceJob(It.IsAny<int>()))
            .Returns((int id) =>
            {
                var nc = context.Invoices.Single(i => i.Id == id);
                nc.Resultado = "R";
                nc.Observaciones = "CUIT del receptor inválido.";
                context.SaveChanges();
                return Task.CompletedTask;
            });

        var service = BuildService(context);

        // Rechazo real: el job NO re-tira (termina "manejado"), marca Failed.
        await service.ProcessAnnulmentJob(original.Id, "vendedor-A", approvalRequestId: null);

        var after = await context.Invoices.AsNoTracking().FirstAsync(i => i.Id == original.Id);
        Assert.Equal(AnnulmentStatus.Failed, after.AnnulmentStatus);

        _bcBridgeMock.Verify(
            b => b.OnArcaFailedAsync(original.Id, It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _bcBridgeMock.Verify(
            b => b.OnArcaSucceededAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);

        var notif = await context.Notifications.AsNoTracking().FirstOrDefaultAsync(n => n.RelatedEntityId == original.Id);
        Assert.NotNull(notif);
        // Voz de los avisos (2026-07-08): rechazo fiscal contado en negocio, con el número de reserva.
        Assert.Contains("AFIP no aceptó la anulación de la reserva", notif!.Message);
    }

    /// <summary>
    /// F2: "AFIP no respondio" (la NC queda PENDING sin throw). NO es un rechazo: NO se llama OnArcaFailedAsync
    /// (el BC debe seguir esperando la confirmacion fiscal) y el job re-tira para reintentar.
    /// </summary>
    [Fact]
    public async Task ProcessAnnulmentJob_AfipDidNotRespond_Pending_DoesNotCallOnArcaFailed_AndRetries()
    {
        await using var context = new AppDbContext(_dbOptions);
        var original = await SeedSaleInvoicePendingAnnulmentAsync(context);

        SetupAfipCreatePendingCreditNote(context, original.Id, creditNoteId: 502);
        SetupProcessInvoiceJobLeavesPending(context);

        var service = BuildService(context);

        var error = await Record.ExceptionAsync(() =>
            service.ProcessAnnulmentJob(original.Id, "vendedor-A", approvalRequestId: null));
        Assert.NotNull(error); // re-tira -> Hangfire reintenta

        // El BC NUNCA se marca rechazado por un problema de red.
        _bcBridgeMock.Verify(
            b => b.OnArcaFailedAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _bcBridgeMock.Verify(
            b => b.OnArcaSucceededAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // El aviso al usuario es de negocio (no filtra estado tecnico interno).
        var notif = await context.Notifications.AsNoTracking().FirstOrDefaultAsync(n => n.RelatedEntityId == original.Id);
        Assert.NotNull(notif);
        Assert.Contains("AFIP no respondió", notif!.Message);
        Assert.Contains("La estamos reintentando por vos", notif.Message);
        Assert.DoesNotContain("PENDING", notif.Message);
    }

    /// <summary>
    /// F2 + data-exposure: cuando AFIP tira una excepcion tecnica con detalle crudo (el caso real fue el DNS caido,
    /// "Name or service not known (wsaahomo.afip.gov.ar:443)"), ese texto NO debe llegar al aviso del usuario. Se
    /// reemplaza por un mensaje generico de negocio y NO se marca el BC como rechazado.
    /// </summary>
    [Fact]
    public async Task ProcessAnnulmentJob_AfipThrowsTechnical_UserMessageHasNoHostname()
    {
        await using var context = new AppDbContext(_dbOptions);
        var original = await SeedSaleInvoicePendingAnnulmentAsync(context);

        SetupAfipCreatePendingCreditNote(context, original.Id, creditNoteId: 503);
        _afipMock
            .Setup(a => a.ProcessInvoiceJob(It.IsAny<int>()))
            .ThrowsAsync(new Exception("Name or service not known (wsaahomo.afip.gov.ar:443)"));

        var service = BuildService(context);

        var error = await Record.ExceptionAsync(() =>
            service.ProcessAnnulmentJob(original.Id, "vendedor-A", approvalRequestId: null));
        Assert.NotNull(error); // re-tira

        var after = await context.Invoices.AsNoTracking().FirstAsync(i => i.Id == original.Id);
        Assert.Equal(AnnulmentStatus.Failed, after.AnnulmentStatus);

        _bcBridgeMock.Verify(
            b => b.OnArcaFailedAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);

        var notif = await context.Notifications.AsNoTracking().FirstOrDefaultAsync(n => n.RelatedEntityId == original.Id);
        Assert.NotNull(notif);
        // El aviso es el copy generico de negocio; el hostname/host tecnico jamas aparece.
        Assert.Equal(
            "La anulación de la reserva F-ANNUL-0001 quedó en camino: AFIP no respondió en este momento. " +
            "La estamos reintentando por vos, no hace falta que hagas nada.",
            notif!.Message);
        Assert.DoesNotContain("wsaahomo", notif.Message);
        Assert.DoesNotContain("Name or service not known", notif.Message);
    }

    /// <summary>
    /// Data-exposure (gate 2026-07-08): rama "AFIP RECHAZADO" del catch (rechazo fiscal permanente que llega como
    /// EXCEPCION, no como Resultado="R" — distinto camino del test de arriba). Si el detalle que trae la excepcion
    /// es tecnico (XML/SOAP de ARCA), el saneador lo tiene que tapar: el aviso al usuario nunca debe mostrar ese
    /// crudo, solo el copy de negocio.
    /// </summary>
    [Fact]
    public async Task ProcessAnnulmentJob_AfipRechazadoException_ConDetalleTecnico_NoFiltraElXml()
    {
        await using var context = new AppDbContext(_dbOptions);
        var original = await SeedSaleInvoicePendingAnnulmentAsync(context);

        SetupAfipCreatePendingCreditNote(context, original.Id, creditNoteId: 505);
        _afipMock
            .Setup(a => a.ProcessInvoiceJob(It.IsAny<int>()))
            .ThrowsAsync(new Exception(
                "AFIP RECHAZADO: <soap:Fault><faultstring>Object reference not set to an instance of an object.</faultstring></soap:Fault>"));

        var service = BuildService(context);

        // Rechazo permanente: el job NO re-tira (termina "manejado" con return, no throw).
        var error = await Record.ExceptionAsync(() =>
            service.ProcessAnnulmentJob(original.Id, "vendedor-A", approvalRequestId: null));
        Assert.Null(error);

        var notif = await context.Notifications.AsNoTracking().FirstOrDefaultAsync(n => n.RelatedEntityId == original.Id);
        Assert.NotNull(notif);
        Assert.Contains("AFIP rechazó la anulación de la reserva F-ANNUL-0001", notif!.Message);
        // El XML/SOAP tecnico NUNCA llega al usuario: el saneador lo reemplaza por el copy generico.
        Assert.DoesNotContain("<soap", notif.Message);
        Assert.DoesNotContain("faultstring", notif.Message);
        Assert.DoesNotContain("Object reference", notif.Message);
    }

    /// <summary>
    /// Espejo del test de arriba: si el detalle de la excepcion "AFIP RECHAZADO" es texto de negocio legible (no
    /// XML), el saneador lo deja pasar tal cual — es informacion util para el vendedor.
    /// </summary>
    [Fact]
    public async Task ProcessAnnulmentJob_AfipRechazadoException_ConMotivoDeNegocio_SeConserva()
    {
        await using var context = new AppDbContext(_dbOptions);
        var original = await SeedSaleInvoicePendingAnnulmentAsync(context);

        SetupAfipCreatePendingCreditNote(context, original.Id, creditNoteId: 506);
        _afipMock
            .Setup(a => a.ProcessInvoiceJob(It.IsAny<int>()))
            .ThrowsAsync(new Exception("AFIP RECHAZADO: CUIT del emisor sin habilitación"));

        var service = BuildService(context);

        var error = await Record.ExceptionAsync(() =>
            service.ProcessAnnulmentJob(original.Id, "vendedor-A", approvalRequestId: null));
        Assert.Null(error);

        var notif = await context.Notifications.AsNoTracking().FirstOrDefaultAsync(n => n.RelatedEntityId == original.Id);
        Assert.NotNull(notif);
        Assert.Contains("CUIT del emisor sin habilitación", notif!.Message);
    }

    // =========================================================================================
    // F3 — el catch remedia sin reventar
    // =========================================================================================

    /// <summary>
    /// F3: tras una excepcion tecnica, el catch (que empieza limpiando el ChangeTracker) igual marca la factura
    /// como Failed y crea el aviso, sin reventar. NOTA: EF InMemory no aplica el unique index de Postgres, asi que
    /// aca NO se reproduce el 23505 que envenenaba el contexto en produccion; verificamos que el camino de
    /// remediacion del catch corre correctamente. El escenario real del 23505 queda cubierto por el fix F1 (no
    /// se re-emite) + smoke en Postgres.
    /// </summary>
    [Fact]
    public async Task ProcessAnnulmentJob_OnTechnicalError_CatchRemediates_WithoutThrowingFromRemediation()
    {
        await using var context = new AppDbContext(_dbOptions);
        var original = await SeedSaleInvoicePendingAnnulmentAsync(context);

        SetupAfipCreatePendingCreditNote(context, original.Id, creditNoteId: 504);
        _afipMock
            .Setup(a => a.ProcessInvoiceJob(It.IsAny<int>()))
            .ThrowsAsync(new Exception("fallo tecnico simulado"));

        var service = BuildService(context);

        // El job re-tira la excepcion ORIGINAL (para Hangfire), pero la remediacion del catch (Failed + aviso)
        // NO debe tirar por su cuenta.
        var error = await Record.ExceptionAsync(() =>
            service.ProcessAnnulmentJob(original.Id, "vendedor-A", approvalRequestId: null));
        Assert.NotNull(error);

        var after = await context.Invoices.AsNoTracking().FirstAsync(i => i.Id == original.Id);
        Assert.Equal(AnnulmentStatus.Failed, after.AnnulmentStatus);

        var notif = await context.Notifications.AsNoTracking().FirstOrDefaultAsync(n => n.RelatedEntityId == original.Id);
        Assert.NotNull(notif);
        Assert.Equal("Error", notif!.Type);
    }
}
