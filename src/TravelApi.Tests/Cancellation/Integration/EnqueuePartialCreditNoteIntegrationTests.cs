using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.DTOs.Cancellation;
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
/// FC1.3 Fase 2 Etapa 4 (plan tactico §FC1.3.F2.2, 2026-05-27): tests de
/// integracion de <c>InvoiceService.EnqueuePartialCreditNoteAsync</c>.
///
/// <para><b>Que cubren</b>: el PUNTO DE ENTRADA de la NC parcial — valida los montos,
/// marca la factura origen como anulacion en curso (AnnulmentStatus = Pending) y
/// encola el job Hangfire. NO cubren la emision real al ARCA (eso es la Etapa 5;
/// el job <c>ProcessPartialCreditNoteJob</c> hoy es un esqueleto que lanza
/// NotImplementedException).</para>
///
/// <para><b>Por que Postgres real (no InMemory)</b>: persistimos la mutacion de
/// <c>AnnulmentStatus</c> contra una BD real para validar el contrato de persistencia
/// del modulo FC1 igual que el resto de los tests de integracion. Estos tests
/// COMPILAN local (sin Docker) y CORREN en el VPS, como el resto del modulo.</para>
///
/// <para><b>Como verificamos el encolado</b>: <see cref="IBackgroundJobClient"/> no
/// acepta un Moq trivial de su metodo de extension <c>Enqueue&lt;T&gt;</c>, asi que
/// verificamos la API publica subyacente <c>Create(Job, EnqueuedState)</c> — mismo
/// patron que los tests de la NC total en <c>InvoiceServiceFilteringAndAnnulmentTests</c>.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class EnqueuePartialCreditNoteIntegrationTests
    : IClassFixture<PostgresIntegrationFixture>, IAsyncLifetime
{
    private readonly PostgresIntegrationFixture _fixture;

    // Tolerancia que devuelve el settings mock. Igual al default de la entidad (0.01)
    // para que los tests reflejen el comportamiento real de produccion.
    private const decimal Tolerance = 0.01m;

    public EnqueuePartialCreditNoteIntegrationTests(PostgresIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => _fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    // =========================================================================
    // Helpers de construccion del servicio + seed.
    // =========================================================================

    /// <summary>
    /// Construye un <see cref="InvoiceService"/> apuntando al <paramref name="context"/>
    /// del fixture Postgres, con todas las dependencias externas mockeadas. Devuelve
    /// tambien el mock del job client para que el test verifique (o no) el encolado.
    /// </summary>
    /// <param name="enableRealEmission">
    /// Valor del flag maestro de Fase 2 (<c>EnablePartialCreditNoteRealEmission</c>) que
    /// devuelve el settings mock. Default <c>true</c> (escenario operativo de Fase 2). El
    /// test del guard lo pasa en <c>false</c> para verificar que el metodo rebota.
    /// </param>
    private (InvoiceService Service, Mock<IBackgroundJobClient> JobClientMock) BuildService(
        AppDbContext context,
        bool enableRealEmission = true)
    {
        var mapper = new MapperConfiguration(c => c.AddProfile<MappingProfile>()).CreateMapper();

        var settingsMock = new Mock<IOperationalFinanceSettingsService>();
        settingsMock
            .Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings
            {
                // El guard de EnqueuePartialCreditNoteAsync lee este flag (defense in
                // depth). Default true para reflejar el escenario operativo de Fase 2;
                // el test del flag apagado lo pasa en false.
                EnablePartialCreditNoteRealEmission = enableRealEmission,
                PartialCreditNoteRoundingTolerance = Tolerance,
            });

        var jobClientMock = new Mock<IBackgroundJobClient>();

        var service = new InvoiceService(
            context,
            new EntityReferenceResolver(context),
            new Mock<IAfipService>().Object,
            new Mock<IInvoicePdfService>().Object,
            mapper,
            jobClientMock.Object,
            NullLogger<InvoiceService>.Instance,
            settingsMock.Object,
            BuildUserManager());

        return (service, jobClientMock);
    }

    /// <summary>
    /// UserManager minimo (no se usa en este flujo, pero el ctor de InvoiceService lo
    /// exige). Mismo patron que <c>InvoiceServiceFilteringAndAnnulmentTests</c>.
    /// </summary>
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
    /// Persiste una factura minima (sin reserva ni items — la NC parcial no los
    /// necesita en el ENCOLADO, solo en el job de Etapa 5) y devuelve su Id.
    /// </summary>
    private async Task<int> SeedInvoiceAsync(
        int tipoComprobante = 6, // Factura B por default
        AnnulmentStatus annulmentStatus = AnnulmentStatus.None)
    {
        await using var ctx = _fixture.CreateDbContext();
        var invoice = new Invoice
        {
            TipoComprobante = tipoComprobante,
            PuntoDeVenta = 1,
            NumeroComprobante = 1001,
            CAE = "12345678901234",
            Resultado = "A",
            ImporteTotal = 1_000_000m,
            ImporteNeto = 826_446m,
            ImporteIva = 173_554m,
            AnnulmentStatus = annulmentStatus,
            CreatedAt = DateTime.UtcNow,
        };
        ctx.Invoices.Add(invoice);
        await ctx.SaveChangesAsync();
        return invoice.Id;
    }

    /// <summary>
    /// Liquidacion coherente: factura origen consistente (neto + IVA == total) y las
    /// lineas suman exactamente el monto fiscal a acreditar. Ejemplo: de una factura B
    /// de 1.000.000 se acreditan 300.000.
    /// </summary>
    private static PartialCreditNoteEmissionInput BuildValidLiquidation()
    {
        var lines = new List<PartialCreditNoteLineDto>
        {
            new(Description: "Hotel - parte cancelada", Quantity: 1m, UnitPrice: 300_000m, Total: 300_000m, AlicuotaIvaId: 5),
        };
        return new PartialCreditNoteEmissionInput(
            OriginalNetAmount: 826_446m,
            OriginalVatAmount: 173_554m,
            OriginalTotalAmount: 1_000_000m,
            FiscalAmountToCredit: 300_000m,
            Currency: "ARS",
            ExchangeRateAtOriginalInvoice: 1m,
            Lines: lines);
    }

    // =========================================================================
    // Tests.
    // =========================================================================

    /// <summary>
    /// Happy path: liquidacion valida -> la factura queda en Pending con la
    /// trazabilidad persistida + se encola exactamente un job Hangfire.
    /// </summary>
    [Fact]
    public async Task EnqueuePartialCreditNoteAsync_HappyPath_PersistsPendingAndEnqueuesJob()
    {
        var invoiceId = await SeedInvoiceAsync();

        await using var ctx = _fixture.CreateDbContext();
        var (service, jobClientMock) = BuildService(ctx);

        await service.EnqueuePartialCreditNoteAsync(
            originalInvoiceId: invoiceId,
            liquidation: BuildValidLiquidation(),
            userId: "vendedor-X",
            userName: "Vendedor X",
            reason: "Cancelacion parcial de hotel",
            approvalRequestId: 42,
            ct: CancellationToken.None);

        // La factura quedo marcada como anulacion en curso, con la trazabilidad.
        await using var verifyCtx = _fixture.CreateDbContext();
        var refreshed = await verifyCtx.Invoices.AsNoTracking().FirstAsync(i => i.Id == invoiceId);
        Assert.Equal(AnnulmentStatus.Pending, refreshed.AnnulmentStatus);
        Assert.Equal("vendedor-X", refreshed.AnnulledByUserId);
        Assert.Equal("Vendedor X", refreshed.AnnulledByUserName);
        Assert.Equal("Cancelacion parcial de hotel", refreshed.AnnulmentReason);
        Assert.Equal(42, refreshed.AnnulmentApprovalRequestId);
        // AnnulledAt se setea cuando el ARCA confirma la NC (job, Etapa 5), no aca.
        Assert.Null(refreshed.AnnulledAt);

        // Se encolo el job. Enqueue<T> internamente llama Create(Job, EnqueuedState).
        jobClientMock.Verify(
            j => j.Create(It.IsAny<Job>(), It.IsAny<EnqueuedState>()),
            Times.Once);
    }

    /// <summary>
    /// Idempotencia: si la factura ya esta en Pending, una segunda solicitud rebota
    /// sin encolar un segundo job (evita doble NC parcial sobre la misma factura).
    /// </summary>
    [Fact]
    public async Task EnqueuePartialCreditNoteAsync_AlreadyPending_Rejects()
    {
        var invoiceId = await SeedInvoiceAsync(annulmentStatus: AnnulmentStatus.Pending);

        await using var ctx = _fixture.CreateDbContext();
        var (service, jobClientMock) = BuildService(ctx);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.EnqueuePartialCreditNoteAsync(
                originalInvoiceId: invoiceId,
                liquidation: BuildValidLiquidation(),
                userId: "vendedor-X",
                userName: "Vendedor X",
                reason: "retry",
                approvalRequestId: 42,
                ct: CancellationToken.None));

        Assert.Contains("anulacion en curso", ex.Message);

        // No se encolo ningun job.
        jobClientMock.Verify(
            j => j.Create(It.IsAny<Job>(), It.IsAny<EnqueuedState>()),
            Times.Never);
    }

    /// <summary>
    /// RH-003: Factura M (tipo 51) NO esta soportada para NC parcial en Fase 2. La
    /// solicitud rebota fail-fast, sin mutar estado ni encolar.
    /// </summary>
    [Fact]
    public async Task EnqueuePartialCreditNoteAsync_FacturaM_Rejects()
    {
        var invoiceId = await SeedInvoiceAsync(tipoComprobante: 51); // Factura M

        await using var ctx = _fixture.CreateDbContext();
        var (service, jobClientMock) = BuildService(ctx);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.EnqueuePartialCreditNoteAsync(
                originalInvoiceId: invoiceId,
                liquidation: BuildValidLiquidation(),
                userId: "vendedor-X",
                userName: "Vendedor X",
                reason: "test",
                approvalRequestId: 42,
                ct: CancellationToken.None));

        Assert.Contains("Factura M no soportada", ex.Message);

        // No muto el estado de la factura y no encolo el job.
        await using var verifyCtx = _fixture.CreateDbContext();
        var refreshed = await verifyCtx.Invoices.AsNoTracking().FirstAsync(i => i.Id == invoiceId);
        Assert.Equal(AnnulmentStatus.None, refreshed.AnnulmentStatus);
        Assert.Null(refreshed.AnnulmentReason);

        jobClientMock.Verify(
            j => j.Create(It.IsAny<Job>(), It.IsAny<EnqueuedState>()),
            Times.Never);
    }

    /// <summary>
    /// M4: si la validacion defensiva PRE-encolado falla (la suma de las lineas no
    /// coincide con el monto fiscal a acreditar), el throw ocurre ANTES de mutar el
    /// estado y ANTES de encolar. Cero side-effects: la factura sigue en None.
    /// </summary>
    [Fact]
    public async Task EnqueuePartialCreditNoteAsync_SumMismatchAtEnqueue_DoesNotMutateInvoiceState()
    {
        var invoiceId = await SeedInvoiceAsync();

        // Liquidacion invalida: las lineas suman 299.999,50 pero FiscalAmountToCredit
        // dice 300.000 — gap de 0,50 > tolerancia 0,01.
        var invalidLiquidation = new PartialCreditNoteEmissionInput(
            OriginalNetAmount: 826_446m,
            OriginalVatAmount: 173_554m,
            OriginalTotalAmount: 1_000_000m,
            FiscalAmountToCredit: 300_000m,
            Currency: "ARS",
            ExchangeRateAtOriginalInvoice: 1m,
            Lines: new List<PartialCreditNoteLineDto>
            {
                new(Description: "Hotel - parte cancelada", Quantity: 1m, UnitPrice: 299_999.50m, Total: 299_999.50m, AlicuotaIvaId: 5),
            });

        await using var ctx = _fixture.CreateDbContext();
        var (service, jobClientMock) = BuildService(ctx);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.EnqueuePartialCreditNoteAsync(
                originalInvoiceId: invoiceId,
                liquidation: invalidLiquidation,
                userId: "vendedor-X",
                userName: "Vendedor X",
                reason: "test",
                approvalRequestId: 42,
                ct: CancellationToken.None));

        Assert.Contains("suma de las lineas no coincide", ex.Message);

        // CLAVE (M4): el estado de la factura NO cambio — sigue en None, sin razon
        // ni approval persistidos.
        await using var verifyCtx = _fixture.CreateDbContext();
        var refreshed = await verifyCtx.Invoices.AsNoTracking().FirstAsync(i => i.Id == invoiceId);
        Assert.Equal(AnnulmentStatus.None, refreshed.AnnulmentStatus);
        Assert.Null(refreshed.AnnulmentReason);
        Assert.Null(refreshed.AnnulmentApprovalRequestId);

        // Y no se encolo el job.
        jobClientMock.Verify(
            j => j.Create(It.IsAny<Job>(), It.IsAny<EnqueuedState>()),
            Times.Never);
    }

    /// <summary>
    /// RH4 (defense in depth): con el flag maestro de Fase 2 apagado
    /// (<c>EnablePartialCreditNoteRealEmission=false</c>), el metodo rebota ANTES de
    /// mutar estado y ANTES de encolar. Aunque el caller (BC service de F2.3) deberia
    /// validar el flag, el metodo es public y no confia en eso: con el flag OFF el
    /// sistema sigue como FC1.2 (NC total), asi que emitir una NC parcial aca seria un
    /// cambio de comportamiento no autorizado. Cero side-effects: la factura sigue en None.
    /// </summary>
    [Fact]
    public async Task EnqueuePartialCreditNoteAsync_FlagDisabled_Rejects()
    {
        var invoiceId = await SeedInvoiceAsync();

        await using var ctx = _fixture.CreateDbContext();
        var (service, jobClientMock) = BuildService(ctx, enableRealEmission: false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.EnqueuePartialCreditNoteAsync(
                originalInvoiceId: invoiceId,
                liquidation: BuildValidLiquidation(),
                userId: "vendedor-X",
                userName: "Vendedor X",
                reason: "test",
                approvalRequestId: 42,
                ct: CancellationToken.None));

        Assert.Contains("EnablePartialCreditNoteRealEmission=false", ex.Message);

        // CLAVE: con el flag apagado el estado de la factura NO cambio — sigue en None,
        // sin razon ni approval persistidos.
        await using var verifyCtx = _fixture.CreateDbContext();
        var refreshed = await verifyCtx.Invoices.AsNoTracking().FirstAsync(i => i.Id == invoiceId);
        Assert.Equal(AnnulmentStatus.None, refreshed.AnnulmentStatus);
        Assert.Null(refreshed.AnnulmentReason);
        Assert.Null(refreshed.AnnulmentApprovalRequestId);

        // Y no se encolo el job.
        jobClientMock.Verify(
            j => j.Create(It.IsAny<Job>(), It.IsAny<EnqueuedState>()),
            Times.Never);
    }

    /// <summary>
    /// M4 (chequeo 1 de ValidateLiquidationAmounts): si la factura origen es incoherente
    /// (neto + IVA != total, fuera de tolerancia), el throw ocurre ANTES de mutar el
    /// estado y ANTES de encolar. Mejor rebotar aca que mandar un XML inconsistente al
    /// ARCA. Cero side-effects: la factura sigue en None.
    /// </summary>
    [Fact]
    public async Task EnqueuePartialCreditNoteAsync_OriginalAmountsIncoherent_DoesNotMutateInvoiceState()
    {
        var invoiceId = await SeedInvoiceAsync();

        // Liquidacion con factura origen rota: neto 826.446 + IVA 173.554 = 1.000.000,
        // pero OriginalTotalAmount dice 1.000.001 — gap de 1,00 > tolerancia 0,01.
        // (Las lineas SI suman FiscalAmountToCredit, para aislar el chequeo 1 del 2.)
        var incoherentLiquidation = new PartialCreditNoteEmissionInput(
            OriginalNetAmount: 826_446m,
            OriginalVatAmount: 173_554m,
            OriginalTotalAmount: 1_000_001m,
            FiscalAmountToCredit: 300_000m,
            Currency: "ARS",
            ExchangeRateAtOriginalInvoice: 1m,
            Lines: new List<PartialCreditNoteLineDto>
            {
                new(Description: "Hotel - parte cancelada", Quantity: 1m, UnitPrice: 300_000m, Total: 300_000m, AlicuotaIvaId: 5),
            });

        await using var ctx = _fixture.CreateDbContext();
        var (service, jobClientMock) = BuildService(ctx);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.EnqueuePartialCreditNoteAsync(
                originalInvoiceId: invoiceId,
                liquidation: incoherentLiquidation,
                userId: "vendedor-X",
                userName: "Vendedor X",
                reason: "test",
                approvalRequestId: 42,
                ct: CancellationToken.None));

        Assert.Contains("montos de la factura origen no son coherentes", ex.Message);

        // CLAVE: el estado de la factura NO cambio — sigue en None, sin razon ni approval.
        await using var verifyCtx = _fixture.CreateDbContext();
        var refreshed = await verifyCtx.Invoices.AsNoTracking().FirstAsync(i => i.Id == invoiceId);
        Assert.Equal(AnnulmentStatus.None, refreshed.AnnulmentStatus);
        Assert.Null(refreshed.AnnulmentReason);
        Assert.Null(refreshed.AnnulmentApprovalRequestId);

        // Y no se encolo el job.
        jobClientMock.Verify(
            j => j.Create(It.IsAny<Job>(), It.IsAny<EnqueuedState>()),
            Times.Never);
    }
}
