using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TravelApi.Application.DTOs;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Reservations;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Cancellation.Integration;

/// <summary>
/// ADR-048 T5 (2026-07-17, hardening) — MT1 del review backend
/// (<c>docs/architecture/2026-07-17-t5-review-backend.md</c> §6): corre el SQL CRUDO REAL del backfill
/// (las 4 constantes de <see cref="Adr048T5BackfillSql"/>, las MISMAS que usa la migración
/// <c>Adr048_M2_AddDerivedStatusColumnsToReserva</c> — no una copia) contra datos sembrados en Postgres
/// y compara el resultado contra la derivación EN VIVO para el mismo dato.
///
/// <para><b>Por qué hacía falta este archivo (lo que el review encontró)</b>:
/// <c>Adr048T5DerivedAxesIntegrationTests</c> escribe las columnas vía
/// <c>ReservaMoneyPersister.PersistAsync</c> — nunca ejecuta el <c>UPDATE</c>/<c>CASE</c> de la
/// migración. Las 11 pruebas de <c>ReservaDerivedAxesProjectorTests</c> cubren solo el proyector C#. Así,
/// la equivalencia SQL↔C# (el riesgo central de la tanda: dos implementaciones del MISMO criterio, una en
/// SQL y otra en C#, que se pueden desincronizar en silencio) quedaba verificada solo por inspección
/// visual. Este archivo la ejecuta de verdad.</para>
///
/// <para><b>Estrategia por rama</b>: para las 3 ramas que SÍ pueden pasar por el persister real (cobro
/// CON filas hijas, facturación CON comprobantes, facturación SIN comprobantes) se usa el persister como
/// el ORÁCULO de "derivación en vivo" — se corre primero, se anota su resultado, se NULEAN las columnas
/// (simulando el estado ANTES del backfill) y se corre el SQL crudo para comparar. Para la rama de cobro
/// SIN filas hijas (fallback puro — dato legacy de antes de ADR-021, cuando <c>ReservaMoneyByCurrency</c>
/// ni existía) el persister no sirve de oráculo porque CREARÍA las filas hijas al correr; se usa
/// <see cref="ReservaCollectionStatus.Derive(IEnumerable{ReservaCollectionLine})"/> directo, el mismo
/// código que usa <c>FillPorMonedaForListAsync</c> para esa rama.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class Adr048T5BackfillSqlIntegrationTests
    : IClassFixture<PostgresIntegrationFixture>, IAsyncLifetime
{
    private readonly PostgresIntegrationFixture _fixture;

    public Adr048T5BackfillSqlIntegrationTests(PostgresIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => _fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    // ================================================================================================
    // RAMA 1 — eje de COBRO, CON filas hijas en ReservaMoneyByCurrency (backfill 1/4).
    // ================================================================================================

    [Fact]
    public async Task Backfill_EjeDeCobroConFilasHijas_CoincideConLaDerivacionEnVivo()
    {
        int reservaId;
        await using (var seedCtx = _fixture.CreateDbContext())
        {
            var customer = new Customer { FullName = "Cliente MT1-1", TaxCondition = "Consumidor Final", IsActive = true };
            var supplier = new Supplier { Name = "Operador MT1-1", IsActive = true, TaxCondition = "IVA_RESP_INSCRIPTO" };
            seedCtx.Customers.Add(customer);
            seedCtx.Suppliers.Add(supplier);
            await seedCtx.SaveChangesAsync();

            var reserva = new Reserva
            {
                NumeroReserva = "F-MT1-1", Name = "Cobro con filas hijas — ConDeuda",
                Status = EstadoReserva.Confirmed, PayerId = customer.Id,
            };
            seedCtx.Reservas.Add(reserva);
            await seedCtx.SaveChangesAsync();
            reservaId = reserva.Id;

            // Servicio resuelto SIN pago -> ConDeuda. Sale por encima de lo pagado en ambas monedas
            // (ARS con deuda, USD con saldo a favor) para ejercer la prioridad ConDeuda > SaldoAFavor.
            seedCtx.HotelBookings.Add(new HotelBooking
            {
                ReservaId = reserva.Id, SupplierId = supplier.Id, Status = "Confirmado",
                SalePrice = 1000m, NetCost = 700m, Currency = "ARS",
                CheckIn = DateTime.UtcNow.AddDays(10), CheckOut = DateTime.UtcNow.AddDays(15),
            });
            seedCtx.FlightSegments.Add(new FlightSegment
            {
                ReservaId = reserva.Id, SupplierId = supplier.Id, Status = "HK",
                SalePrice = 500m, NetCost = 300m, Currency = "USD",
                DepartureTime = DateTime.UtcNow.AddDays(10),
            });
            await seedCtx.SaveChangesAsync();
            seedCtx.Payments.Add(new Payment
            {
                ReservaId = reserva.Id, Amount = 700m, Currency = "USD", ImputedCurrency = "USD",
                Status = "Paid", IsDeleted = false,
            });
            await seedCtx.SaveChangesAsync();
        }

        // ORACULO: el persister real corre la MISMA cuenta que el listado/detalle usan en vivo.
        string liveCollectionStatus;
        await using (var persistCtx = _fixture.CreateDbContext())
        {
            await ReservaMoneyPersister.PersistAsync(persistCtx, reservaId, CancellationToken.None);
        }
        await using (var readCtx = _fixture.CreateDbContext())
        {
            liveCollectionStatus = (await readCtx.Reservas.AsNoTracking().SingleAsync(r => r.Id == reservaId))
                .DerivedCollectionStatus!;
        }

        // Simular el estado ANTES del backfill: la columna vuelve a null (las filas hijas de
        // ReservaMoneyByCurrency, que YA escribió el persister, quedan intactas — son la fuente que
        // consume la rama 1 del backfill).
        await using (var nullCtx = _fixture.CreateDbContext())
        {
            await nullCtx.Database.ExecuteSqlRawAsync(
                @"UPDATE ""TravelFiles"" SET ""DerivedCollectionStatus"" = NULL WHERE ""Id"" = {0}", reservaId);
        }

        // Correr el SQL CRUDO REAL del backfill (la misma constante que usa la migración).
        await using (var backfillCtx = _fixture.CreateDbContext())
        {
            await backfillCtx.Database.ExecuteSqlRawAsync(Adr048T5BackfillSql.CollectionAxisWithChildRows);
        }

        await using var verifyCtx = _fixture.CreateDbContext();
        var backfilled = await verifyCtx.Reservas.AsNoTracking().SingleAsync(r => r.Id == reservaId);

        Assert.Equal(liveCollectionStatus, backfilled.DerivedCollectionStatus);
        Assert.Equal(ReservaCollectionStatus.WithDebt, backfilled.DerivedCollectionStatus);
    }

    // ================================================================================================
    // RAMA 2 — eje de COBRO, SIN filas hijas (fallback puro, dato legacy pre-ADR-021, backfill 2/4).
    // ================================================================================================

    [Fact]
    public async Task Backfill_EjeDeCobroSinFilasHijas_CoincideConElFallbackEnVivo()
    {
        int reservaId;
        await using (var seedCtx = _fixture.CreateDbContext())
        {
            var customer = new Customer { FullName = "Cliente MT1-2", TaxCondition = "Consumidor Final", IsActive = true };
            seedCtx.Customers.Add(customer);
            await seedCtx.SaveChangesAsync();

            // Reserva "legacy": el escalar Balance/TotalPaid quedó seteado por fuera del persister (dato
            // de antes de ADR-021, cuando ReservaMoneyByCurrency ni existía) — CERO filas hijas.
            var reserva = new Reserva
            {
                NumeroReserva = "F-MT1-2", Name = "Cobro sin filas hijas — SaldoAFavor legacy",
                Status = EstadoReserva.Confirmed, PayerId = customer.Id,
                Balance = -150m, TotalPaid = 150m,
            };
            seedCtx.Reservas.Add(reserva);
            await seedCtx.SaveChangesAsync();
            reservaId = reserva.Id;
        }

        // ORACULO: mismo fallback que usa FillPorMonedaForListAsync para una reserva sin filas hijas
        // (una unica "linea" con el escalar de la cabecera).
        var liveFallback = ReservaCollectionStatus.Derive(
            new[] { new ReservaCollectionLine(balance: -150m, hasCharges: true, hasPayments: true) });

        await using (var backfillCtx = _fixture.CreateDbContext())
        {
            await backfillCtx.Database.ExecuteSqlRawAsync(Adr048T5BackfillSql.CollectionAxisFallback);
        }

        await using var verifyCtx = _fixture.CreateDbContext();
        var backfilled = await verifyCtx.Reservas.AsNoTracking().SingleAsync(r => r.Id == reservaId);

        Assert.Equal(liveFallback, backfilled.DerivedCollectionStatus);
        Assert.Equal(ReservaCollectionStatus.CreditBalance, backfilled.DerivedCollectionStatus);

        // Confirma que la rama SIN filas hijas realmente se ejercitó (no hay ninguna fila para esta
        // reserva en la tabla hija — si la hubiera, habría corrido por la rama 1, no esta).
        var childRowCount = await verifyCtx.ReservaMoneyByCurrency.CountAsync(r => r.ReservaId == reservaId);
        Assert.Equal(0, childRowCount);
    }

    // ================================================================================================
    // RAMA 3 — eje de FACTURACION, CON comprobantes CAE (backfill 3/4). Incluye el caso T3
    // "FullyReturned" y el caso N1 (tipo desconocido con CAE = no cuenta, alineado con el escritor).
    // ================================================================================================

    [Fact]
    public async Task Backfill_EjeDeFacturacionConComprobantes_CoincideConLaDerivacionEnVivo_IncluyendoTipoDesconocido()
    {
        int reservaId;
        await using (var seedCtx = _fixture.CreateDbContext())
        {
            var customer = new Customer { FullName = "Cliente MT1-3", TaxCondition = "Consumidor Final", IsActive = true };
            seedCtx.Customers.Add(customer);
            await seedCtx.SaveChangesAsync();

            var reserva = new Reserva
            {
                NumeroReserva = "F-MT1-3", Name = "Facturacion con comprobantes — FullyReturned",
                Status = EstadoReserva.Confirmed, PayerId = customer.Id, TotalSale = 1000m,
            };
            seedCtx.Reservas.Add(reserva);
            await seedCtx.SaveChangesAsync();
            reservaId = reserva.Id;

            // Factura A + NC A total -> bruto > 0, neto ~ 0 -> "Facturada y devuelta" (T3).
            seedCtx.Invoices.Add(new Invoice
            {
                ReservaId = reserva.Id, TipoComprobante = 1, ImporteTotal = 1000m, Resultado = "A",
            });
            seedCtx.Invoices.Add(new Invoice
            {
                ReservaId = reserva.Id, TipoComprobante = 3, ImporteTotal = 1000m, Resultado = "A",
            });
            // N1 (2026-07-17, review backend): un TipoComprobante FUERA de los 12 conocidos, con CAE
            // aprobado (dato corrupto — no debería existir en un comprobante real). Si el backfill y el
            // escritor go-forward no usaran el MISMO criterio ("desconocido no cuenta"), esta fila
            // cambiaría el resultado de un lado y no del otro. Con el fix, aporta 0 en los dos.
            seedCtx.Invoices.Add(new Invoice
            {
                ReservaId = reserva.Id, TipoComprobante = 999, ImporteTotal = 5000m, Resultado = "A",
            });
            await seedCtx.SaveChangesAsync();
        }

        // ORACULO: el persister real (que incluye los comprobantes) corre la MISMA cuenta que el
        // detalle en vivo.
        string liveInvoicingStatus;
        await using (var persistCtx = _fixture.CreateDbContext())
        {
            await ReservaMoneyPersister.PersistAsync(persistCtx, reservaId, CancellationToken.None);
        }
        await using (var readCtx = _fixture.CreateDbContext())
        {
            liveInvoicingStatus = (await readCtx.Reservas.AsNoTracking().SingleAsync(r => r.Id == reservaId))
                .DerivedInvoicingStatus!;
        }

        await using (var nullCtx = _fixture.CreateDbContext())
        {
            await nullCtx.Database.ExecuteSqlRawAsync(
                @"UPDATE ""TravelFiles"" SET ""DerivedInvoicingStatus"" = NULL WHERE ""Id"" = {0}", reservaId);
        }

        await using (var backfillCtx = _fixture.CreateDbContext())
        {
            await backfillCtx.Database.ExecuteSqlRawAsync(Adr048T5BackfillSql.InvoicingAxisWithInvoices);
        }

        await using var verifyCtx = _fixture.CreateDbContext();
        var backfilled = await verifyCtx.Reservas.AsNoTracking().SingleAsync(r => r.Id == reservaId);

        Assert.Equal(liveInvoicingStatus, backfilled.DerivedInvoicingStatus);
        Assert.Equal(ReservaInvoicingStatus.FullyReturned, backfilled.DerivedInvoicingStatus);
    }

    // ================================================================================================
    // RAMA 4 — eje de FACTURACION, SIN comprobantes con CAE aprobado (backfill 4/4).
    // ================================================================================================

    [Fact]
    public async Task Backfill_EjeDeFacturacionSinComprobantesConCae_EscribeNotInvoiced()
    {
        int reservaId;
        await using (var seedCtx = _fixture.CreateDbContext())
        {
            var customer = new Customer { FullName = "Cliente MT1-4", TaxCondition = "Consumidor Final", IsActive = true };
            seedCtx.Customers.Add(customer);
            await seedCtx.SaveChangesAsync();

            var reserva = new Reserva
            {
                NumeroReserva = "F-MT1-4", Name = "Facturacion sin CAE aprobado — NotInvoiced",
                Status = EstadoReserva.Confirmed, PayerId = customer.Id, TotalSale = 1000m,
            };
            seedCtx.Reservas.Add(reserva);
            await seedCtx.SaveChangesAsync();
            reservaId = reserva.Id;

            // Una factura EN PROCESO (sin CAE aprobado todavia) no debe contar: sigue "Sin facturar".
            seedCtx.Invoices.Add(new Invoice
            {
                ReservaId = reserva.Id, TipoComprobante = 1, ImporteTotal = 1000m, Resultado = "PENDING",
            });
            await seedCtx.SaveChangesAsync();
        }

        await using (var backfillCtx = _fixture.CreateDbContext())
        {
            await backfillCtx.Database.ExecuteSqlRawAsync(Adr048T5BackfillSql.InvoicingAxisFallback);
        }

        await using var verifyCtx = _fixture.CreateDbContext();
        var backfilled = await verifyCtx.Reservas.AsNoTracking().SingleAsync(r => r.Id == reservaId);

        Assert.Equal(ReservaInvoicingStatus.NotInvoiced, backfilled.DerivedInvoicingStatus);
    }
}
