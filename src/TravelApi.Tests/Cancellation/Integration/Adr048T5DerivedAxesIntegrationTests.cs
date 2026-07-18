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
using TravelApi.Infrastructure.Reservations;
using TravelApi.Infrastructure.Services;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Cancellation.Integration;

/// <summary>
/// ADR-048 T5 (2026-07-17, hardening — materializacion de los ejes secundarios) contra Postgres REAL.
///
/// <para>Por que hace falta Postgres y no alcanza con los unit tests InMemory
/// (<c>Adr048T5DerivedAxesPersisterTests</c>): este test prueba DOS cosas que InMemory no puede
/// demostrar — (1) que la columna nueva realmente se puede FILTRAR en SQL (<c>WHERE
/// "DerivedInvoicingStatus" = ...</c>, con su indice, contra el motor real), y (2) que los TRES caminos
/// — la columna materializada filtrada directo, el LISTADO (que ahora la lee), y el DETALLE (que sigue
/// derivando en vivo) — devuelven el MISMO valor para la MISMA reserva. Si el wiring del listado
/// (<c>FillInvoicingStatusForListAsync</c>) alguna vez divergiera del proyector del persister, este test
/// lo agarra.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class Adr048T5DerivedAxesIntegrationTests
    : IClassFixture<PostgresIntegrationFixture>, IAsyncLifetime
{
    private readonly PostgresIntegrationFixture _fixture;

    public Adr048T5DerivedAxesIntegrationTests(PostgresIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => _fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ColumnaMaterializada_FiltraEnSql_YCoincideConListadoYDetalle()
    {
        Guid reservaPublicId;
        int reservaId;

        await using (var seedCtx = _fixture.CreateDbContext())
        {
            var customer = new Customer { FullName = "Cliente T5", TaxCondition = "Consumidor Final", IsActive = true };
            var supplier = new Supplier { Name = "Operador T5", IsActive = true, TaxCondition = "IVA_RESP_INSCRIPTO" };
            seedCtx.Customers.Add(customer);
            seedCtx.Suppliers.Add(supplier);
            await seedCtx.SaveChangesAsync();

            var reserva = new Reserva
            {
                NumeroReserva = "F-T5-MATERIALIZADO",
                Name = "Reserva T5 — factura y NC total",
                Status = EstadoReserva.Confirmed,
                PayerId = customer.Id,
            };
            seedCtx.Reservas.Add(reserva);
            await seedCtx.SaveChangesAsync();
            reservaPublicId = reserva.PublicId;
            reservaId = reserva.Id;

            seedCtx.HotelBookings.Add(new HotelBooking
            {
                ReservaId = reserva.Id,
                SupplierId = supplier.Id,
                Status = "Confirmado",
                SalePrice = 1000m,
                NetCost = 700m,
                Currency = "ARS",
            });
            await seedCtx.SaveChangesAsync();

            // Factura A original con CAE aprobado.
            seedCtx.Invoices.Add(new Invoice
            {
                TipoComprobante = 1,
                PuntoDeVenta = 1,
                NumeroComprobante = 1,
                ImporteTotal = 1000m,
                ImporteNeto = 826.45m,
                ImporteIva = 173.55m,
                ReservaId = reserva.Id,
                CAE = "68000000000001",
                Resultado = "A",
                CreatedAt = DateTime.UtcNow,
            });
            await seedCtx.SaveChangesAsync();

            // Nota de Credito TOTAL: el neto queda en 0, el bruto sigue > 0 -> "Facturada y devuelta".
            seedCtx.Invoices.Add(new Invoice
            {
                TipoComprobante = 3,
                PuntoDeVenta = 1,
                NumeroComprobante = 2,
                ImporteTotal = 1000m,
                ImporteNeto = 826.45m,
                ImporteIva = 173.55m,
                ReservaId = reserva.Id,
                CAE = "68000000000002",
                Resultado = "A",
                CreatedAt = DateTime.UtcNow,
            });
            await seedCtx.SaveChangesAsync();
        }

        // El UNICO escritor de las columnas materializadas: la MISMA llamada que corre en cada mutacion
        // real de plata (cobro / cancelacion / emision-anulacion de comprobante) en produccion.
        await using (var persistCtx = _fixture.CreateDbContext())
        {
            await ReservaMoneyPersister.PersistAsync(persistCtx, reservaId, CancellationToken.None);
        }

        // ---------- 1) La columna se puede FILTRAR en SQL real (con su indice) ----------
        await using (var filterCtx = _fixture.CreateDbContext())
        {
            var filtradas = await filterCtx.Reservas
                .AsNoTracking()
                .Where(r => r.DerivedInvoicingStatus == ReservaInvoicingStatus.FullyReturned)
                .Select(r => r.PublicId)
                .ToListAsync();

            Assert.Contains(reservaPublicId, filtradas);
        }

        // ---------- 2) El LISTADO (que ahora lee la columna) coincide ----------
        await using var queryCtx = _fixture.CreateDbContext();
        var service = BuildReservaService(queryCtx);

        var page = await service.GetReservasAsync(new ReservaListQuery(), CancellationToken.None);
        var filaEnListado = Assert.Single(page.Items, i => i.PublicId == reservaPublicId);
        Assert.Equal(ReservaInvoicingStatus.FullyReturned, filaEnListado.InvoicingStatus);

        // ---------- 3) El DETALLE (que sigue derivando EN VIVO, a proposito — T5 no lo toca) coincide ----------
        var detalle = await service.GetReservaByIdAsync(reservaPublicId.ToString(), CancellationToken.None);
        Assert.Equal(ReservaInvoicingStatus.FullyReturned, detalle.InvoicingStatus);
    }

    /// <summary>
    /// Arma un <see cref="ReservaService"/> real (no mockeado) apuntando al Postgres del fixture. Mismo
    /// patron que <c>Adr048T3ListInvoicingStatusIntegrationTests.BuildReservaService</c>.
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
