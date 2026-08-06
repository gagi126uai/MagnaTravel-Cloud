using System;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Services;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Integration;

/// <summary>
/// "Facturas en dólares" (spec firmada 2026-08-06, Parte B) contra Postgres REAL.
///
/// <para><b>Por qué hace falta este test y no alcanza con los unitarios</b>: la consulta del reporte
/// filtra el período por el día de emisión, que se arma con dos "si no hay, usá el siguiente"
/// encadenados (<c>CbteFchArgentina ?? IssuedAt ?? CreatedAt</c>). El proveedor InMemory de la suite
/// unit NO traduce nada a SQL — ejecuta la expresión como C# directo, así que una expresión no
/// traducible pasa los tests unitarios y explota recién en producción. Es exactamente la trampa que ya
/// mordió a este repo con el <c>ORDER BY CASE</c> del resolver de cotizaciones y con el buscador
/// global. Este test corre la consulta tal cual contra Postgres: si algo no se traduce, revienta acá.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class UsdInvoicesReportSqlTranslationIntegrationTests
    : IClassFixture<PostgresIntegrationFixture>, IAsyncLifetime
{
    private readonly PostgresIntegrationFixture _fixture;

    public UsdInvoicesReportSqlTranslationIntegrationTests(PostgresIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => _fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static ReportService BuildService(TravelApi.Infrastructure.Persistence.AppDbContext ctx)
    {
        var bna = new Mock<IBnaExchangeRateService>();
        bna.Setup(b => b.GetUsdSellerRateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((BnaUsdSellerRateDto?)null);
        return new ReportService(ctx, bna.Object);
    }

    [Fact]
    public async Task ConDatosReales_LaConsultaSeTraduceASql_YCalculaLaDiferencia()
    {
        await using var ctx = _fixture.CreateDbContext();

        var reserva = new Reserva
        {
            PublicId = Guid.NewGuid(),
            NumeroReserva = "R-USD-INT-1",
            Name = "Reserva en dólares",
            Status = EstadoReserva.Confirmed
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var invoice = new Invoice
        {
            PublicId = Guid.NewGuid(),
            ReservaId = reserva.Id,
            TipoComprobante = 6,
            PuntoDeVenta = 1,
            NumeroComprobante = 12,
            Resultado = "A",
            MonId = "DOL",
            MonCotiz = 1234.50m,
            ImporteTotal = 1000m,
            CbteFchArgentina = new DateTime(2026, 08, 06, 0, 0, 0, DateTimeKind.Utc),
            CreatedAt = new DateTime(2026, 08, 06, 12, 0, 0, DateTimeKind.Utc)
        };
        ctx.Invoices.Add(invoice);
        await ctx.SaveChangesAsync();

        ctx.Payments.Add(new Payment
        {
            ReservaId = reserva.Id,
            LinkedInvoiceId = invoice.Id,
            Amount = 1_500_000m,
            Currency = "ARS",
            Status = "Paid",
            AffectsCash = true,
            PaidAt = new DateTime(2026, 08, 06, 15, 0, 0, DateTimeKind.Utc)
        });
        await ctx.SaveChangesAsync();

        var reporte = await BuildService(ctx).GetUsdInvoicesReportAsync(
            new DateTime(2026, 08, 01, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 08, 31, 23, 59, 59, DateTimeKind.Utc),
            CancellationToken.None);

        var fila = Assert.Single(reporte.Filas);
        Assert.Equal(1_234_500m, fila.PesosDeLaFactura);
        Assert.Equal(1_500_000m, fila.PesosCobrados);
        Assert.Equal(265_500m, fila.Diferencia);
    }

    /// <summary>
    /// Con la base vacía la consulta igual tiene que EJECUTARSE contra Postgres (es donde salta una
    /// expresión no traducible), aunque no devuelva filas.
    /// </summary>
    [Fact]
    public async Task ConLaBaseVacia_LaConsultaCorreSinExplotar()
    {
        await using var ctx = _fixture.CreateDbContext();

        var reporte = await BuildService(ctx).GetUsdInvoicesReportAsync(null, null, CancellationToken.None);

        Assert.Empty(reporte.Filas);
    }
}
