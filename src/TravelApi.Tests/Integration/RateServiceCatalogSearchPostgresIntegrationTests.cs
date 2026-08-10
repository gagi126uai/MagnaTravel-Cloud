using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Helpers;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Integration;

/// <summary>
/// Buscador del catálogo (mejora 2026-08-10): red REAL contra Postgres.
///
/// <para><b>Por que hace falta este archivo</b>: los tests unitarios corren sobre EF Core InMemory, que
/// no tiene pg_trgm — ejercitan el filtro fino en memoria, pero NO la consulta SQL que decide qué
/// productos llegan a ese filtro. Justamente ahí vive lo que se rompió: el buscador viejo medía el
/// parecido del texto ENTERO y "sheraton" no encontraba "sheraton buenos aires hotel &amp; convention
/// center". Estos tests ejecutan la consulta real (word_similarity, operadores % y &lt;%, brazos por
/// palabra, arreglo de tipos) contra un Postgres de verdad.</para>
///
/// <para><b>pg_trgm a mano</b>: la fixture arma el esquema con <c>EnsureCreated()</c>, que construye
/// tablas desde el modelo pero NO corre las migraciones — y la extensión pg_trgm se instala en una
/// migración con SQL crudo. Por eso cada test la crea explícitamente (la imagen postgres:16 la trae en
/// el paquete contrib). Sin esto, el service caería a su fallback ILIKE y estos tests no probarían lo
/// que vinieron a probar.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class RateServiceCatalogSearchPostgresIntegrationTests
    : IClassFixture<PostgresIntegrationFixture>, IAsyncLifetime
{
    private readonly PostgresIntegrationFixture _fixture;

    public RateServiceCatalogSearchPostgresIntegrationTests(PostgresIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        await _fixture.ResetDatabaseAsync();

        await using var ctx = _fixture.CreateDbContext();
        await ctx.Database.ExecuteSqlRawAsync("CREATE EXTENSION IF NOT EXISTS pg_trgm;");
        // El TRUNCATE de la fixture no nombra las tablas del tarifario. Las limpiamos acá para que un
        // test no vea los productos del anterior.
        await ctx.Database.ExecuteSqlRawAsync(
            @"TRUNCATE TABLE ""RateSupplierSales"", ""Rates"" RESTART IDENTITY CASCADE;");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// El service tal cual lo usa el endpoint, pero sin identidad: el enmascarado de costos queda
    /// fail-closed (oculta el costo). Estos tests miran QUE productos encuentra y en qué orden, no los
    /// precios, así que no hace falta armar el stack de permisos.
    /// </summary>
    private static RateService CreateService(AppDbContext ctx)
        => new(ctx, NullLogger<RateService>.Instance);

    /// <summary>Un hotel del tarifario, con el SearchName escrito por la función autoritativa.</summary>
    private static Rate BuildHotelRate(string hotelName, string city, int? supplierId = null)
        => new()
        {
            ServiceType = CatalogServiceTypes.Hotel,
            ProductName = $"Tarifa {hotelName}",
            HotelName = hotelName,
            City = city,
            SupplierId = supplierId,
            NetCost = 100m,
            SalePrice = 160m,
            Currency = "USD",
            PriceUnit = "noche",
            IsActive = true,
            SearchName = TextNormalizer.NormalizeForCatalog(hotelName)
        };

    private static Rate BuildRate(string serviceType, string productName)
        => new()
        {
            ServiceType = serviceType,
            ProductName = productName,
            NetCost = 100m,
            SalePrice = 160m,
            Currency = "USD",
            PriceUnit = "pasajero",
            IsActive = true,
            SearchName = TextNormalizer.NormalizeForCatalog(productName)
        };

    /// <summary>
    /// El escenario base: el Sheraton de nombre larguísimo (el caso que motivó la mejora) y otro
    /// Sheraton en otra ciudad, cada uno con SU operador — uno aprendido de una venta y otro cargado a
    /// mano en la tarifa.
    /// </summary>
    private static async Task<AppDbContext> SeedEscenarioSheratonAsync(PostgresIntegrationFixture fixture)
    {
        var ctx = fixture.CreateDbContext();

        var ola = new Supplier { Name = "Ola Mayorista" };
        var sur = new Supplier { Name = "Turismo Sur" };
        ctx.Suppliers.AddRange(ola, sur);
        await ctx.SaveChangesAsync();

        var largo = BuildHotelRate("Sheraton Buenos Aires Hotel & Convention Center", "Buenos Aires");
        var cordoba = BuildHotelRate("Sheraton Cordoba", "Cordoba", sur.Id);
        ctx.Rates.AddRange(largo, cordoba);
        await ctx.SaveChangesAsync();

        ctx.RateSupplierSales.Add(new RateSupplierSale
        {
            RateId = largo.Id,
            SupplierId = ola.Id,
            LastSoldAt = new DateTime(2026, 5, 22, 0, 0, 0, DateTimeKind.Utc),
            LastNetCost = 100m,
            LastTax = 0m,
            LastSalePrice = 160m,
            LastCurrency = "USD",
            LastPriceUnit = CatalogPriceUnits.NocheHabitacion,
            SalesCount = 1
        });
        await ctx.SaveChangesAsync();

        return ctx;
    }

    /// <summary>
    /// EL caso que motivó la obra: una palabra suelta tiene que encontrar el nombre largo. Con la
    /// consulta vieja (similarity del texto entero >= 0.4) esto daba CERO resultados en producción.
    /// </summary>
    [Fact]
    public async Task Buscar_UnaPalabraSuelta_EncuentraElNombreLargo()
    {
        await using var ctx = await SeedEscenarioSheratonAsync(_fixture);
        var service = CreateService(ctx);

        var result = await service.CatalogSearchAsync(
            CatalogServiceTypes.Hotel, "sheraton", CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, item => item.Name == "Sheraton Buenos Aires Hotel & Convention Center");
    }

    /// <summary>
    /// Error de tipeo: lo tiene que encontrar la consulta SQL (word_similarity), no solo el filtro en
    /// memoria — si el candidato no vuelve de la base, el filtro fino nunca lo ve.
    /// </summary>
    [Fact]
    public async Task Buscar_ConErrorDeTipeo_LoEncuentraLaConsultaSql()
    {
        await using var ctx = await SeedEscenarioSheratonAsync(_fixture);
        var service = CreateService(ctx);

        var result = await service.CatalogSearchAsync(
            CatalogServiceTypes.Hotel, "sheratom", CancellationToken.None);

        Assert.NotEmpty(result);
    }

    /// <summary>
    /// Fix del bloqueante C-2 (review 2026-08-1x): una búsqueda CORTA que solo comparte UNA palabra
    /// con un nombre LARGO ("sheraton eze" contra "Sheraton Buenos Aires Hotel &amp; Convention
    /// Center", que no tiene ningún "eze" en ningún lado) no puede parecer un resultado FUERTE. Antes
    /// del fix, <c>word_similarity()</c> — que compara la palabra contra el MEJOR PEDAZO del nombre,
    /// no contra el nombre entero — inflaba el score del nombre largo por encima del corte de 0.65 con
    /// el que la ficha resalta "el más parecido" y apaga el matcher anti-duplicados (P7). Este test
    /// corre contra Postgres de VERDAD (el bug vivía en <c>word_similarity()</c>, que InMemory no
    /// ejecuta) y falla si el score vuelve a colarse arriba del corte.
    /// </summary>
    [Fact]
    public async Task Buscar_ConCoberturaParcial_NoSuperaElCorteDeParecidoFuerte()
    {
        await using var ctx = await SeedEscenarioSheratonAsync(_fixture);
        var service = CreateService(ctx);

        // Ningún rate del escenario tiene "eze" en ningún lado (nombre, ciudad): para CUALQUIER
        // candidato la búsqueda cubre 1 sola de las 2 palabras escritas.
        var result = await service.CatalogSearchAsync(
            CatalogServiceTypes.Hotel, "sheraton eze", CancellationToken.None);

        Assert.NotEmpty(result);
        Assert.All(result, item =>
        {
            Assert.NotNull(item.Score);
            Assert.True(
                item.Score < 0.65,
                $"score {item.Score} deberia quedar bajo el corte de 0.65 (cobertura parcial: falta 'eze')");
        });
    }

    /// <summary>Escribir las palabras al revés encuentra lo mismo: no hay un orden "correcto".</summary>
    [Fact]
    public async Task Buscar_ConLasPalabrasAlReves_EncuentraIgual()
    {
        await using var ctx = await SeedEscenarioSheratonAsync(_fixture);
        var service = CreateService(ctx);

        var result = await service.CatalogSearchAsync(
            CatalogServiceTypes.Hotel, "aires buenos sheraton", CancellationToken.None);

        var item = Assert.Single(result);
        Assert.Equal("Sheraton Buenos Aires Hotel & Convention Center", item.Name);
    }

    /// <summary>
    /// Sumar el nombre del OPERADOR achica la lista al producto correcto, aunque esa palabra no esté
    /// en ningún lado del nombre del hotel.
    /// </summary>
    [Fact]
    public async Task Buscar_ConElNombreDelOperador_AchicaLaLista()
    {
        await using var ctx = await SeedEscenarioSheratonAsync(_fixture);
        var service = CreateService(ctx);

        var result = await service.CatalogSearchAsync(
            CatalogServiceTypes.Hotel, "sheraton ola", CancellationToken.None);

        var item = Assert.Single(result);
        Assert.Equal("Sheraton Buenos Aires Hotel & Convention Center", item.Name);
    }

    /// <summary>
    /// La búsqueda cruza los tipos y el tipo de la solapa donde está parado el vendedor va primero.
    /// Además: un Hotel y un Paquete que se llaman igual NO se colapsan en el dedupe.
    /// </summary>
    [Fact]
    public async Task Buscar_CruzaTipos_YRespetaElTipoPreferido()
    {
        await using var ctx = _fixture.CreateDbContext();
        ctx.Rates.Add(BuildHotelRate("Bariloche", "Bariloche"));
        ctx.Rates.Add(BuildRate(CatalogServiceTypes.Paquete, "Bariloche"));
        await ctx.SaveChangesAsync();
        var service = CreateService(ctx);

        var desdeHotel = await service.CatalogSearchAsync(
            CatalogServiceTypes.Hotel, "bariloche", CancellationToken.None);
        var desdePaquete = await service.CatalogSearchAsync(
            CatalogServiceTypes.Paquete, "bariloche", CancellationToken.None);

        Assert.Equal(2, desdeHotel.Count);
        Assert.Equal(CatalogServiceTypes.Hotel, desdeHotel[0].ServiceType);

        Assert.Equal(2, desdePaquete.Count);
        Assert.Equal(CatalogServiceTypes.Paquete, desdePaquete[0].ServiceType);
    }

    /// <summary>
    /// La consulta se arma con un pedazo por palabra: con el máximo de palabras tiene que EJECUTAR
    /// bien (parámetros y paréntesis en su lugar). Un error de armado acá sería un 500 en la ficha.
    /// </summary>
    [Fact]
    public async Task Buscar_ConElMaximoDePalabras_LaConsultaEjecutaSinError()
    {
        await using var ctx = await SeedEscenarioSheratonAsync(_fixture);
        var service = CreateService(ctx);

        var textoLargo = "sheraton buenos aires hotel convention center doble desayuno";
        Assert.True(CatalogSearchTokens.Tokenize(textoLargo).Count == CatalogSearchTokens.MaxTokens);

        var result = await service.CatalogSearchAsync(
            CatalogServiceTypes.Hotel, textoLargo, CancellationToken.None);

        // No importa cuántos vuelvan: lo que se prueba es que la consulta corre y no explota.
        Assert.NotNull(result);
    }

    /// <summary>
    /// Los productos inactivos y los de tipos que no tienen solapa en la ficha (Excursion, Otro) no
    /// aparecen: cruzar tipos no significa mostrar todo.
    /// </summary>
    [Fact]
    public async Task Buscar_NoTraeInactivosNiTiposFueraDeLaFicha()
    {
        await using var ctx = _fixture.CreateDbContext();
        var inactivo = BuildHotelRate("Maitei Posadas", "Posadas");
        inactivo.IsActive = false;
        ctx.Rates.Add(inactivo);
        ctx.Rates.Add(BuildRate(CatalogServiceTypes.Excursion, "Maitei Cataratas"));
        ctx.Rates.Add(BuildRate(CatalogServiceTypes.Otro, "Maitei Otro"));
        await ctx.SaveChangesAsync();
        var service = CreateService(ctx);

        var result = await service.CatalogSearchAsync(
            CatalogServiceTypes.Hotel, "maitei", CancellationToken.None);

        Assert.Empty(result);
    }
}
