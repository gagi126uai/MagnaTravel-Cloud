using System;
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
using TravelApi.Infrastructure.Reservations;
using TravelApi.Infrastructure.Services;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Cancellation.Integration;

/// <summary>
/// ADR-048 T5 fix B1 (2026-07-17, review backend) — MT2: la regresión que el review encontró.
///
/// <para><b>El bug (B1, cerrado por este fix)</b>: emitir una FACTURA DE VENTA nunca movía el saldo de
/// la reserva (ADR-037: facturar está desacoplado del cobro), así que nunca pasaba por
/// <c>ReservaMoneyPersister.PersistAsync</c> — el único lugar que, hasta antes de este fix, refrescaba
/// <c>Reserva.DerivedInvoicingStatus</c>. Con el listado leyendo esa columna directo (T5), el resultado
/// era: reserva pagada y facturada → LISTADO dice "Sin facturar" (columna vieja) mientras el DETALLE
/// dice "Facturada total" (sigue derivando en vivo). Exactamente la mentira #2 que ADR-048 vino a
/// matar, reintroducida por la materialización de T5.</para>
///
/// <para><b>Cómo se "emite" la factura en este test, sin ARCA real</b>: mismo patrón que
/// <c>AfipServiceCascadeReceiptVoidTests</c> (el único precedente de este repo para probar el efecto de
/// <c>AfipService.ProcessInvoiceJob</c> sin un SOAP real contra AFIP) — se siembra la <c>Invoice</c>
/// directo con <c>Resultado="A"</c> (el estado que deja ARCA tras aprobar el CAE) y se invoca el MISMO
/// método que <c>AfipService.ProcessInvoiceJob</c> llama, ahora, para una factura de venta/ND que no es
/// Nota de Crédito: <see cref="ReservaMoneyPersister.RefreshInvoicingAxisOnlyAsync"/>. Este repo no tiene
/// infraestructura para fakear el SOAP de WSFE/WSAA (ningún test existente lo hace); construirla está
/// fuera del alcance de este hardening. La cobertura de "ProcessInvoiceJob invoca este método" es por
/// lectura de código (compila contra el símbolo real) — ver <c>AfipService.cs</c>, el bloque después de
/// <c>invoice.Resultado = "A"</c>.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class Adr048T5B1FacturaDeVentaRegressionIntegrationTests
    : IClassFixture<PostgresIntegrationFixture>, IAsyncLifetime
{
    private readonly PostgresIntegrationFixture _fixture;

    public Adr048T5B1FacturaDeVentaRegressionIntegrationTests(PostgresIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => _fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ReservaPagadaAlCien_TrasEmitirFacturaDeVenta_ListadoYDetalleCoincidenEnFacturadaTotal()
    {
        Guid reservaPublicId;
        int reservaId;

        await using (var seedCtx = _fixture.CreateDbContext())
        {
            var customer = new Customer { FullName = "Cliente B1", TaxCondition = "Consumidor Final", IsActive = true };
            var supplier = new Supplier { Name = "Operador B1", IsActive = true, TaxCondition = "IVA_RESP_INSCRIPTO" };
            seedCtx.Customers.Add(customer);
            seedCtx.Suppliers.Add(supplier);
            await seedCtx.SaveChangesAsync();

            var reserva = new Reserva
            {
                NumeroReserva = "F-B1-REGRESION",
                Name = "Reserva pagada, despues facturada (flujo habitual)",
                Status = EstadoReserva.Confirmed,
                PayerId = customer.Id,
            };
            seedCtx.Reservas.Add(reserva);
            await seedCtx.SaveChangesAsync();
            reservaPublicId = reserva.PublicId;
            reservaId = reserva.Id;

            seedCtx.HotelBookings.Add(new HotelBooking
            {
                ReservaId = reserva.Id, SupplierId = supplier.Id, Status = "Confirmado",
                SalePrice = 1000m, NetCost = 700m, Currency = "ARS",
                CheckIn = DateTime.UtcNow.AddDays(20), CheckOut = DateTime.UtcNow.AddDays(25),
            });
            await seedCtx.SaveChangesAsync();

            seedCtx.Payments.Add(new Payment
            {
                ReservaId = reserva.Id, Amount = 1000m, Currency = "ARS", Status = "Paid", IsDeleted = false,
            });
            await seedCtx.SaveChangesAsync();
        }

        // 1) La reserva se cobra al 100% ANTES de facturar (flujo habitual: "facturar post-pago").
        //    Esto es lo unico que, hasta antes del fix, refrescaba DerivedInvoicingStatus -> queda en
        //    "NotInvoiced" (todavia no hay ninguna factura).
        await using (var persistCtx = _fixture.CreateDbContext())
        {
            await ReservaMoneyPersister.PersistAsync(persistCtx, reservaId, CancellationToken.None);
        }

        await using (var verifyBeforeCtx = _fixture.CreateDbContext())
        {
            var antesDeFacturar = await verifyBeforeCtx.Reservas.AsNoTracking().SingleAsync(r => r.Id == reservaId);
            Assert.Equal(ReservaCollectionStatus.Settled, antesDeFacturar.DerivedCollectionStatus);
            Assert.Equal(ReservaInvoicingStatus.NotInvoiced, antesDeFacturar.DerivedInvoicingStatus);
        }

        // 2) "Emitir la factura de venta": ARCA aprueba el CAE (Resultado="A"). Simulado igual que
        //    AfipServiceCascadeReceiptVoidTests simula la aprobacion de una NC (sin SOAP real).
        await using (var emitCtx = _fixture.CreateDbContext())
        {
            emitCtx.Invoices.Add(new Invoice
            {
                ReservaId = reservaId, TipoComprobante = 1, ImporteTotal = 1000m, Resultado = "A",
            });
            await emitCtx.SaveChangesAsync();
        }

        // REGRESION SIN EL FIX (documentado, no ejecutado como assert-de-fallo): si en este punto
        // NADIE refresca DerivedInvoicingStatus, la columna sigue en "NotInvoiced" (el valor del paso 1)
        // para siempre — el bug B1. El paso 3 es EXACTAMENTE el codigo que
        // AfipService.ProcessInvoiceJob corre ahora (fix de esta tanda) para una factura de venta/ND.
        await using (var refreshCtx = _fixture.CreateDbContext())
        {
            await ReservaMoneyPersister.RefreshInvoicingAxisOnlyAsync(refreshCtx, reservaId, CancellationToken.None);
        }

        // 3) LISTADO y DETALLE tienen que coincidir en "Facturada total" — NO "Sin facturar".
        await using var queryCtx = _fixture.CreateDbContext();
        var service = BuildReservaService(queryCtx);

        var page = await service.GetReservasAsync(new ReservaListQuery(), CancellationToken.None);
        var filaEnListado = Assert.Single(page.Items, i => i.PublicId == reservaPublicId);
        Assert.Equal(ReservaInvoicingStatus.FullyInvoiced, filaEnListado.InvoicingStatus);

        var detalle = await service.GetReservaByIdAsync(reservaPublicId.ToString(), CancellationToken.None);
        Assert.Equal(ReservaInvoicingStatus.FullyInvoiced, detalle.InvoicingStatus);
    }

    /// <summary>Mismo patron que <c>Adr048T5DerivedAxesIntegrationTests.BuildReservaService</c>.</summary>
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
}
