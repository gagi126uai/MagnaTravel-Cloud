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
using TravelApi.Application.DTOs.Cancellation;
using TravelApi.Application.Exceptions;
using TravelApi.Application.Interfaces;
using TravelApi.Application.Mappings;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Cancellation.Integration;

/// <summary>
/// FC1.3 Fase 2 Etapa 5 (plan tactico §FC1.3.F2.2, 2026-05-27): tests de integracion del
/// CUERPO REAL de <c>InvoiceService.ProcessPartialCreditNoteJob</c> — el job que emite la
/// NC parcial al ARCA con idempotencia anti-doble-POST.
///
/// <para><b>Como se aisla ARCA</b>: <see cref="IAfipService"/> se mockea. El SOAP real no
/// corre. <c>CreatePendingInvoice</c> del mock PERSISTE una NC real en la BD del fixture
/// (para que <c>ReloadAsync</c> y las queries del job funcionen) y devuelve esa fila. Asi
/// verificamos el contrato del job (request armado, persistencia, idempotencia) sin pegarle
/// a AFIP. Los metodos de consulta (<c>GetLastAuthorizedNumeroAsync</c>,
/// <c>QueryLastAuthorizedWithDetailsAsync</c>) tambien se mockean por test.</para>
///
/// <para><b>Postgres real (no InMemory)</b>: la idempotencia depende del indice UNIQUE de
/// <c>ArcaIdempotencyKeys</c> (que InMemory ignora). Estos tests COMPILAN local (sin Docker)
/// y CORREN en el VPS, como el resto del modulo FC1.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class ProcessPartialCreditNoteJobIntegrationTests
    : IClassFixture<PostgresIntegrationFixture>, IAsyncLifetime
{
    private readonly PostgresIntegrationFixture _fixture;

    private const decimal Tolerance = 0.01m;
    private const int StaleThresholdMinutes = 10;

    public ProcessPartialCreditNoteJobIntegrationTests(PostgresIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => _fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    // =========================================================================
    // Construccion del servicio + mocks.
    // =========================================================================

    /// <summary>
    /// Resultado que el mock de <c>CreatePendingInvoice</c> + <c>ProcessInvoiceJob</c>
    /// simula para ARCA: "A" (aprobado) o "R" (rechazado).
    /// </summary>
    private enum ArcaEmissionResult { Approved, Rejected }

    private sealed class ServiceBundle
    {
        // Service se setea despues de crear el bundle (los callbacks de Moq capturan el
        // bundle, asi que el bundle tiene que existir antes que el service). Por eso es
        // set, no init.
        public InvoiceService Service { get; set; } = null!;
        public required Mock<IAfipService> AfipMock { get; init; }
        public required Mock<IApprovalRequestService> ApprovalMock { get; init; }
        public CreateInvoiceRequest? CapturedRequest { get; set; }
        public int CreatePendingInvoiceCallCount { get; set; }
    }

    /// <summary>
    /// Construye el servicio con AFIP mockeado. <paramref name="emissionResult"/> controla
    /// que devuelve la "emision" simulada. <paramref name="lastAuthorizedNumero"/> es el
    /// snapshot del numerador que devuelve <c>GetLastAuthorizedNumeroAsync</c>.
    /// <paramref name="recoveryResult"/>, si no es null, es lo que devuelve
    /// <c>QueryLastAuthorizedWithDetailsAsync</c> (usado por los tests de stale key recovery).
    /// </summary>
    private ServiceBundle BuildService(
        AppDbContext context,
        ArcaEmissionResult emissionResult = ArcaEmissionResult.Approved,
        int lastAuthorizedNumero = 5000,
        ArcaCompoundQueryResult? recoveryResult = null,
        bool enableRealEmission = true,
        IvaProrrateoMode prorrateoMode = IvaProrrateoMode.ProportionalToNet)
    {
        var mapper = new MapperConfiguration(c => c.AddProfile<MappingProfile>()).CreateMapper();

        var settingsMock = new Mock<IOperationalFinanceSettingsService>();
        settingsMock
            .Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings
            {
                EnablePartialCreditNoteRealEmission = enableRealEmission,
                PartialCreditNoteRoundingTolerance = Tolerance,
                IdempotencyKeyStaleThresholdMinutes = StaleThresholdMinutes,
                IvaProrrateoMode = prorrateoMode,
            });

        var afipMock = new Mock<IAfipService>();
        var approvalMock = new Mock<IApprovalRequestService>();
        approvalMock
            .Setup(a => a.MarkConsumedAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var bundle = new ServiceBundle
        {
            Service = null!, // se setea abajo (necesitamos el bundle en los callbacks).
            AfipMock = afipMock,
            ApprovalMock = approvalMock,
        };

        afipMock
            .Setup(a => a.GetLastAuthorizedNumeroAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(lastAuthorizedNumero);

        if (recoveryResult is not null)
        {
            afipMock
                .Setup(a => a.QueryLastAuthorizedWithDetailsAsync(
                    It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(recoveryResult);
        }

        // CreatePendingInvoice: persiste una NC real en la BD del fixture y la devuelve.
        // Capturamos el request para los asserts. Cada llamada crea su propio context para
        // no pelear con el ChangeTracker del context del job.
        afipMock
            .Setup(a => a.CreatePendingInvoice(It.IsAny<int>(), It.IsAny<CreateInvoiceRequest>()))
            .ReturnsAsync((int reservaId, CreateInvoiceRequest request) =>
            {
                bundle.CapturedRequest = request;
                bundle.CreatePendingInvoiceCallCount++;

                // Resolver el Id interno de la factura origen desde el PublicId del request.
                int? originalId = null;
                if (Guid.TryParse(request.OriginalInvoiceId, out var publicId))
                {
                    using var lookupCtx = _fixture.CreateDbContext();
                    originalId = lookupCtx.Invoices
                        .Where(i => i.PublicId == publicId)
                        .Select(i => (int?)i.Id)
                        .FirstOrDefault();
                }

                using var ctx = _fixture.CreateDbContext();
                var nc = new Invoice
                {
                    TipoComprobante = request.CbteTipo,
                    PuntoDeVenta = 1,
                    NumeroComprobante = 0, // PENDING todavia no tiene numero asignado.
                    Resultado = "PENDING",
                    ImporteTotal = request.Items.Sum(i => i.Total),
                    ImporteNeto = request.Items.Sum(i => i.Total),
                    ImporteIva = 0m,
                    OriginalInvoiceId = originalId,
                    CreatedAt = DateTime.UtcNow,
                };
                ctx.Invoices.Add(nc);
                ctx.SaveChanges();
                return nc;
            });

        // ProcessInvoiceJob: simula la respuesta de ARCA actualizando la NC recien creada.
        afipMock
            .Setup(a => a.ProcessInvoiceJob(It.IsAny<int>()))
            .Returns((int invoiceId) =>
            {
                using var ctx = _fixture.CreateDbContext();
                var nc = ctx.Invoices.First(i => i.Id == invoiceId);
                if (emissionResult == ArcaEmissionResult.Approved)
                {
                    nc.Resultado = "A";
                    nc.CAE = "70000000000001";
                    nc.NumeroComprobante = lastAuthorizedNumero + 1;
                    nc.IssuedAt = DateTime.UtcNow;
                    nc.Observaciones = null;
                }
                else
                {
                    nc.Resultado = "R";
                    nc.Observaciones = "ARCA rechazo: comprobante asociado inexistente (test).";
                }
                ctx.SaveChanges();
                return Task.CompletedTask;
            });

        bundle.Service = new InvoiceService(
            context,
            new EntityReferenceResolver(context),
            afipMock.Object,
            new Mock<IInvoicePdfService>().Object,
            mapper,
            new Mock<IBackgroundJobClient>().Object,
            NullLogger<InvoiceService>.Instance,
            settingsMock.Object,
            BuildUserManager(),
            approvalService: approvalMock.Object);

        return bundle;
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

    // =========================================================================
    // Seed helpers.
    // =========================================================================

    /// <summary>
    /// Persiste la factura origen (Factura B por default) con AnnulmentStatus=Pending
    /// (estado en que la deja EnqueuePartialCreditNoteAsync antes de encolar el job).
    ///
    /// <para>Tambien crea un Customer + Reserva minimos y vincula la factura a la reserva:
    /// el job exige <c>Invoice.ReservaId</c> no nulo (la NC necesita una reserva asociada,
    /// igual que la NC total). El mock de <c>CreatePendingInvoice</c> NO resuelve el cliente
    /// real, asi que con que la fila Reserva exista alcanza para pasar el guard + la FK.</para>
    /// </summary>
    private async Task<(int Id, long NumeroComprobante)> SeedOriginalInvoiceAsync(
        int tipoComprobante = 6,
        long numeroComprobante = 1001)
    {
        await using var ctx = _fixture.CreateDbContext();

        var customer = new Customer
        {
            FullName = "Cliente Test",
            TaxCondition = "Consumidor Final",
            IsActive = true,
        };
        ctx.Customers.Add(customer);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = $"F-PCN-{Guid.NewGuid().ToString("N")[..8]}",
            Name = "Reserva NC parcial test",
            Status = EstadoReserva.Confirmed,
            PayerId = customer.Id,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var invoice = new Invoice
        {
            TipoComprobante = tipoComprobante,
            PuntoDeVenta = 1,
            NumeroComprobante = numeroComprobante,
            CAE = "12345678901234",
            Resultado = "A",
            ImporteTotal = 1_000_000m,
            ImporteNeto = 826_446m,
            ImporteIva = 173_554m,
            AnnulmentStatus = AnnulmentStatus.Pending,
            ReservaId = reserva.Id,
            CreatedAt = DateTime.UtcNow,
        };
        ctx.Invoices.Add(invoice);
        await ctx.SaveChangesAsync();
        return (invoice.Id, invoice.NumeroComprobante);
    }

    /// <summary>
    /// Seed de un ApplicationUser (FK de notificaciones / trazabilidad).
    /// </summary>
    private async Task SeedUserAsync(string userId)
    {
        await using var ctx = _fixture.CreateDbContext();
        if (await ctx.Users.AnyAsync(u => u.Id == userId)) return;
        ctx.Users.Add(new ApplicationUser
        {
            Id = userId,
            UserName = userId,
            NormalizedUserName = userId.ToUpperInvariant(),
            Email = $"{userId}@test.local",
            NormalizedEmail = $"{userId.ToUpperInvariant()}@TEST.LOCAL",
            FullName = "Vendedor Test",
            IsActive = true,
            SecurityStamp = Guid.NewGuid().ToString(),
        });
        await ctx.SaveChangesAsync();
    }

    /// <summary>
    /// Seed de una key huerfana (CreatedAt viejo + ResolvedAt null) para los tests de
    /// stale key recovery. Devuelve la key calculada igual que el job.
    /// </summary>
    private async Task<string> SeedOrphanKeyAsync(
        int originalInvoiceId,
        int approvalRequestId,
        decimal fiscalAmountToCredit,
        string currency,
        int ageMinutes,
        int? lastSeenNumeroBeforePost)
    {
        string key = ComputeIdempotencyKey(originalInvoiceId, approvalRequestId, fiscalAmountToCredit, currency);
        await using var ctx = _fixture.CreateDbContext();
        ctx.ArcaIdempotencyKeys.Add(new ArcaIdempotencyKey
        {
            Key = key,
            CreatedAt = DateTime.UtcNow.AddMinutes(-ageMinutes),
            ResolvedAt = null,
            LastSeenNumeroBeforePost = lastSeenNumeroBeforePost,
        });
        await ctx.SaveChangesAsync();
        return key;
    }

    /// <summary>
    /// Replica EXACTA del calculo de idemKey del job (SHA256 hex de
    /// "{id}|{approval}|{monto:F2}|{moneda}"). Si el formato del job cambia, este helper
    /// tiene que cambiar a la par — por eso se documenta el acoplamiento.
    /// </summary>
    private static string ComputeIdempotencyKey(
        int originalInvoiceId, int approvalRequestId, decimal fiscalAmountToCredit, string currency)
    {
        string raw = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{originalInvoiceId}|{approvalRequestId}|{fiscalAmountToCredit:F2}|{currency}");
        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string BuildLiquidationJson(
        decimal fiscalAmountToCredit = 300_000m,
        decimal lineTotal = 300_000m,
        int alicuotaIvaId = 5,
        string currency = "ARS")
    {
        var input = new PartialCreditNoteEmissionInput(
            OriginalNetAmount: 826_446m,
            OriginalVatAmount: 173_554m,
            OriginalTotalAmount: 1_000_000m,
            FiscalAmountToCredit: fiscalAmountToCredit,
            Currency: currency,
            ExchangeRateAtOriginalInvoice: 1m,
            Lines: new List<PartialCreditNoteLineDto>
            {
                new(Description: "Hotel - parte cancelada", Quantity: 1m, UnitPrice: lineTotal, Total: lineTotal, AlicuotaIvaId: alicuotaIvaId),
            });
        return System.Text.Json.JsonSerializer.Serialize(input);
    }

    // =========================================================================
    // Tests.
    // =========================================================================

    /// <summary>
    /// Camino feliz: el job arma el CreateInvoiceRequest correcto (lineas, OriginalInvoiceId,
    /// IsCreditNote=true, NC tipo 8 para Factura B), emite, y la factura origen queda
    /// Succeeded con la key resuelta.
    /// </summary>
    [Fact]
    public async Task ProcessPartialCreditNoteJob_BuildsCorrectInvoiceRequest()
    {
        var (originalId, _) = await SeedOriginalInvoiceAsync();
        await SeedUserAsync("vendedor-X");

        await using var ctx = _fixture.CreateDbContext();
        var bundle = BuildService(ctx);

        await bundle.Service.ProcessPartialCreditNoteJob(
            originalInvoiceId: originalId,
            liquidationJson: BuildLiquidationJson(),
            userId: "vendedor-X",
            approvalRequestId: 0);

        // El request armado refleja la liquidacion.
        Assert.NotNull(bundle.CapturedRequest);
        var req = bundle.CapturedRequest!;
        Assert.True(req.IsCreditNote);
        Assert.False(req.IsDebitNote);
        Assert.Equal(8, req.CbteTipo); // Factura B (6) -> NC B (8).
        Assert.Single(req.Items);
        Assert.Equal(300_000m, req.Items[0].Total);
        Assert.Equal(5, req.Items[0].AlicuotaIvaId);
        Assert.Empty(req.Tributes); // Fase 2 no prorratea tributos provinciales.

        // La factura origen quedo anulada (NC aprobada por ARCA).
        await using var verifyCtx = _fixture.CreateDbContext();
        var refreshed = await verifyCtx.Invoices.AsNoTracking().FirstAsync(i => i.Id == originalId);
        Assert.Equal(AnnulmentStatus.Succeeded, refreshed.AnnulmentStatus);
        Assert.NotNull(refreshed.AnnulledAt);

        // La key de idempotencia quedo resuelta.
        var key = await verifyCtx.ArcaIdempotencyKeys.AsNoTracking().SingleAsync();
        Assert.NotNull(key.ResolvedAt);

        // Se emitio exactamente una vez.
        Assert.Equal(1, bundle.CreatePendingInvoiceCallCount);
    }

    /// <summary>
    /// Cuadre ARCA: si el comprobante no cuadra (la liquidacion no cierra contra
    /// FiscalAmountToCredit), el job marca Failed SIN llamar a ARCA.
    /// </summary>
    [Fact]
    public async Task ProcessPartialCreditNoteJob_SumMismatch_FailsBeforeArca()
    {
        var (originalId, _) = await SeedOriginalInvoiceAsync();
        await SeedUserAsync("vendedor-X");

        await using var ctx = _fixture.CreateDbContext();
        var bundle = BuildService(ctx);

        // FiscalAmountToCredit=100.000 pero la linea suma 99.999,50 (gap > tolerancia).
        var badJson = BuildLiquidationJson(fiscalAmountToCredit: 100_000m, lineTotal: 99_999.50m);

        await bundle.Service.ProcessPartialCreditNoteJob(
            originalInvoiceId: originalId,
            liquidationJson: badJson,
            userId: "vendedor-X",
            approvalRequestId: 0);

        // La factura origen quedo Failed.
        await using var verifyCtx = _fixture.CreateDbContext();
        var refreshed = await verifyCtx.Invoices.AsNoTracking().FirstAsync(i => i.Id == originalId);
        Assert.Equal(AnnulmentStatus.Failed, refreshed.AnnulmentStatus);

        // NO se llamo a ARCA para emitir.
        bundle.AfipMock.Verify(
            a => a.CreatePendingInvoice(It.IsAny<int>(), It.IsAny<CreateInvoiceRequest>()),
            Times.Never);
    }

    /// <summary>
    /// RH-004: si Hangfire reintenta el job tras un timeout y la key sigue activa (reciente),
    /// el reintento NO emite un segundo comprobante. Aca simulamos el reintento corriendo el
    /// job una segunda vez con la misma idemKey ya insertada y reciente.
    /// </summary>
    [Fact]
    public async Task ProcessPartialCreditNoteJob_HangfireRetryAfterTimeout_DoesNotEmitDuplicate()
    {
        var (originalId, _) = await SeedOriginalInvoiceAsync();
        await SeedUserAsync("vendedor-X");

        // Primera corrida: emite normal.
        await using (var ctx1 = _fixture.CreateDbContext())
        {
            var bundle1 = BuildService(ctx1);
            await bundle1.Service.ProcessPartialCreditNoteJob(
                originalInvoiceId: originalId,
                liquidationJson: BuildLiquidationJson(),
                userId: "vendedor-X",
                approvalRequestId: 0);
            Assert.Equal(1, bundle1.CreatePendingInvoiceCallCount);
        }

        // La key quedo Resolved tras la primera corrida exitosa. Un reintento de Hangfire
        // detecta la key resuelta -> duplicado -> NO re-emite.
        await using var ctx2 = _fixture.CreateDbContext();
        var bundle2 = BuildService(ctx2);
        await bundle2.Service.ProcessPartialCreditNoteJob(
            originalInvoiceId: originalId,
            liquidationJson: BuildLiquidationJson(),
            userId: "vendedor-X",
            approvalRequestId: 0);

        // El reintento NO emitio un segundo comprobante.
        Assert.Equal(0, bundle2.CreatePendingInvoiceCallCount);
    }

    /// <summary>
    /// RH-004: una key ACTIVA reciente (otro intento en vuelo) aborta limpio sin re-emitir.
    /// </summary>
    [Fact]
    public async Task ProcessPartialCreditNoteJob_IdempotencyKey_UniqueViolation_AbortsCleanly()
    {
        var (originalId, _) = await SeedOriginalInvoiceAsync();
        await SeedUserAsync("vendedor-X");

        // Seed de una key RECIENTE (age 1 min, < umbral 10) sin resolver: simula otro intento
        // que ya inserto la key y esta procesando.
        await SeedOrphanKeyAsync(
            originalInvoiceId: originalId,
            approvalRequestId: 0,
            fiscalAmountToCredit: 300_000m,
            currency: "ARS",
            ageMinutes: 1,
            lastSeenNumeroBeforePost: 5000);

        await using var ctx = _fixture.CreateDbContext();
        var bundle = BuildService(ctx);

        await bundle.Service.ProcessPartialCreditNoteJob(
            originalInvoiceId: originalId,
            liquidationJson: BuildLiquidationJson(),
            userId: "vendedor-X",
            approvalRequestId: 0);

        // No re-emitio: la key reciente bloqueo.
        Assert.Equal(0, bundle.CreatePendingInvoiceCallCount);

        // La key sigue sin resolver (el otro intento la resuelve).
        await using var verifyCtx = _fixture.CreateDbContext();
        var key = await verifyCtx.ArcaIdempotencyKeys.AsNoTracking().SingleAsync();
        Assert.Null(key.ResolvedAt);
    }

    /// <summary>
    /// RH2-004: una key huerfana (vieja + sin resolver) con ARCA que confirma que el POST
    /// nunca viajo (Found=false) se borra y permite reintento limpio: el job emite 1 vez.
    /// </summary>
    [Fact]
    public async Task ProcessPartialCreditNoteJob_KeyOrphanedAfterCrash_AllowsRetryAfterStaleThreshold()
    {
        var (originalId, _) = await SeedOriginalInvoiceAsync();
        await SeedUserAsync("vendedor-X");

        await SeedOrphanKeyAsync(
            originalInvoiceId: originalId,
            approvalRequestId: 0,
            fiscalAmountToCredit: 300_000m,
            currency: "ARS",
            ageMinutes: 15, // > umbral 10 -> huerfana.
            lastSeenNumeroBeforePost: 5000);

        // ARCA: el numerador NO avanzo -> POST nunca viajo.
        var recovery = new ArcaCompoundQueryResult(
            Found: false, LastNumero: 5000, Cae: null, CbteAsoc: null,
            IssuedAt: null, ImporteTotal: null, MonId: null, MonCotiz: null);

        await using var ctx = _fixture.CreateDbContext();
        var bundle = BuildService(ctx, recoveryResult: recovery);

        await bundle.Service.ProcessPartialCreditNoteJob(
            originalInvoiceId: originalId,
            liquidationJson: BuildLiquidationJson(),
            userId: "vendedor-X",
            approvalRequestId: 0);

        // Se emitio exactamente 1 vez (retry limpio tras borrar la huerfana).
        Assert.Equal(1, bundle.CreatePendingInvoiceCallCount);

        // Al final hay 1 key (la nueva del retry), resuelta tras la emision exitosa.
        await using var verifyCtx = _fixture.CreateDbContext();
        var key = await verifyCtx.ArcaIdempotencyKeys.AsNoTracking().SingleAsync();
        Assert.NotNull(key.ResolvedAt);
    }

    /// <summary>
    /// A.6 (Found=false): mismo que el anterior pero verificando explicitamente el caso
    /// "POST nunca viajo" (numerador no avanzo). Borra key + retry + 1 sola emision.
    /// </summary>
    [Fact]
    public async Task ProcessPartialCreditNoteJob_KeyOrphanedPostNeverArrived_DeletesKeyAndRetries()
    {
        var (originalId, _) = await SeedOriginalInvoiceAsync();
        await SeedUserAsync("vendedor-X");

        await SeedOrphanKeyAsync(
            originalInvoiceId: originalId, approvalRequestId: 0,
            fiscalAmountToCredit: 300_000m, currency: "ARS",
            ageMinutes: 15, lastSeenNumeroBeforePost: 1234);

        var recovery = new ArcaCompoundQueryResult(
            Found: false, LastNumero: 1234, Cae: null, CbteAsoc: null,
            IssuedAt: null, ImporteTotal: null, MonId: null, MonCotiz: null);

        await using var ctx = _fixture.CreateDbContext();
        var bundle = BuildService(ctx, recoveryResult: recovery);

        await bundle.Service.ProcessPartialCreditNoteJob(
            originalInvoiceId: originalId,
            liquidationJson: BuildLiquidationJson(),
            userId: "vendedor-X",
            approvalRequestId: 0);

        Assert.Equal(1, bundle.CreatePendingInvoiceCallCount);
    }

    /// <summary>
    /// A.6 (Caso A): key huerfana + ARCA confirma que el comprobante SI se emitio y matchea
    /// nuestra factura origen (CbteAsoc == NumeroComprobante) + monto. Deriva el CAE, resuelve
    /// la key y NO re-POSTea.
    /// </summary>
    [Fact]
    public async Task ProcessPartialCreditNoteJob_KeyOrphanedPostArrived_RecoversFromArca()
    {
        var (originalId, numero) = await SeedOriginalInvoiceAsync();
        await SeedUserAsync("vendedor-X");

        await SeedOrphanKeyAsync(
            originalInvoiceId: originalId, approvalRequestId: 0,
            fiscalAmountToCredit: 300_000m, currency: "ARS",
            ageMinutes: 15, lastSeenNumeroBeforePost: 1234);

        // ARCA: el numerador avanzo y el comprobante asociado apunta a NUESTRA factura origen
        // (por NumeroComprobante) con el monto esperado.
        var recovery = new ArcaCompoundQueryResult(
            Found: true,
            LastNumero: 1235,
            Cae: "70000000000099",
            CbteAsoc: (int)numero, // CbteAsoc es el NUMERO de comprobante, no el Id.
            IssuedAt: DateTime.UtcNow,
            ImporteTotal: 300_000m,
            MonId: "PES",
            MonCotiz: 1m);

        await using var ctx = _fixture.CreateDbContext();
        var bundle = BuildService(ctx, recoveryResult: recovery);

        await bundle.Service.ProcessPartialCreditNoteJob(
            originalInvoiceId: originalId,
            liquidationJson: BuildLiquidationJson(),
            userId: "vendedor-X",
            approvalRequestId: 0);

        // NO re-emitio (derivo el CAE del comprobante ya emitido).
        Assert.Equal(0, bundle.CreatePendingInvoiceCallCount);

        await using var verifyCtx = _fixture.CreateDbContext();
        // La key quedo resuelta.
        var key = await verifyCtx.ArcaIdempotencyKeys.AsNoTracking().SingleAsync();
        Assert.NotNull(key.ResolvedAt);
        // La factura origen quedo anulada.
        var refreshed = await verifyCtx.Invoices.AsNoTracking().FirstAsync(i => i.Id == originalId);
        Assert.Equal(AnnulmentStatus.Succeeded, refreshed.AnnulmentStatus);
    }

    /// <summary>
    /// A.6 (mismatch de monto): key huerfana + ARCA encontro un comprobante que apunta a
    /// nuestra factura pero con OTRO monto (otro proceso ocupo el numerador). Se trata como
    /// caso B -> borra key + retry limpio.
    /// </summary>
    [Fact]
    public async Task ProcessPartialCreditNoteJob_KeyOrphanedMismatchAmount_TreatsAsPostNeverArrived()
    {
        var (originalId, numero) = await SeedOriginalInvoiceAsync();
        await SeedUserAsync("vendedor-X");

        await SeedOrphanKeyAsync(
            originalInvoiceId: originalId, approvalRequestId: 0,
            fiscalAmountToCredit: 300_000m, currency: "ARS",
            ageMinutes: 15, lastSeenNumeroBeforePost: 1234);

        // ARCA: comprobante encontrado apunta a nuestra factura pero con monto distinto.
        var recovery = new ArcaCompoundQueryResult(
            Found: true,
            LastNumero: 1235,
            Cae: "70000000000077",
            CbteAsoc: (int)numero,
            IssuedAt: DateTime.UtcNow,
            ImporteTotal: 555_555m, // != 300.000 esperado.
            MonId: "PES",
            MonCotiz: 1m);

        await using var ctx = _fixture.CreateDbContext();
        var bundle = BuildService(ctx, recoveryResult: recovery);

        await bundle.Service.ProcessPartialCreditNoteJob(
            originalInvoiceId: originalId,
            liquidationJson: BuildLiquidationJson(),
            userId: "vendedor-X",
            approvalRequestId: 0);

        // Tratado como caso B -> retry limpio -> emite 1 vez.
        Assert.Equal(1, bundle.CreatePendingInvoiceCallCount);
    }

    /// <summary>
    /// RH4-001: el snapshot del numerador se captura DENTRO del job (no en el encolado).
    /// Verificamos que <c>GetLastAuthorizedNumeroAsync</c> se invoco exactamente 1 vez y que
    /// el <c>LastSeenNumeroBeforePost</c> persistido es el que devolvio esa invocacion.
    /// </summary>
    [Fact]
    public async Task ProcessPartialCreditNoteJob_SnapshotCapturedAtJobStartNotAtEnqueueTime()
    {
        var (originalId, _) = await SeedOriginalInvoiceAsync();
        await SeedUserAsync("vendedor-X");

        const int snapshot = 4242;

        await using var ctx = _fixture.CreateDbContext();
        var bundle = BuildService(ctx, lastAuthorizedNumero: snapshot);

        await bundle.Service.ProcessPartialCreditNoteJob(
            originalInvoiceId: originalId,
            liquidationJson: BuildLiquidationJson(),
            userId: "vendedor-X",
            approvalRequestId: 0);

        // El snapshot se pidio una sola vez (dentro del job).
        bundle.AfipMock.Verify(
            a => a.GetLastAuthorizedNumeroAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Once);

        // El LastSeenNumeroBeforePost persistido en la key coincide con ese snapshot.
        await using var verifyCtx = _fixture.CreateDbContext();
        var key = await verifyCtx.ArcaIdempotencyKeys.AsNoTracking().SingleAsync();
        Assert.Equal(snapshot, key.LastSeenNumeroBeforePost);
    }

    /// <summary>
    /// ARCA rechaza (Resultado "R"): la factura origen queda Failed y la key se resuelve
    /// (intento terminal).
    /// </summary>
    [Fact]
    public async Task ProcessPartialCreditNoteJob_ArcaRejects_MarksFailed()
    {
        var (originalId, _) = await SeedOriginalInvoiceAsync();
        await SeedUserAsync("vendedor-X");

        await using var ctx = _fixture.CreateDbContext();
        var bundle = BuildService(ctx, emissionResult: ArcaEmissionResult.Rejected);

        await bundle.Service.ProcessPartialCreditNoteJob(
            originalInvoiceId: originalId,
            liquidationJson: BuildLiquidationJson(),
            userId: "vendedor-X",
            approvalRequestId: 0);

        await using var verifyCtx = _fixture.CreateDbContext();
        var refreshed = await verifyCtx.Invoices.AsNoTracking().FirstAsync(i => i.Id == originalId);
        Assert.Equal(AnnulmentStatus.Failed, refreshed.AnnulmentStatus);

        var key = await verifyCtx.ArcaIdempotencyKeys.AsNoTracking().SingleAsync();
        Assert.NotNull(key.ResolvedAt);
    }
}
