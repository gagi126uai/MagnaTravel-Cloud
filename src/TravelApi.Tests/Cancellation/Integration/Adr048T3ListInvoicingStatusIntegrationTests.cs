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
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Cancellation.Integration;

/// <summary>
/// ADR-048 T3 (2026-07-17, regla 5) — item pedido por el review backend (M6.1, "missing tests"):
/// prueba contra Postgres REAL que el LISTADO de reservas devuelve "FullyReturned" cuando una factura
/// con CAE se devolvió con una Nota de Crédito TOTAL.
///
/// <para>Por qué hace falta un test de Postgres (y no alcanza con los tests puros de
/// <c>ReservaInvoicingStatusTests</c>/<c>ReservaInvoicingCuadreCalculatorTests</c>): la señal nueva
/// (<c>BrutoEmitido</c>) del LISTADO se arma con una query LINQ-to-SQL agrupada
/// (<c>FillInvoicingStatusForListAsync</c>, <c>ReservaService.cs</c>) que EF Core tiene que traducir a SQL
/// real. Los unit tests corren contra el proveedor InMemory, que NUNCA ejercita esa traducción — si la
/// forma de la query no traduce en Postgres, el listado de reservas revienta con 500 recién en producción
/// (el patrón "verde en unit, roto en Postgres" que ya mordió a este proyecto). Este test blinda DOS
/// cosas a la vez:
///   1. Que la query del listado compila y corre contra Postgres real (fija la traducción SQL, M1 del
///      review backend).
///   2. Que el LISTADO y el DETALLE (<c>GetReservaByIdAsync</c>) devuelven el MISMO
///      <c>InvoicingStatus</c> para la misma reserva (alineación detalle-vs-listado, la otra mitad de lo
///      que pedía la spec §T3 con su caminata E2E).</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class Adr048T3ListInvoicingStatusIntegrationTests
    : IClassFixture<PostgresIntegrationFixture>, IAsyncLifetime
{
    private readonly PostgresIntegrationFixture _fixture;

    public Adr048T3ListInvoicingStatusIntegrationTests(PostgresIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => _fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task FacturaConCae_MasNotaDeCreditoTotal_ElListadoDevuelveFullyReturned()
    {
        Guid reservaPublicId;
        await using (var seedCtx = _fixture.CreateDbContext())
        {
            var customer = new Customer
            {
                FullName = "Cliente T3 listado",
                TaxCondition = "Consumidor Final",
                IsActive = true,
            };
            seedCtx.Customers.Add(customer);
            await seedCtx.SaveChangesAsync();

            var reserva = new Reserva
            {
                NumeroReserva = "F-T3-LISTADO",
                Name = "Reserva T3 — factura y NC total",
                Status = EstadoReserva.Confirmed,
                PayerId = customer.Id,
            };
            seedCtx.Reservas.Add(reserva);
            await seedCtx.SaveChangesAsync();
            reservaPublicId = reserva.PublicId;

            // Factura A original: CAE aprobado (Resultado="A"), la que arma el "bruto emitido".
            var facturaOriginal = new Invoice
            {
                TipoComprobante = 1, // Factura A
                PuntoDeVenta = 1,
                NumeroComprobante = 1,
                ImporteTotal = 1000m,
                ImporteNeto = 826.45m,
                ImporteIva = 173.55m,
                ReservaId = reserva.Id,
                CAE = "68000000000001",
                Resultado = "A",
                CreatedAt = DateTime.UtcNow,
            };
            seedCtx.Invoices.Add(facturaOriginal);
            await seedCtx.SaveChangesAsync();

            // Nota de Crédito TOTAL: mismo importe que la factura -> el NETO queda exactamente en 0,
            // pero el BRUTO sigue en 1000 (la NC nunca lo baja). Este es el caso que antes de T3
            // colapsaba con "nunca se facturó nada" y mostraba "Sin facturar".
            var notaDeCreditoTotal = new Invoice
            {
                TipoComprobante = 3, // Nota de Crédito A
                PuntoDeVenta = 1,
                NumeroComprobante = 2,
                ImporteTotal = 1000m,
                ImporteNeto = 826.45m,
                ImporteIva = 173.55m,
                ReservaId = reserva.Id,
                OriginalInvoiceId = facturaOriginal.Id,
                CAE = "68000000000002",
                Resultado = "A",
                CreatedAt = DateTime.UtcNow,
            };
            seedCtx.Invoices.Add(notaDeCreditoTotal);
            await seedCtx.SaveChangesAsync();
        }

        await using var queryCtx = _fixture.CreateDbContext();
        var service = BuildReservaService(queryCtx);

        // ---------- LISTADO: la query agrupada nueva (BrutoEmitido) tiene que traducir contra Postgres ----------
        var page = await service.GetReservasAsync(new ReservaListQuery(), CancellationToken.None);
        var filaEnListado = Assert.Single(page.Items, i => i.PublicId == reservaPublicId);
        Assert.Equal(ReservaInvoicingStatus.FullyReturned, filaEnListado.InvoicingStatus);

        // ---------- DETALLE: misma reserva, mismo eje — no puede divergir del listado ----------
        var detalle = await service.GetReservaByIdAsync(reservaPublicId.ToString(), CancellationToken.None);
        Assert.Equal(ReservaInvoicingStatus.FullyReturned, detalle.InvoicingStatus);
    }

    /// <summary>
    /// Arma un <see cref="ReservaService"/> real (no mockeado) apuntando al Postgres del fixture. Sin
    /// <c>IHttpContextAccessor</c>/permission resolver a propósito: este test no ejercita el masking de
    /// costos (fuera de alcance), y <c>ReservaService</c> los trata como opcionales (ver su constructor).
    /// El <c>UserManager</c> es el único parámetro no-opcional además de los básicos — se arma con un
    /// store mockeado que nunca se invoca en este camino (mismo patrón que
    /// <c>ReservaServiceCancelledMoneyContextTests.BuildUserManager</c>).
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
}
