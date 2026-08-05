using System;
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
/// Fix bug "Falta facturar" fantasma (2026-08-05, F-9), hallazgo B3 del review backend: prueba la
/// migración de RE-PROYECCIÓN (<c>20260805171705_Adr048T5_M_ReprojectInvoicingAxisAllReservas</c>) contra
/// Postgres real. Corre el MISMO SQL que ejecuta su <c>Up()</c> (las constantes de
/// <see cref="Adr048T5BackfillSql"/>, ya corregidas a <c>ConfirmedSale</c> — no una copia).
///
/// <para><b>Qué diferencia esto de <c>Adr048T5BackfillSqlIntegrationTests</c></b>: esos tests arrancan
/// SIEMPRE desde <c>DerivedInvoicingStatus = NULL</c> (primera materialización). Esta clase arranca desde
/// un valor YA MATERIALIZADO con el criterio VIEJO (simula el estado real de PROD hoy: filas que el
/// backfill original de 2026-07-17 llenó comparando contra <c>TotalSale</c>) y prueba que la
/// re-proyección lo CORRIGE. También prueba idempotencia: correr el mismo SQL dos veces da el mismo
/// resultado.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class Adr048T5ReprojectInvoicingAxisMigrationIntegrationTests
    : IClassFixture<PostgresIntegrationFixture>, IAsyncLifetime
{
    private readonly PostgresIntegrationFixture _fixture;

    public Adr048T5ReprojectInvoicingAxisMigrationIntegrationTests(PostgresIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => _fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// EL CASO EXACTO DEL BUG, ya materializado con el criterio viejo: reserva con un servicio cotizado
    /// pero NUNCA confirmado (TotalSale = 100.000, ConfirmedSale = 0) y una factura viva de 40.000. El
    /// backfill ORIGINAL (2026-07-17, contra TotalSale) la habría dejado en "PartiallyInvoiced" (40.000 &lt;
    /// 100.000 — mintiendo que falta facturar 60.000 de algo que nunca fue firme). Sembramos esa columna
    /// YA con ese valor viejo (simulando PROD hoy) y corremos la re-proyección: tiene que corregirla a
    /// "FullyInvoiced" (40.000 &gt;= ConfirmedSale 0 — la señal correcta: se facturó de más).
    /// </summary>
    [Fact]
    public async Task Reproyeccion_CorrigeFilaMaterializadaConCriterioViejo_DePartiallyInvoicedAFullyInvoiced()
    {
        int reservaId;
        await using (var seedCtx = _fixture.CreateDbContext())
        {
            var customer = new Customer { FullName = "Cliente B3", TaxCondition = "Consumidor Final", IsActive = true };
            seedCtx.Customers.Add(customer);
            await seedCtx.SaveChangesAsync();

            var reserva = new Reserva
            {
                NumeroReserva = "F-B3-1", Name = "Reserva con criterio materializado viejo",
                Status = EstadoReserva.Confirmed, PayerId = customer.Id,
                TotalSale = 100_000m, ConfirmedSale = 0m,
                // Simula lo que dejó el backfill ORIGINAL (comparando contra TotalSale) en PROD hoy: el
                // valor VIEJO, ya materializado, que nadie va a re-proyectar hasta que la reserva vuelva a
                // mover plata o facturar de nuevo.
                DerivedInvoicingStatus = ReservaInvoicingStatus.PartiallyInvoiced,
            };
            seedCtx.Reservas.Add(reserva);
            await seedCtx.SaveChangesAsync();
            reservaId = reserva.Id;

            seedCtx.Invoices.Add(new Invoice
            {
                ReservaId = reserva.Id, TipoComprobante = 1, ImporteTotal = 40_000m, Resultado = "A",
            });
            await seedCtx.SaveChangesAsync();
        }

        // Corre el MISMO SQL que la migración de re-proyección (su Up() llama a estas dos constantes, en
        // este orden).
        await using (var migrateCtx = _fixture.CreateDbContext())
        {
            await migrateCtx.Database.ExecuteSqlRawAsync(Adr048T5BackfillSql.InvoicingAxisWithInvoices);
            await migrateCtx.Database.ExecuteSqlRawAsync(Adr048T5BackfillSql.InvoicingAxisFallback);
        }

        await using (var verifyCtx = _fixture.CreateDbContext())
        {
            var corrected = await verifyCtx.Reservas.AsNoTracking().SingleAsync(r => r.Id == reservaId);
            Assert.Equal(ReservaInvoicingStatus.FullyInvoiced, corrected.DerivedInvoicingStatus);
        }

        // Idempotencia (B3: "correrla dos veces da lo mismo"): re-ejecutar el mismo SQL no cambia el
        // resultado ya corregido.
        await using (var migrateAgainCtx = _fixture.CreateDbContext())
        {
            await migrateAgainCtx.Database.ExecuteSqlRawAsync(Adr048T5BackfillSql.InvoicingAxisWithInvoices);
            await migrateAgainCtx.Database.ExecuteSqlRawAsync(Adr048T5BackfillSql.InvoicingAxisFallback);
        }

        await using var finalCtx = _fixture.CreateDbContext();
        var stillCorrect = await finalCtx.Reservas.AsNoTracking().SingleAsync(r => r.Id == reservaId);
        Assert.Equal(ReservaInvoicingStatus.FullyInvoiced, stillCorrect.DerivedInvoicingStatus);
    }

    /// <summary>
    /// Reserva SIN ningún comprobante con CAE aprobado, con la columna ya materializada (en cualquier
    /// valor previo, simulando dato sucio o legacy): la re-proyección la deja en "NotInvoiced" — la rama
    /// fallback no dependía de TotalSale/ConfirmedSale, así que esto prueba que la re-proyección cubre TODAS
    /// las reservas (no solo las que tienen factura), sin dejar filas afuera de las dos sentencias.
    /// </summary>
    [Fact]
    public async Task Reproyeccion_ReservaSinComprobantesAprobados_QuedaNotInvoiced()
    {
        int reservaId;
        await using (var seedCtx = _fixture.CreateDbContext())
        {
            var customer = new Customer { FullName = "Cliente B3-2", TaxCondition = "Consumidor Final", IsActive = true };
            seedCtx.Customers.Add(customer);
            await seedCtx.SaveChangesAsync();

            var reserva = new Reserva
            {
                NumeroReserva = "F-B3-2", Name = "Reserva sin comprobantes, columna sucia",
                Status = EstadoReserva.Confirmed, PayerId = customer.Id,
                TotalSale = 5_000m, ConfirmedSale = 0m,
                // Dato sucio a proposito: un valor que NO le corresponde, para probar que la re-proyeccion
                // lo pisa igual (no es "solo rellena si esta NULL").
                DerivedInvoicingStatus = ReservaInvoicingStatus.FullyInvoiced,
            };
            seedCtx.Reservas.Add(reserva);
            await seedCtx.SaveChangesAsync();
            reservaId = reserva.Id;
        }

        await using (var migrateCtx = _fixture.CreateDbContext())
        {
            await migrateCtx.Database.ExecuteSqlRawAsync(Adr048T5BackfillSql.InvoicingAxisWithInvoices);
            await migrateCtx.Database.ExecuteSqlRawAsync(Adr048T5BackfillSql.InvoicingAxisFallback);
        }

        await using var verifyCtx = _fixture.CreateDbContext();
        var corrected = await verifyCtx.Reservas.AsNoTracking().SingleAsync(r => r.Id == reservaId);
        Assert.Equal(ReservaInvoicingStatus.NotInvoiced, corrected.DerivedInvoicingStatus);
    }
}
