using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Helpers;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-017 F1.2 (catalogo find-or-create, buscador): tests UNITARIOS de <c>catalog-search</c>.
///
/// <para>Estos tests corren sobre EF Core InMemory, asi que NO ejercitan pg_trgm (la pesca ancha
/// contra Postgres se prueba en <c>RateServiceCatalogSearchPostgresIntegrationTests</c>). InMemory
/// dispara el fallback en memoria del service, que pasa por EL MISMO filtro fino que el camino real:
/// alcanza para verificar cobertura de palabras, puntaje, dedupe, orden y enmascarado de costo.</para>
/// </summary>
public class RateServiceCatalogSearchTests
{
    private const string SeeCostPermission = Permissions.CobranzasSeeCost;

    /// <summary>
    /// El mismo corte que usa la ficha (<c>STRONG_MATCH_THRESHOLD</c> de ProductSearchField.jsx) para
    /// resaltar el primer resultado y para no molestar al motor anti-duplicados. Esta duplicado a
    /// proposito: es un CONTRATO entre el motor y la pantalla, y si alguien mueve el numero de un lado
    /// estos tests lo hacen visible.
    /// </summary>
    private const double StrongMatchThreshold = 0.65;

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    // Construye el service con el flag prendido/apagado y el permiso de costos a eleccion.
    private static RateService CreateService(
        AppDbContext context,
        bool catalogEnabled,
        bool canSeeCost = true,
        bool isAdmin = false,
        bool withIdentity = true)
    {
        const string userId = "vendedor-test";
        IHttpContextAccessor? accessor = null;
        IUserPermissionResolver? resolver = null;
        if (withIdentity)
        {
            accessor = isAdmin
                ? BuildHttpContextAccessor(userId, "Admin")
                : BuildHttpContextAccessor(userId);
            resolver = canSeeCost
                ? BuildResolver(userId, SeeCostPermission)
                : BuildResolver(userId);
        }

        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings
            .Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings { EnableCatalogFindOrCreate = catalogEnabled });

