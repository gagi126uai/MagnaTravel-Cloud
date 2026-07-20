using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
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
using TravelApi.Infrastructure.Services.Reservations;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Cancellation.Integration;

/// <summary>
/// Tanda 6 (plan de remediacion "contrato pantalla-motor", 2026-07-20) — regla dura de merge (B2 del plan):
/// los flags <c>canEdit</c>/<c>canDelete</c> que el DTO expone por CADA cobro dependen de estado real en la
/// base (recibo Issued, factura con CAE viva), asi que el cross-check contra los guards reales tiene que
/// correr contra Postgres real, no InMemory ni un test puro.
///
/// <para><b>Hallazgo del frontend-reviewer (2026-07-20, post primera version de este archivo):</b> la ficha
/// NO arma <c>reserva.payments[]</c> desde <c>ReservaService.GetReservaByIdAsync</c> (el detalle completo de
/// la reserva) — lo arma desde <c>PaymentService.GetPaymentsForReservaAsync</c>
/// (<c>GET /api/payments/reserva/{id}</c>, el que llama <c>useReservaDetail.js</c>). Los dos caminos
/// construyen <c>PaymentDto</c> de forma distinta (uno desde entidades ya cargadas, el otro desde un
/// <c>ProjectTo</c> + una consulta extra), asi que este archivo cruza los DOS contra los guards reales, no
/// solo el del detalle.</para>
///
/// Este test seedea los escenarios de la spec de pantalla, pide el DTO por LOS DOS caminos y verifica que
/// CADA flag coincide EXACTO (Allowed + Reason) con lo que responden los guards reales de escritura
/// (<c>MutationGuards.GetPaymentMutationBlockReasonAsync</c> / <c>DeleteGuards.GetPaymentDeleteBlockReasonAsync</c>)
/// sobre la MISMA fila de Postgres — los mismos metodos que corren de verdad cuando el usuario aprieta
/// "Guardar" en el PUT/DELETE de un cobro.
///
/// <para><b>Por que esto y no un PUT/DELETE por HTTP real</b>: el unico harness HTTP del repo
/// (<c>CustomWebApplicationFactory</c>) fuerza InMemory a proposito (ver su <c>ConfigureWebHost</c>) — no
/// sirve para la regla B2, que pide Postgres real. El unico harness con Postgres real del repo
/// (<c>PostgresIntegrationFixture</c>, Testcontainers) no levanta el pipeline HTTP; en cambio se usa aca para
/// invocar DIRECTAMENTE los mismos metodos que <c>PaymentsController</c>/<c>ReservasController</c> llaman por
/// debajo (<c>PaymentService.GetPaymentsForReservaAsync</c>, <c>ReservaService.GetReservaByIdAsync</c>,
/// <c>MutationGuards</c>/<c>DeleteGuards</c>) — que es exactamente donde vive la regla real que se esta
/// cruzando. Mismo patron de construccion de <c>ReservaService</c> que ya usan los demas tests de este
/// directorio contra Postgres (ver <c>Adr048T3ListInvoicingStatusIntegrationTests</c>).</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class PaymentRowCapabilityFlagsIntegrationTests
    : IClassFixture<PostgresIntegrationFixture>, IAsyncLifetime
{
    private readonly PostgresIntegrationFixture _fixture;

    public PaymentRowCapabilityFlagsIntegrationTests(PostgresIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => _fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task SinReciboSinFactura_DtoYGuardCoinciden_PermitenEditarYEliminar()
    {
        var (reservaId, paymentId) = await SeedReservaConUnPagoAsync();

        await AssertDtoFlagsMatchRealGuardsAsync(reservaId, paymentId);
    }

    [Fact]
    public async Task ReciboEmitido_DtoYGuardCoinciden_BloqueanEditarYEliminar()
    {
        var (reservaId, paymentId) = await SeedReservaConUnPagoAsync();

        await using (var seedCtx = _fixture.CreateDbContext())
        {
            seedCtx.PaymentReceipts.Add(new PaymentReceipt
            {
                PaymentId = paymentId,
                ReservaId = reservaId,
                ReceiptNumber = "REC-T6-" + Guid.NewGuid().ToString("N")[..6],
                Amount = 100m,
                Status = PaymentReceiptStatuses.Issued,
            });
            await seedCtx.SaveChangesAsync();
        }

        await AssertDtoFlagsMatchRealGuardsAsync(reservaId, paymentId);
    }

    [Fact]
    public async Task ReciboSoloAnulado_DtoYGuardCoinciden_BloqueanEditar_PeroPermitenEliminar()
    {
        // Regla vigente desde 2026-05-11 (C28): un recibo Voided-solo no bloquea eliminar. Este test cruza
        // ESA asimetria contra el guard real, no solo contra la politica pura (ya cubierta en
        // PaymentCapabilityPolicyTests) — Postgres es quien manda la ultima palabra sobre el estado real.
        var (reservaId, paymentId) = await SeedReservaConUnPagoAsync();

        await using (var seedCtx = _fixture.CreateDbContext())
        {
            seedCtx.PaymentReceipts.Add(new PaymentReceipt
            {
                PaymentId = paymentId,
                ReservaId = reservaId,
                ReceiptNumber = "REC-T6-" + Guid.NewGuid().ToString("N")[..6],
                Amount = 100m,
                Status = PaymentReceiptStatuses.Voided,
                VoidedAt = DateTime.UtcNow,
                VoidedByUserId = "u-test",
                VoidedByUserName = "Vendedor de prueba",
                VoidReason = "Prueba T6",
            });
            await seedCtx.SaveChangesAsync();
        }

        await AssertDtoFlagsMatchRealGuardsAsync(reservaId, paymentId);
    }

    [Fact]
    public async Task VinculadoAFacturaConCaeVivo_DtoYGuardCoinciden_BloqueanEditarYEliminar()
    {
        var (reservaId, paymentId) = await SeedReservaConUnPagoAsync();

        await using (var seedCtx = _fixture.CreateDbContext())
        {
            var invoice = new Invoice
            {
                TipoComprobante = 6, // Factura B (comun, no NC)
                PuntoDeVenta = 1,
                NumeroComprobante = 1,
                ImporteTotal = 100m,
                ImporteNeto = 82.64m,
                ImporteIva = 17.36m,
                ReservaId = reservaId,
                CAE = "68000000000099",
                Resultado = "A",
                AnnulmentStatus = AnnulmentStatus.None,
                CreatedAt = DateTime.UtcNow,
            };
            seedCtx.Invoices.Add(invoice);
            await seedCtx.SaveChangesAsync();

            var payment = await seedCtx.Payments.FirstAsync(p => p.Id == paymentId);
            payment.RelatedInvoiceId = invoice.Id;
            await seedCtx.SaveChangesAsync();
        }

        await AssertDtoFlagsMatchRealGuardsAsync(reservaId, paymentId);
    }

    [Fact]
    public async Task VinculadoAFacturaYaAnuladaConNc_DtoYGuardCoinciden_LiberaEditar_PeroSigueBloqueandoEliminar()
    {
        // El caso mas sutil de la tanda: la factura tiene AnnulmentStatus=Succeeded (la NC ya fue aprobada
        // por AFIP), asi que para EDITAR ya no cuenta como "viva" — pero para ELIMINAR el pago sigue
        // bloqueado porque estuvo vinculado a una factura ALGUNA VEZ (regla preexistente de DeleteGuards,
        // mas estricta a proposito porque borrar es irreversible). Si algun dia alguien "corrige" el guard
        // real de borrado para que sea simetrico con el de editar sin tocar este test, este assert lo
        // atrapa (el cross-check compara contra el guard REAL, no contra un valor fijo).
        var (reservaId, paymentId) = await SeedReservaConUnPagoAsync();

        await using (var seedCtx = _fixture.CreateDbContext())
        {
            var invoice = new Invoice
            {
                TipoComprobante = 6,
                PuntoDeVenta = 1,
                NumeroComprobante = 2,
                ImporteTotal = 100m,
                ImporteNeto = 82.64m,
                ImporteIva = 17.36m,
                ReservaId = reservaId,
                CAE = "68000000000098",
                Resultado = "A",
                AnnulmentStatus = AnnulmentStatus.Succeeded, // NC ya aprobada: la factura ya no esta viva
                CreatedAt = DateTime.UtcNow,
            };
            seedCtx.Invoices.Add(invoice);
            await seedCtx.SaveChangesAsync();

            var payment = await seedCtx.Payments.FirstAsync(p => p.Id == paymentId);
            payment.RelatedInvoiceId = invoice.Id;
            await seedCtx.SaveChangesAsync();
        }

        await AssertDtoFlagsMatchRealGuardsAsync(reservaId, paymentId);
    }

    // ---------- helpers de seed ----------

    private async Task<(int ReservaId, int PaymentId)> SeedReservaConUnPagoAsync()
    {
        await using var seedCtx = _fixture.CreateDbContext();

        var reserva = new Reserva
        {
            NumeroReserva = "F-T6-" + Guid.NewGuid().ToString("N")[..6],
            Name = "Reserva T6 capacidades de pago",
            Status = EstadoReserva.Confirmed,
        };
        seedCtx.Reservas.Add(reserva);
        await seedCtx.SaveChangesAsync();

        var payment = new Payment { ReservaId = reserva.Id, Amount = 100m, Method = "Cash", Status = "Paid" };
        seedCtx.Payments.Add(payment);
        await seedCtx.SaveChangesAsync();

        return (reserva.Id, payment.Id);
    }

    /// <summary>
    /// El corazon del cross-check (B2): pide el DTO por LOS DOS caminos que la ficha puede usar
    /// (<c>ReservaService.GetReservaByIdAsync</c> — detalle completo — y
    /// <c>PaymentService.GetPaymentsForReservaAsync</c> — el que la ficha usa DE VERDAD, hallazgo del
    /// frontend-reviewer), le pregunta a los guards reales su veredicto sobre EL MISMO pago, y compara los
    /// tres. Cada lado usa un <see cref="AppDbContext"/> NUEVO (sin tracking compartido) para que el
    /// resultado sea lo que Postgres realmente tiene guardado, no lo que el change tracker recuerda de haber
    /// seedeado.
    /// </summary>
    private async Task AssertDtoFlagsMatchRealGuardsAsync(int reservaId, int paymentId)
    {
        await using var reservaDetailCtx = _fixture.CreateDbContext();
        var reservaService = BuildReservaService(reservaDetailCtx);
        var reservaDetailDto = await reservaService.GetReservaByIdAsync(reservaId);
        var paymentFromReservaDetail = Assert.Single(reservaDetailDto.Payments);

        await using var paymentsListCtx = _fixture.CreateDbContext();
        var paymentService = BuildPaymentService(paymentsListCtx);
        var paymentsList = (await paymentService.GetPaymentsForReservaAsync(reservaId, CancellationToken.None)).ToList();
        var paymentFromPaymentsList = Assert.Single(paymentsList);

        await using var guardCtx = _fixture.CreateDbContext();
        var editReason = await MutationGuards.GetPaymentMutationBlockReasonAsync(guardCtx, paymentId, CancellationToken.None);
        var deleteReason = await DeleteGuards.GetPaymentDeleteBlockReasonAsync(guardCtx, paymentId, CancellationToken.None);

        AssertMatchesGuard(paymentFromReservaDetail, editReason, deleteReason, camino: "GetReservaByIdAsync");
        AssertMatchesGuard(paymentFromPaymentsList, editReason, deleteReason, camino: "GetPaymentsForReservaAsync (el que usa la ficha)");
    }

    /// <summary>
    /// Compara los flags de UN <see cref="PaymentDto"/> contra el veredicto real del guard. Ambos flags
    /// deben venir POBLADOS (no null) — si alguno de los dos caminos se olvida de calcularlos, este assert
    /// falla con <c>Assert.NotNull</c> antes de comparar, en vez de un <c>NullReferenceException</c> opaco.
    /// </summary>
    private static void AssertMatchesGuard(PaymentDto payment, string? editReason, string? deleteReason, string camino)
    {
        Assert.True(payment.CanEdit is not null, $"{camino}: CanEdit vino null (esperado: poblado).");
        Assert.True(payment.CanDelete is not null, $"{camino}: CanDelete vino null (esperado: poblado).");

        Assert.Equal(editReason is null, payment.CanEdit!.Allowed);
        Assert.Equal(editReason, payment.CanEdit.Reason);

        Assert.Equal(deleteReason is null, payment.CanDelete!.Allowed);
        Assert.Equal(deleteReason, payment.CanDelete.Reason);
    }

    /// <summary>
    /// Arma un <see cref="ReservaService"/> real (no mockeado) apuntando al Postgres del fixture. Mismo
    /// patron que <c>Adr048T3ListInvoicingStatusIntegrationTests.BuildReservaService</c>: sin
    /// <c>IHttpContextAccessor</c>/permission resolver a proposito (este test no ejercita masking de costos
    /// ni multas del operador, fuera de alcance de esta tanda).
    /// </summary>
    private static ReservaService BuildReservaService(AppDbContext context)
    {
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OperationalFinanceSettings());

        var store = new Mock<IUserStore<ApplicationUser>>();
        var userManager = new UserManager<ApplicationUser>(
            store.Object, null!, null!,
            Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(),
            null!, null!, null!, null!);

        return new ReservaService(
            context,
            new MapperConfiguration(c => c.AddProfile<MappingProfile>()).CreateMapper(),
            settings.Object,
            userManager,
            NullLogger<ReservaService>.Instance);
    }

    /// <summary>
    /// Arma un <see cref="PaymentService"/> real apuntando al Postgres del fixture — el service que de verdad
    /// atiende <c>GET /api/payments/reserva/{id}</c>, el endpoint que consume la ficha (ver docstring de la
    /// clase). Ctor de 5 args (los opcionales quedan en default null, igual que <c>BuildReservaService</c>).
    /// </summary>
    private static PaymentService BuildPaymentService(AppDbContext context)
    {
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OperationalFinanceSettings());

        return new PaymentService(
            context,
            new EntityReferenceResolver(context),
            new MapperConfiguration(c => c.AddProfile<MappingProfile>()).CreateMapper(),
            settings.Object,
            NullLogger<PaymentService>.Instance);
    }
}
