using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Cancellation.Integration;

/// <summary>
/// FC1.3 Fase 2 Etapa 0 (plan tactico Fase 2 §FC1.3.F2.2 + §FC1.3.F2.5, 2026-05-27):
/// tests de REGRESION para el gap-closing de la migracion <c>Fase2_M1b</c>.
///
/// <para><b>Que protegen</b>:
///  - Que crear una factura (flujo FC1.2) sigue funcionando y nace con
///    <c>MonId = "PES"</c> + <c>MonCotiz = 1</c> (back-compat: nadie tiene que pasar
///    moneda todavia, el comportamiento de pesos es el default).
///  - Que el DEFAULT a nivel BD ('PES' / 1) aplica cuando un INSERT NO especifica las
///    columnas (simula filas legacy / INSERTs que no conocen estas columnas).
///  - Que el indice UNIQUE sobre <c>ArcaIdempotencyKeys."Key"</c> rechaza un segundo
///    INSERT con la misma key (corazon del mecanismo anti-doble-POST de F2.2).</para>
///
/// <para><b>NO testeamos</b> el comportamiento de F2.5 (multimoneda real / XML SOAP):
/// en esta Etapa 0 esas columnas son inertes. Solo verificamos estructura + defaults.</para>
///
/// <para><b>Por que Postgres real (no InMemory)</b>: el DEFAULT a nivel BD y el indice
/// UNIQUE son features SQL que InMemory no ejecuta. Estos tests COMPILAN local (sin
/// Postgres) y CORREN en el VPS con Docker, igual que el resto del modulo FC1.
/// La fixture usa <c>EnsureCreatedAsync</c> (schema desde el modelo), asi que ejercita
/// la config <c>HasDefaultValue</c> de AppDbContext y la migracion queda validada por
/// separado via <c>has-pending-model-changes</c> (sin drift).</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class InvoiceCurrencyAndArcaIdempotencyIntegrationTests
    : IClassFixture<PostgresIntegrationFixture>, IAsyncLifetime
{
    private readonly PostgresIntegrationFixture _fixture;

    public InvoiceCurrencyAndArcaIdempotencyIntegrationTests(PostgresIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => _fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Construye una factura minima como las del flujo FC1.2 (sin tocar MonId/MonCotiz).
    /// ReservaId queda null para no necesitar seedear una Reserva: la moneda es
    /// independiente de la reserva.
    /// </summary>
    private static Invoice BuildFc12StyleInvoice() => new()
    {
        TipoComprobante = 6, // Factura B (consumidor final), tipico FC1.2
        PuntoDeVenta = 1,
        NumeroComprobante = 1,
        CAE = "12345678901234",
        VencimientoCAE = DateTime.UtcNow.AddDays(10),
        Resultado = "A",
        ImporteTotal = 1210m,
        ImporteNeto = 1000m,
        ImporteIva = 210m,
        AnnulmentStatus = AnnulmentStatus.None,
        CreatedAt = DateTime.UtcNow,
        // OJO: a proposito NO seteamos MonId ni MonCotiz, para verificar el default.
    };

    [Fact]
    public async Task Fc12StyleInvoice_PersistsAndDefaultsToPesos()
    {
        // Arrange + Act: persistir una factura tal cual la arma FC1.2 hoy.
        await using (var ctx = _fixture.CreateDbContext())
        {
            ctx.Invoices.Add(BuildFc12StyleInvoice());
            await ctx.SaveChangesAsync();
        }

        // Assert: leer en un context nuevo (sin cache del ChangeTracker) y verificar
        // que la factura nacio en pesos por el default de la entidad.
        await using (var ctx = _fixture.CreateDbContext())
        {
            var invoice = await ctx.Invoices.SingleAsync();
            Assert.Equal("PES", invoice.MonId);
            Assert.Equal(1m, invoice.MonCotiz);
        }
    }

    [Fact]
    public async Task RawInsert_WithoutCurrencyColumns_UsesDatabaseDefault()
    {
        // Este test prueba el DEFAULT a nivel BD (no el default de C#): hace un INSERT
        // crudo que OMITE las columnas MonId/MonCotiz. Si el default de la migracion
        // no estuviera, el INSERT fallaria (NOT NULL sin valor) o dejaria valores
        // inesperados. Simula filas legacy / un INSERT externo que no conoce las columnas.
        await using var ctx = _fixture.CreateDbContext();

        // INSERT minimo que solo llena las columnas NOT NULL preexistentes + deja que
        // MonId/MonCotiz tomen su DEFAULT de BD ('PES' / 1).
        await ctx.Database.ExecuteSqlRawAsync("""
            INSERT INTO "Invoices"
                ("PublicId", "CreatedAt", "TipoComprobante", "PuntoDeVenta",
                 "NumeroComprobante", "ImporteTotal", "ImporteNeto", "ImporteIva",
                 "WasForced", "AnnulmentStatus", "OutstandingBalanceAtIssuance")
            VALUES
                (gen_random_uuid(), now(), 6, 1, 99, 1210, 1000, 210, false, 0, 0);
            """);

        // Leemos las columnas crudas para no depender del default de la entidad C#.
        var monId = await ctx.Database
            .SqlQueryRaw<string>("""SELECT "MonId" AS "Value" FROM "Invoices" WHERE "NumeroComprobante" = 99""")
            .SingleAsync();
        var monCotiz = await ctx.Database
            .SqlQueryRaw<decimal>("""SELECT "MonCotiz" AS "Value" FROM "Invoices" WHERE "NumeroComprobante" = 99""")
            .SingleAsync();

        Assert.Equal("PES", monId);
        Assert.Equal(1m, monCotiz);
    }

    [Fact]
    public async Task ArcaIdempotencyKey_DuplicateKey_RejectedByUniqueIndex()
    {
        // Arrange: insertar una key.
        const string sharedKey = "sha256:dup-test-key";

        await using (var ctx = _fixture.CreateDbContext())
        {
            ctx.ArcaIdempotencyKeys.Add(new ArcaIdempotencyKey
            {
                Key = sharedKey,
                JobId = "job-1",
                CreatedAt = DateTime.UtcNow,
                LastSeenNumeroBeforePost = 41,
            });
            await ctx.SaveChangesAsync();
        }

        // Act + Assert: un segundo INSERT con la MISMA key debe ser rechazado por el
        // indice UNIQUE. EF envuelve la violacion de Postgres en DbUpdateException con
        // un PostgresException (SqlState 23505 = unique_violation) como InnerException.
        await using (var ctx = _fixture.CreateDbContext())
        {
            ctx.ArcaIdempotencyKeys.Add(new ArcaIdempotencyKey
            {
                Key = sharedKey, // misma key -> debe rebotar
                JobId = "job-2",
                CreatedAt = DateTime.UtcNow,
            });

            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
            var pgEx = Assert.IsType<PostgresException>(ex.InnerException);
            Assert.Equal("23505", pgEx.SqlState); // unique_violation
        }

        // Solo quedo la primera key (el segundo INSERT no se persistio).
        await using (var ctx = _fixture.CreateDbContext())
        {
            var keys = await ctx.ArcaIdempotencyKeys.Where(k => k.Key == sharedKey).ToListAsync();
            Assert.Single(keys);
            Assert.Equal("job-1", keys[0].JobId);
        }
    }

    [Fact]
    public async Task ArcaIdempotencyKey_DistinctKeys_BothPersist()
    {
        // Dos keys distintas conviven sin problema (el UNIQUE solo bloquea duplicados).
        await using var ctx = _fixture.CreateDbContext();

        ctx.ArcaIdempotencyKeys.Add(new ArcaIdempotencyKey { Key = "sha256:key-a", CreatedAt = DateTime.UtcNow });
        ctx.ArcaIdempotencyKeys.Add(new ArcaIdempotencyKey { Key = "sha256:key-b", CreatedAt = DateTime.UtcNow });
        await ctx.SaveChangesAsync();

        var count = await ctx.ArcaIdempotencyKeys.CountAsync();
        Assert.Equal(2, count);
    }

    // =========================================================================
    // FC1.3.F2.5 (multimoneda, 2026-05-28): el request lleva MonId/MonCotiz hasta
    // la Invoice persistida. Ejercitamos el AfipService REAL (CreatePendingInvoice
    // NO necesita certificado ni auth: solo BD). El SOAP a ARCA NO se manda aca
    // (lo prueba el AfipServiceMonedaSoapFormatTests a nivel de formato del XML).
    // =========================================================================

    /// <summary>
    /// Stub del protector de datos sensibles (passthrough). CreatePendingInvoice no lo usa
    /// en este flujo, pero el ctor de AfipService lo exige.
    /// </summary>
    private sealed class NoopSensitiveDataProtector : ISensitiveDataProtector
    {
        public string? ProtectString(string? value) => value;
        public string? UnprotectString(string? value) => value;
        public byte[]? ProtectBytes(byte[]? value) => value;
        public byte[]? UnprotectBytes(byte[]? value) => value;
    }

    /// <summary>
    /// Seedea AfipSettings + un Customer + una Reserva (con Payer) y devuelve el ReservaId.
    /// CreatePendingInvoice resuelve la reserva por Id para tomar el cliente/snapshot.
    /// </summary>
    private async Task<int> SeedReservaForPendingInvoiceAsync()
    {
        await using var ctx = _fixture.CreateDbContext();

        if (!await ctx.AfipSettings.AnyAsync())
        {
            ctx.AfipSettings.Add(new AfipSettings
            {
                PuntoDeVenta = 1,
                TaxCondition = "Monotributo", // -> Factura C, simple, no exige datos de cliente RI
            });
        }

        var customer = new Customer
        {
            FullName = "Cliente F2.5",
            TaxCondition = "Consumidor Final",
            IsActive = true,
        };
        ctx.Customers.Add(customer);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = $"F-CUR-{Guid.NewGuid().ToString("N")[..8]}",
            Name = "Reserva moneda test",
            Status = EstadoReserva.Confirmed,
            PayerId = customer.Id,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();
        return reserva.Id;
    }

    private AfipService BuildRealAfipService(AppDbContext context)
        => new(
            context,
            NullLogger<AfipService>.Instance,
            new HttpClient(),
            new NoopSensitiveDataProtector(),
            auditService: null);

    private static CreateInvoiceRequest BuildSingleLineRequest(string reservaId)
        => new()
        {
            ReservaId = reservaId,
            Items = new List<InvoiceItemDto>
            {
                new()
                {
                    Description = "Hotel",
                    Quantity = 1m,
                    UnitPrice = 1_000m,
                    Total = 1_000m,
                    AlicuotaIvaId = 5, // 21%
                },
            },
        };

    /// <summary>
    /// Fc12NormalInvoice_StillEmitsWithPesos (regresion FC1.2): el caller NO setea
    /// MonId/MonCotiz en el request -> la Invoice persistida nace en PES/1 (defaults).
    /// El comportamiento de pesos de FC1.2 queda intacto.
    /// </summary>
    [Fact]
    public async Task Fc12NormalInvoice_StillEmitsWithPesos()
    {
        var reservaId = await SeedReservaForPendingInvoiceAsync();

        await using var ctx = _fixture.CreateDbContext();
        var afip = BuildRealAfipService(ctx);

        // Request estilo FC1.2: sin MonId/MonCotiz -> defaults del DTO ("PES", 1).
        var request = BuildSingleLineRequest(reservaId.ToString());

        var invoice = await afip.CreatePendingInvoice(reservaId, request);

        await using var verifyCtx = _fixture.CreateDbContext();
        var persisted = await verifyCtx.Invoices.AsNoTracking().FirstAsync(i => i.Id == invoice.Id);
        Assert.Equal("PES", persisted.MonId);
        Assert.Equal(1m, persisted.MonCotiz);
    }

    /// <summary>
    /// PartialCreditNoteUsd_EmitsWithDolarAndSnapshotRate (a nivel persistencia): un request
    /// con MonId="DOL" + MonCotiz=1234.56 (como lo arma EmitPartialCreditNoteAsync para una
    /// factura USD) hace que la Invoice nazca con esos valores. De ahi ProcessInvoiceJob los
    /// relee para el SOAP.
    /// </summary>
    [Fact]
    public async Task PartialCreditNoteUsd_PersistsWithDolarAndSnapshotRate()
    {
        var reservaId = await SeedReservaForPendingInvoiceAsync();

        await using var ctx = _fixture.CreateDbContext();
        var afip = BuildRealAfipService(ctx);

        var request = BuildSingleLineRequest(reservaId.ToString());
        request.MonId = "DOL";
        request.MonCotiz = 1234.56m;

        var invoice = await afip.CreatePendingInvoice(reservaId, request);

        await using var verifyCtx = _fixture.CreateDbContext();
        var persisted = await verifyCtx.Invoices.AsNoTracking().FirstAsync(i => i.Id == invoice.Id);
        Assert.Equal("DOL", persisted.MonId);
        Assert.Equal(1234.56m, persisted.MonCotiz);
    }
}