        return new RateService(
            context, NullLogger<RateService>.Instance, resolver, accessor, settings.Object);
    }

    private static IHttpContextAccessor BuildHttpContextAccessor(string userId, params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        foreach (var role in roles) claims.Add(new Claim(ClaimTypes.Role, role));
        return new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
            }
        };
    }

    private static IUserPermissionResolver BuildResolver(string userId, params string[] permissions)
    {
        var mock = new Mock<IUserPermissionResolver>();
        IReadOnlySet<string> set = new HashSet<string>(permissions);
        mock.Setup(r => r.GetPermissionsAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(set);
        return mock.Object;
    }

    // Crea un Rate de hotel con SearchName ya normalizado (como lo deja el backfill / la app).
    private static Rate BuildHotelRate(
        int id, string hotelName, string city, decimal netCost = 100m, decimal salePrice = 160m)
    {
        return new Rate
        {
            Id = id,
            ServiceType = "Hotel",
            ProductName = $"Tarifa {hotelName}",
            HotelName = hotelName,
            City = city,
            RoomType = "Doble",
            NetCost = netCost,
            Tax = 15m,
            SalePrice = salePrice,
            Commission = salePrice - netCost - 15m,
            Currency = "ARS",
            PriceUnit = "noche",
            HotelPriceType = "base_doble",
            IsActive = true,
            // SearchName se calcula con la MISMA funcion autoritativa que usa la app.
            SearchName = TextNormalizer.NormalizeForCatalog(hotelName)
        };
    }

    /// <summary>Un producto de cualquier otro tipo (Aereo, Paquete, ...), con lo minimo para buscarlo.</summary>
    private static Rate BuildRate(int id, string serviceType, string productName)
    {
        return new Rate
        {
            Id = id,
            ServiceType = serviceType,
            ProductName = productName,
            SalePrice = 500m,
            Currency = "ARS",
            IsActive = true,
            SearchName = TextNormalizer.NormalizeForCatalog(productName)
        };
    }

    /// <summary>
    /// Dos hoteles que se llaman parecido pero se le compran a operadores DISTINTOS: uno con el
    /// operador aprendido de una venta (<c>RateSupplierSale</c>) y otro con el operador cargado a mano
    /// en la tarifa vieja (<c>Rate.Supplier</c>). Asi los tests ejercitan las DOS fuentes de nombres
    /// de operador que mira el buscador.
    /// </summary>
    private static async Task SeedDosSheratonConOperadoresDistintosAsync(AppDbContext context)
    {
        context.Suppliers.Add(new Supplier { Id = 1, Name = "Ola Mayorista" });
        context.Suppliers.Add(new Supplier { Id = 2, Name = "Turismo Sur" });

        context.Rates.Add(BuildHotelRate(
            1, "Sheraton Buenos Aires Hotel & Convention Center", "Buenos Aires"));
        var cordoba = BuildHotelRate(2, "Sheraton Cordoba", "Cordoba");
        cordoba.SupplierId = 2; // operador cargado a mano en la tarifa vieja
        context.Rates.Add(cordoba);

        // El operador del primero se aprendio vendiendolo (es el camino nuevo del tarifario).
        context.RateSupplierSales.Add(new RateSupplierSale
        {
            Id = 1,
            RateId = 1,
            SupplierId = 1,
            LastSoldAt = new DateTime(2026, 5, 22, 0, 0, 0, DateTimeKind.Utc),
            LastNetCost = 100m,
            LastSalePrice = 160m,
            LastCurrency = "USD",
            LastPriceUnit = "noche_habitacion",
            SalesCount = 1
        });

        await context.SaveChangesAsync();
    }

    // ============================= R4 — gate por flag =============================

    [Fact]
    public async Task CatalogSearch_FlagOn_ReturnsResults()
    {
        await using var context = CreateContext();
        context.Rates.Add(BuildHotelRate(1, "Hotel Maitei", "Posadas"));
        await context.SaveChangesAsync();
        var service = CreateService(context, catalogEnabled: true);

        var result = await service.CatalogSearchAsync("Hotel", "maitei", CancellationToken.None);

        Assert.NotNull(result);
        var item = Assert.Single(result!);
        Assert.Equal("Hotel Maitei", item.Name);
        Assert.Equal("Posadas", item.Subtitle);
    }

    [Fact]
    public async Task CatalogSearch_QueryTooShort_ReturnsEmpty_NotNull()
    {
        await using var context = CreateContext();
        context.Rates.Add(BuildHotelRate(1, "Hotel Maitei", "Posadas"));
        await context.SaveChangesAsync();
        var service = CreateService(context, catalogEnabled: true);

        var result = await service.CatalogSearchAsync("Hotel", "m", CancellationToken.None);

        // Con flag ON pero q corta: lista vacia (NO null: el endpoint existe, solo no hay que buscar).
        Assert.NotNull(result);
        Assert.Empty(result!);
    }

    // ============================= tipo PREFERIDO, ya no filtro (2026-08-10) =============================

    /// <summary>
    /// INVERSION del test viejo (<c>CatalogSearch_FiltersByServiceType</c>): antes el tipo escondia
    /// todo lo demas y este mismo caso devolvia UN resultado. Ahora la busqueda cruza los tipos y
    /// devuelve los dos "Maitei" — pero el de la solapa donde esta parado el vendedor va PRIMERO.
    /// </summary>
    [Fact]
    public async Task CatalogSearch_CruzaTipos_YElTipoDeLaSolapaVaPrimero()
    {
        await using var context = CreateContext();
        context.Rates.Add(BuildHotelRate(1, "Maitei", "Posadas"));
        context.Rates.Add(BuildRate(2, "Aereo", "Maitei Air EZE-MIA"));
        await context.SaveChangesAsync();
        var service = CreateService(context, catalogEnabled: true);

        var result = await service.CatalogSearchAsync("Hotel", "maitei", CancellationToken.None);

        Assert.Equal(2, result!.Count);
        Assert.Equal("Hotel", result[0].ServiceType);

        // La misma busqueda parado en la solapa de Aereos da vuelta el orden, sin esconder nada.
        var fromFlightTab = await service.CatalogSearchAsync("Aereo", "maitei", CancellationToken.None);

        Assert.Equal(2, fromFlightTab!.Count);
        Assert.Equal("Aereo", fromFlightTab[0].ServiceType);
    }

    /// <summary>
    /// INVERSION del comportamiento viejo: sin tipo de servicio el buscador devolvia lista vacia
    /// (el tipo era obligatorio). Ahora busca igual en los 5 tipos de la ficha.
    /// </summary>
    [Fact]
    public async Task CatalogSearch_SinTipoDeServicio_BuscaIgual()
    {
        await using var context = CreateContext();
        context.Rates.Add(BuildHotelRate(1, "Maitei", "Posadas"));
        await context.SaveChangesAsync();
        var service = CreateService(context, catalogEnabled: true);

        var result = await service.CatalogSearchAsync(null, "maitei", CancellationToken.None);

        var item = Assert.Single(result!);
        Assert.Equal("Maitei", item.Name);
    }

    /// <summary>
    /// La palabra del TIPO tambien se busca: escribir "aereo maitei" deja afuera al hotel homonimo
    /// sin que el vendedor tenga que filtrar nada.
    /// </summary>
    [Fact]
    public async Task CatalogSearch_LaPalabraDelTipoAchicaLaLista()
    {
        await using var context = CreateContext();
        context.Rates.Add(BuildHotelRate(1, "Maitei", "Posadas"));
        context.Rates.Add(BuildRate(2, "Aereo", "Maitei Air EZE-MIA"));
        await context.SaveChangesAsync();
        var service = CreateService(context, catalogEnabled: true);

        var result = await service.CatalogSearchAsync("Hotel", "aereo maitei", CancellationToken.None);

        var item = Assert.Single(result!);
        Assert.Equal("Aereo", item.ServiceType);
    }

    // ============================= todas las palabras, con degrade =============================

    /// <summary>
    /// La faceta escondida del buscador: "sheraton ola" junta el NOMBRE del producto con el
    /// OPERADOR con el que se le compra. El otro Sheraton, que no tiene nada que ver con Ola,
    /// desaparece de la lista.
    /// </summary>
    [Fact]
    public async Task CatalogSearch_PalabraDelOperador_AchicaLaLista()
    {
        await using var context = CreateContext();
        await SeedDosSheratonConOperadoresDistintosAsync(context);
        var service = CreateService(context, catalogEnabled: true);

        var result = await service.CatalogSearchAsync("Hotel", "sheraton ola", CancellationToken.None);

        var item = Assert.Single(result!);
        Assert.Equal("Sheraton Buenos Aires Hotel & Convention Center", item.Name);
    }

    /// <summary>
    /// Degrade: si NINGUN producto cubre todo lo escrito, no se esconde nada — se muestran los que
    /// cubren lo que se pueda. Dejar la lista vacia teniendo candidatos empujaria a crear un
    /// duplicado (P7).
    /// </summary>
    [Fact]
    public async Task CatalogSearch_SiNadieCubreTodo_MuestraLosQueCubrenAlgo()
    {
        await using var context = CreateContext();
        await SeedDosSheratonConOperadoresDistintosAsync(context);
        var service = CreateService(context, catalogEnabled: true);

        var result = await service.CatalogSearchAsync("Hotel", "sheraton mendoza", CancellationToken.None);

        Assert.Equal(2, result!.Count);
    }

    /// <summary>
    /// Un pedazo del nombre encuentra el nombre largo. Es EL caso que motivo la mejora: con la medida
    /// vieja (parecido del texto entero >= 0.4) "sheraton" no encontraba
    /// "sheraton buenos aires hotel &amp; convention center".
    /// </summary>
    [Fact]
    public async Task CatalogSearch_PedazoDelNombre_EncuentraElNombreLargo()
    {
        await using var context = CreateContext();
        await SeedDosSheratonConOperadoresDistintosAsync(context);
        var service = CreateService(context, catalogEnabled: true);

        var result = await service.CatalogSearchAsync("Hotel", "sheraton", CancellationToken.None);

        Assert.Equal(2, result!.Count);
    }

    /// <summary>
    /// Error de tipeo: "sheratom" encuentra igual. Como hubo que perdonar el tipeo, el puntaje queda
    /// ABAJO del corte con el que la ficha resalta "el mas parecido" — sigue siendo una sugerencia.
    /// </summary>
    [Fact]
    public async Task CatalogSearch_ConErrorDeTipeo_EncuentraPeroConPuntajeDeSugerencia()
    {
        await using var context = CreateContext();
        await SeedDosSheratonConOperadoresDistintosAsync(context);
        var service = CreateService(context, catalogEnabled: true);

        var result = await service.CatalogSearchAsync("Hotel", "sheratom", CancellationToken.None);

        Assert.NotEmpty(result!);
        Assert.All(result!, item =>
        {
            Assert.NotNull(item.Score);
            Assert.True(item.Score < StrongMatchThreshold, $"score {item.Score} deberia estar bajo el corte");
        });
    }

    // ============================= contrato del puntaje con la ficha =============================

    /// <summary>
    /// CONTRATO con la pantalla y con el motor de IA: la ficha resalta el primer resultado (y NO
    /// consulta al motor anti-duplicados) cuando el puntaje llega a 0.65. Un producto que tiene TODAS
    /// las palabras escritas, tal cual, tiene que superar ese corte.
    /// </summary>
    [Fact]
    public async Task CatalogSearch_CoberturaExactaDeTodasLasPalabras_SuperaElCorteDeLaFicha()
    {
        await using var context = CreateContext();
        context.Rates.Add(BuildHotelRate(1, "Hotel Maitei", "Posadas"));
        await context.SaveChangesAsync();
        var service = CreateService(context, catalogEnabled: true);

        var result = await service.CatalogSearchAsync("Hotel", "hotel maitei", CancellationToken.None);

        var item = Assert.Single(result!);
        Assert.NotNull(item.Score);
        Assert.True(item.Score >= StrongMatchThreshold, $"score {item.Score} deberia superar el corte");
    }

    /// <summary>
    /// El empujon del tipo de la solapa es SOLO para ordenar: no se le suma al puntaje que viaja al
    /// front (si se le sumara, un producto flojo del tipo correcto se resaltaria como "el mas
    /// parecido" solo por estar en la solapa).
    /// </summary>
    [Fact]
    public async Task CatalogSearch_ElEmpujonDelTipo_NoEnsuciaElPuntaje()
    {
        await using var context = CreateContext();
        context.Rates.Add(BuildHotelRate(1, "Maitei", "Posadas"));
        context.Rates.Add(BuildRate(2, "Aereo", "Maitei Air EZE-MIA"));
        await context.SaveChangesAsync();
        var service = CreateService(context, catalogEnabled: true);

        var result = await service.CatalogSearchAsync("Hotel", "maitei", CancellationToken.None);

        // Los dos cubren la unica palabra escrita, tal cual: mismo puntaje, distinto orden.
        Assert.Equal(2, result!.Count);
        Assert.Equal(result[0].Score, result[1].Score);
    }

    // ============================= C-2 (bloqueante, review 2026-08-1x): clamp de score parcial =============================

    /// <summary>
    /// El fix del bloqueante C-2: <c>word_similarity()</c> de Postgres compara la palabra escrita
    /// contra el MEJOR PEDAZO del nombre, no contra el nombre entero. Con una busqueda corta ("sheraton
    /// eze") eso puede inflar el score contra un nombre LARGO que en realidad solo comparte UNA de las
    /// dos palabras escritas (el nombre ni menciona "eze"). Test UNITARIO del clamp en si mismo (sin
    /// Postgres): arma un <c>CatalogMatch</c> a mano con un score alto pero cobertura PARCIAL, y
    /// verifica que <c>ApplyCoverageScores</c> lo recorte por debajo del corte de "parecido fuerte" de
    /// la ficha (el caso con Postgres de verdad esta en
    /// <c>RateServiceCatalogSearchPostgresIntegrationTests</c>).
    /// </summary>
    [Fact]
    public void ApplyCoverageScores_CoberturaParcialConScoreAlto_SeRecortaBajoElCorte()
    {
        var item = new CatalogSearchItemDto
        {
            RatePublicId = Guid.NewGuid(),
            ServiceType = "Hotel",
            Name = "Sheraton Buenos Aires Hotel & Convention Center",
            // Score CRUDO que devolveria Postgres para "sheraton eze" contra este nombre largo: alto
            // pese a que el nombre ni menciona "eze".
            Score = 0.69,
        };
        // Cubre 1 de las 2 palabras escritas ("sheraton" si, "eze" no): cobertura PARCIAL.
        var match = new RateService.CatalogMatch(item, MatchedTokens: 1, ExactMatchedTokens: 1);

        RateService.ApplyCoverageScores(new[] { match }, tokenCount: 2);

        Assert.NotNull(item.Score);
        Assert.True(item.Score < StrongMatchThreshold, $"score {item.Score} deberia quedar bajo el corte de 0.65");
    }

    /// <summary>
    /// Contraparte del test de arriba: si el producto SI cubre todas las palabras escritas, el clamp
    /// NO se aplica — el score sigue siendo el mayor entre lo que midio Postgres y el de cobertura
    /// completa, exactamente como antes del fix.
    /// </summary>
    [Fact]
    public void ApplyCoverageScores_CoberturaCompleta_NoRecortaElScore()
    {
        var item = new CatalogSearchItemDto
        {
            RatePublicId = Guid.NewGuid(),
            ServiceType = "Hotel",
            Name = "Sheraton Buenos Aires",
            Score = 0.9,
        };
        var match = new RateService.CatalogMatch(item, MatchedTokens: 2, ExactMatchedTokens: 2);

        RateService.ApplyCoverageScores(new[] { match }, tokenCount: 2);

        Assert.Equal(0.9, item.Score);
    }

    // ============================= dedupe con tipos cruzados =============================

    /// <summary>
    /// Con la busqueda cruzando tipos, un Hotel y un Paquete que se llaman IGUAL son dos productos
    /// distintos: si la clave de dedupe no llevara el tipo adelante, uno de los dos desapareceria.
    /// </summary>
    [Fact]
    public async Task CatalogSearch_HotelYPaqueteConElMismoNombre_NoSeColapsan()
    {
        await using var context = CreateContext();
        context.Rates.Add(BuildHotelRate(1, "Bariloche", "Bariloche"));
        context.Rates.Add(BuildRate(2, "Paquete", "Bariloche"));
        await context.SaveChangesAsync();
        var service = CreateService(context, catalogEnabled: true);

        var result = await service.CatalogSearchAsync("Hotel", "bariloche", CancellationToken.None);

        Assert.Equal(2, result!.Count);
        Assert.Contains(result!, item => item.ServiceType == "Hotel");
        Assert.Contains(result!, item => item.ServiceType == "Paquete");
    }

    // ============================= R5 / m1 — dedupe =============================

    [Fact]
    public async Task CatalogSearch_SameHotelLoadedManyTimes_ReturnsOneResult()
    {
        await using var context = CreateContext();
        // Tres tarifas del MISMO hotel (distinto room type) -> un solo producto en el dropdown.
        context.Rates.Add(BuildHotelRate(1, "Hotel Maitei", "Posadas"));
        var second = BuildHotelRate(2, "Hotel Maitei", "Posadas");
        second.RoomType = "Triple";
        context.Rates.Add(second);
        var third = BuildHotelRate(3, "Hotel Maitei", "Posadas");
        third.RoomType = "Suite";
        context.Rates.Add(third);
        await context.SaveChangesAsync();
        var service = CreateService(context, catalogEnabled: true);

        var result = await service.CatalogSearchAsync("Hotel", "maitei", CancellationToken.None);

        Assert.Single(result!);
    }

    [Fact]
    public async Task CatalogSearch_HomonymHotelsDifferentCities_ReturnsTwoResults()
    {
        await using var context = CreateContext();
        // Dos hoteles homonimos en ciudades distintas = dos productos distintos (m1).
        context.Rates.Add(BuildHotelRate(1, "Costanera", "Posadas"));
        context.Rates.Add(BuildHotelRate(2, "Costanera", "Córdoba"));
        await context.SaveChangesAsync();
        var service = CreateService(context, catalogEnabled: true);

        var result = await service.CatalogSearchAsync("Hotel", "costanera", CancellationToken.None);

        Assert.Equal(2, result!.Count);
        Assert.Contains(result!, item => item.Subtitle == "Posadas");
        Assert.Contains(result!, item => item.Subtitle == "Córdoba");
    }

    // ============================= R1 — enmascarado de costo =============================

    [Fact]
    public async Task CatalogSearch_WithoutSeeCost_MasksNetCost_KeepsSalePrice_RateFallback()
    {
        await using var context = CreateContext();
        context.Rates.Add(BuildHotelRate(1, "Hotel Maitei", "Posadas", netCost: 100m, salePrice: 160m));
        await context.SaveChangesAsync();
        var service = CreateService(context, catalogEnabled: true, canSeeCost: false);

        var result = await service.CatalogSearchAsync("Hotel", "maitei", CancellationToken.None);

        var item = Assert.Single(result!);
        // Sin ventas registradas -> viene el rateFallback (no el lastSale).
        Assert.Null(item.LastSale);
        Assert.NotNull(item.RateFallback);
        Assert.Null(item.RateFallback!.NetCost);     // costo oculto (R1/D1)
        Assert.Equal(160m, item.RateFallback.SalePrice); // la venta viaja SIEMPRE
    }

    [Fact]
    public async Task CatalogSearch_WithSeeCost_KeepsNetCost_RateFallback()
    {
        await using var context = CreateContext();
        context.Rates.Add(BuildHotelRate(1, "Hotel Maitei", "Posadas", netCost: 100m, salePrice: 160m));
        await context.SaveChangesAsync();
        var service = CreateService(context, catalogEnabled: true, canSeeCost: true);

        var result = await service.CatalogSearchAsync("Hotel", "maitei", CancellationToken.None);

        var item = Assert.Single(result!);
        Assert.NotNull(item.RateFallback);
        Assert.Equal(100m, item.RateFallback!.NetCost);
        Assert.Equal(160m, item.RateFallback.SalePrice);
    }

    [Fact]
    public async Task CatalogSearch_AdminWithoutExplicitPermission_KeepsNetCost()
    {
        await using var context = CreateContext();
        context.Rates.Add(BuildHotelRate(1, "Hotel Maitei", "Posadas", netCost: 100m, salePrice: 160m));
        await context.SaveChangesAsync();
        var service = CreateService(context, catalogEnabled: true, canSeeCost: false, isAdmin: true);

        var result = await service.CatalogSearchAsync("Hotel", "maitei", CancellationToken.None);

        var item = Assert.Single(result!);
        Assert.Equal(100m, item.RateFallback!.NetCost); // bypass por rol Admin
    }

    [Fact]
    public async Task CatalogSearch_WithoutIdentity_FailClosed_MasksNetCost()
    {
        await using var context = CreateContext();
        context.Rates.Add(BuildHotelRate(1, "Hotel Maitei", "Posadas", netCost: 100m, salePrice: 160m));
        await context.SaveChangesAsync();
        // Flag ON pero sin resolver ni accessor: no se sabe quien llama -> fail-closed (oculta costo).
        var service = CreateService(context, catalogEnabled: true, withIdentity: false);

        var result = await service.CatalogSearchAsync("Hotel", "maitei", CancellationToken.None);

        var item = Assert.Single(result!);
        Assert.Null(item.RateFallback!.NetCost);
        Assert.Equal(160m, item.RateFallback.SalePrice);
    }

    // ============================= H-4 (review 2026-08-11): operador en el fallback =============================

    [Fact]
    public async Task CatalogSearch_WithoutSales_RateFallback_IncludesSupplierFromRateFicha()
    {
        // Un producto activo SIN ventas registradas trae, en el fallback, el operador que ya esta
        // cargado en su propia ficha (Rate.Supplier) — no hay ninguna venta de la que aprenderlo
        // todavia. Sin esto, el front no tenia de donde sacar el operador y el guardado se frenaba
        // con "Elegi el operador" aunque la ficha SI lo tuviera cargado.
        await using var context = CreateContext();
        var supplier = new Supplier { Id = 1, Name = "Julia Tours" };
        context.Suppliers.Add(supplier);
        var rate = BuildHotelRate(1, "Hotel Maitei", "Posadas");
        rate.SupplierId = supplier.Id;
        context.Rates.Add(rate);
        await context.SaveChangesAsync();
        var service = CreateService(context, catalogEnabled: true, canSeeCost: true);

        var result = await service.CatalogSearchAsync("Hotel", "maitei", CancellationToken.None);

        var item = Assert.Single(result!);
        Assert.Null(item.LastSale);
        Assert.NotNull(item.RateFallback);
        // El Id publico, NUNCA el interno (data-exposure) — mismo trato que LastSale.SupplierPublicId.
        Assert.Equal(supplier.PublicId, item.RateFallback!.SupplierPublicId);
        Assert.Equal("Julia Tours", item.RateFallback.SupplierName);
    }

    [Fact]
    public async Task CatalogSearch_WithoutSupplierOnRate_RateFallback_LeavesSupplierEmpty()
    {
        // Contraparte: si la ficha del producto TAMPOCO tiene operador cargado, el fallback queda sin
        // operador — no se inventa uno. Mismo criterio "sin dato, casillero vacio" de siempre.
        await using var context = CreateContext();
        context.Rates.Add(BuildHotelRate(1, "Hotel Maitei", "Posadas")); // sin SupplierId
        await context.SaveChangesAsync();
        var service = CreateService(context, catalogEnabled: true, canSeeCost: true);

        var result = await service.CatalogSearchAsync("Hotel", "maitei", CancellationToken.None);

        var item = Assert.Single(result!);
        Assert.NotNull(item.RateFallback);
        Assert.Null(item.RateFallback!.SupplierPublicId);
        Assert.Null(item.RateFallback.SupplierName);
    }

    [Fact]
    public async Task CatalogSearch_WithSales_LastSaleSupplierWins_RateFallbackStaysNull()
    {
        // "Con ventas, LastSale manda": aunque la ficha del Rate tenga cargado OTRO operador, la
        // venta manda y el fallback ni se arma — el Include nuevo de Rate.Supplier (H-4) no se cuela
        // en el camino de LastSale.
        await using var context = CreateContext();
        var fichaSupplier = new Supplier { Id = 1, Name = "Operador viejo de la ficha" };
        var ventaSupplier = new Supplier { Id = 2, Name = "Ola Mayorista" };
        context.Suppliers.AddRange(fichaSupplier, ventaSupplier);
        var rate = BuildHotelRate(1, "Hotel Maitei", "Posadas");
        rate.SupplierId = fichaSupplier.Id;
        context.Rates.Add(rate);
        context.RateSupplierSales.Add(new RateSupplierSale
        {
            Id = 1,
            RateId = 1,
            SupplierId = ventaSupplier.Id,
            LastSoldAt = new DateTime(2026, 5, 22, 0, 0, 0, DateTimeKind.Utc),
            LastNetCost = 48000m,
            LastSalePrice = 60000m,
            LastCurrency = "ARS",
            LastPriceUnit = "noche_habitacion",
            SalesCount = 1
        });
        await context.SaveChangesAsync();
        var service = CreateService(context, catalogEnabled: true, canSeeCost: true);

        var result = await service.CatalogSearchAsync("Hotel", "maitei", CancellationToken.None);

        var item = Assert.Single(result!);
        Assert.Null(item.RateFallback);
        Assert.NotNull(item.LastSale);
        Assert.Equal("Ola Mayorista", item.LastSale!.SupplierName);
    }

    // ============================= contexto "ultima vez" =============================

    [Fact]
    public async Task CatalogSearch_WithLastSale_ReturnsLastSaleContext_NotFallback()
    {
        await using var context = CreateContext();
        var supplier = new Supplier { Id = 1, Name = "Ola Mayorista" };
        context.Suppliers.Add(supplier);
        context.Rates.Add(BuildHotelRate(1, "Hotel Maitei", "Posadas"));
        context.RateSupplierSales.Add(new RateSupplierSale
        {
            Id = 1,
            RateId = 1,
            SupplierId = 1,
            LastSoldAt = new DateTime(2026, 5, 22, 0, 0, 0, DateTimeKind.Utc),
            LastNetCost = 48000m,
            LastTax = 0m,
            LastSalePrice = 60000m,
            LastCurrency = "ARS",
            LastPriceUnit = "noche_habitacion",
            SalesCount = 3
        });
        await context.SaveChangesAsync();
        var service = CreateService(context, catalogEnabled: true, canSeeCost: true);

        var result = await service.CatalogSearchAsync("Hotel", "maitei", CancellationToken.None);

        var item = Assert.Single(result!);
        Assert.Null(item.RateFallback);            // habiendo venta, NO viene el fallback
        Assert.NotNull(item.LastSale);
        Assert.Equal("Ola Mayorista", item.LastSale!.SupplierName);
        Assert.Equal(48000m, item.LastSale.NetCost);
        Assert.Equal(60000m, item.LastSale.SalePrice);
        Assert.Equal("noche_habitacion", item.LastSale.PriceUnit);
    }

    [Fact]
    public async Task CatalogSearch_WithLastSale_WithoutSeeCost_MasksLastSaleNetCost()
    {
        await using var context = CreateContext();
        context.Suppliers.Add(new Supplier { Id = 1, Name = "Ola Mayorista" });
        context.Rates.Add(BuildHotelRate(1, "Hotel Maitei", "Posadas"));
        context.RateSupplierSales.Add(new RateSupplierSale
        {
            Id = 1,
            RateId = 1,
            SupplierId = 1,
            LastSoldAt = new DateTime(2026, 5, 22, 0, 0, 0, DateTimeKind.Utc),
            LastNetCost = 48000m,
            LastSalePrice = 60000m,
            LastCurrency = "ARS",
            LastPriceUnit = "noche_habitacion",
            SalesCount = 1
        });
        await context.SaveChangesAsync();
        var service = CreateService(context, catalogEnabled: true, canSeeCost: false);

        var result = await service.CatalogSearchAsync("Hotel", "maitei", CancellationToken.None);

        var item = Assert.Single(result!);
        Assert.NotNull(item.LastSale);
        Assert.Null(item.LastSale!.NetCost);        // costo oculto (R1/D1)
        Assert.Equal(60000m, item.LastSale.SalePrice); // venta visible
    }
}
