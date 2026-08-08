using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Application.Mappings;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Exceptions;
using TravelApi.Domain.Helpers;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Repositories;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Rediseño de Tarifario (spec firmada 2026-08-06): el catalogo que aprende de las ventas SIN llave
/// (P8=A / M-10), la lista de productos aprendidos con enmascarado de costos (M-1/M-2 + F-14) y el
/// freno de repetidos del lado del servidor (M-3/M-4 + P7).
///
/// <para>Corren sobre EF Core InMemory: NO ejercitan pg_trgm (la similitud difusa real se prueba contra
/// Postgres). El resto del circuito — agrupado, enmascarado, freno y creacion — se valida todo aca.</para>
/// </summary>
public class TarifarioAprendidoTests
{
    private const string SeeCostPermission = Permissions.CobranzasSeeCost;

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    private static IMapper CreateMapper()
        => new MapperConfiguration(config => config.AddProfile<MappingProfile>()).CreateMapper();

    private static IHttpContextAccessor BuildAccessor(string userId)
        => new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    new List<Claim> { new(ClaimTypes.NameIdentifier, userId) }, "Test"))
            }
        };

    private static IUserPermissionResolver BuildResolver(string userId, params string[] permissions)
    {
        var mock = new Mock<IUserPermissionResolver>();
        IReadOnlySet<string> set = new HashSet<string>(permissions);
        mock.Setup(r => r.GetPermissionsAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(set);
        return mock.Object;
    }

    /// <summary>
    /// Settings con la llave vieja del catalogo APAGADA a proposito: sirve para demostrar que ya no la
    /// mira nadie (P8=A). El umbral de "precio viejo" queda configurable para los tests de ámbar.
    /// </summary>
    private static Mock<IOperationalFinanceSettingsService> BuildSettings(int staleDays = 60)
    {
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings
            {
                EnableCatalogFindOrCreate = false,
                StaleCostReferenceDays = staleDays
            });
        return settings;
    }

    private static RateService CreateRateService(
        AppDbContext context, bool canSeeCost = true, int staleDays = 60)
    {
        const string userId = "vendedor-tarifario";
        var resolver = canSeeCost
            ? BuildResolver(userId, SeeCostPermission)
            : BuildResolver(userId);

        return new RateService(
            context, NullLogger<RateService>.Instance, resolver, BuildAccessor(userId),
            BuildSettings(staleDays).Object);
    }

    private static BookingService CreateBookingService(AppDbContext context, IMapper mapper)
    {
        var reservaService = new Mock<IReservaService>();
        reservaService.Setup(s => s.UpdateBalanceAsync(It.IsAny<int>())).Returns(Task.CompletedTask);
        reservaService.Setup(s => s.UpdateBalanceAsync(It.IsAny<int>(), It.IsAny<bool>())).Returns(Task.CompletedTask);

        var supplierService = new Mock<ISupplierService>();
        supplierService.Setup(s => s.UpdateBalanceAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        const string userId = "vendedor-tarifario";

        return new BookingService(
            new Repository<FlightSegment>(context),
            new Repository<HotelBooking>(context),
            new Repository<PackageBooking>(context),
            new Repository<TransferBooking>(context),
            new Repository<AssistanceBooking>(context),
            new Repository<Reserva>(context),
            new Repository<Supplier>(context),
            reservaService.Object,
            supplierService.Object,
            context,
            mapper,
            NullLogger<BookingService>.Instance,
            BuildResolver(userId, SeeCostPermission),
            BuildAccessor(userId),
            BuildSettings().Object);
    }

    private static async Task<(Reserva reserva, Supplier supplier)> SeedReservaAndSupplierAsync(AppDbContext context)
    {
        var supplier = new Supplier { Id = 1, Name = "Ola Mayorista" };
        var reserva = new Reserva { Id = 1, NumeroReserva = "F-2026-1042", Name = "Cancún" };
        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();
        return (reserva, supplier);
    }

    private static CreateHotelRequest HotelWithNewProduct(string supplierPublicId, string name, string city)
        => new(
            SupplierId: supplierPublicId, HotelName: name, StarRating: 4, City: city, Country: "Argentina",
            CheckIn: DateTime.UtcNow.Date.AddDays(10), CheckOut: DateTime.UtcNow.Date.AddDays(12),
            RoomType: "Doble", MealPlan: "Desayuno", Adults: 2, Children: 0, Rooms: 1, ConfirmationNumber: null,
            NetCost: 200m, SalePrice: 300m, Commission: 100m, Notes: null,
            Currency: "ARS",
            NewCatalogProduct: new NewCatalogProductRequest(name, city, supplierPublicId));

    // =====================================================================================
    // (1) La llave murio: el catalogo aprende SIEMPRE (P8=A / M-10)
    // =====================================================================================

    /// <summary>
    /// Con la vieja bandera APAGADA en la base, vender igual crea el producto y deja la memoria de venta
    /// (producto + operador + precio + de que reserva salio). Antes esto no pasaba sin prender la llave.
    /// </summary>
    [Fact]
    public async Task VenderUnProductoNuevo_SinLlavePrendida_CreaElProductoYRecuerdaLaVenta()
    {
        await using var context = CreateContext();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);
        var booking = CreateBookingService(context, CreateMapper());

        await booking.CreateHotelAsync(
            reserva.Id,
            HotelWithNewProduct(supplier.PublicId.ToString(), "Maitei Posadas", "Posadas"),
            CancellationToken.None);

        var rate = Assert.Single(await context.Rates.ToListAsync());
        Assert.Equal("Maitei Posadas", rate.HotelName);
        Assert.Equal(TextNormalizer.NormalizeForCatalog("Maitei Posadas"), rate.SearchName);

        var sale = Assert.Single(await context.RateSupplierSales.ToListAsync());
        Assert.Equal(rate.Id, sale.RateId);
        Assert.Equal(supplier.Id, sale.SupplierId);
        Assert.Equal(1, sale.SalesCount);
        // El precio unitario es por noche/habitacion: 200 de costo en 2 noches = 100 por noche.
        Assert.Equal(100m, sale.LastNetCost);
        // De que reserva salio el precio (M-1): es el enlace que muestra el Tarifario.
        Assert.Equal(reserva.Id, sale.LastReservaId);
    }

    /// <summary>El buscador de la venta tampoco depende ya de ninguna llave: siempre responde.</summary>
    [Fact]
    public async Task BuscadorDeProductos_SinLlavePrendida_DevuelveResultados()
    {
        await using var context = CreateContext();
        context.Rates.Add(BuildHotelRate(1, "Maitei Posadas", "Posadas"));
        await context.SaveChangesAsync();
        var service = CreateRateService(context);

        var results = await service.CatalogSearchAsync("Hotel", "maitei", CancellationToken.None);

        var item = Assert.Single(results);
        Assert.Equal("Maitei Posadas", item.Name);
    }

    // =====================================================================================
    // (2) Freno de repetidos del lado del servidor, con confirmacion explicita (P7 / M-3)
    // =====================================================================================

    [Fact]
    public async Task AltaSimple_ConUnProductoParecido_FrenaYDevuelveLosCandidatos()
    {
        await using var context = CreateContext();
        context.Rates.Add(BuildHotelRate(1, "Maitei Posadas", "Posadas"));
        await context.SaveChangesAsync();
        var service = CreateRateService(context);

        var result = await service.CreateSimpleProductAsync(new CreateSimpleProductRequest
        {
            ServiceType = "Hotel",
            Name = "maitei posadas",
            City = "Posadas",
            Price = 50m,
            Currency = "USD"
        }, CancellationToken.None);

        Assert.Null(result.Created);
        Assert.Equal(SimpleProductCreationReasons.SimilarProductFound, result.Reason);
        var candidate = Assert.Single(result.SimilarProducts);
        Assert.Equal("Maitei Posadas", candidate.Name);
        Assert.True(candidate.IsSameName);
        Assert.Contains("Maitei Posadas", result.Message);
        // Lo importante: NO se creo nada mientras el usuario no confirme.
        Assert.Equal(1, await context.Rates.CountAsync());
    }

    [Fact]
    public async Task AltaSimple_ConConfirmacionExplicita_CreaIgualElProducto()
    {
        await using var context = CreateContext();
        context.Rates.Add(BuildHotelRate(1, "Maitei Posadas", "Posadas"));
        await context.SaveChangesAsync();
        var service = CreateRateService(context);

        var result = await service.CreateSimpleProductAsync(new CreateSimpleProductRequest
        {
            ServiceType = "Hotel",
            Name = "Maitei Posadas",
            City = "Posadas",
            Price = 50m,
            Currency = "USD",
            CreateAnyway = true
        }, CancellationToken.None);

        Assert.NotNull(result.Created);
        Assert.Null(result.Reason);
        Assert.Equal(2, await context.Rates.CountAsync());
        var created = await context.Rates.OrderBy(rate => rate.Id).LastAsync();
        Assert.Equal("Maitei Posadas", created.HotelName);
        Assert.Equal("Posadas", created.City);
        Assert.Equal(50m, created.SalePrice);
        Assert.Equal("USD", created.Currency);
        // Nace con el nombre normalizado escrito: el buscador lo encuentra desde el minuto cero.
        Assert.Equal(TextNormalizer.NormalizeForCatalog("Maitei Posadas"), created.SearchName);
    }

    /// <summary>
    /// Dos hoteles con el MISMO nombre en ciudades distintas NO son repetidos: frenar ahi seria molestar
    /// al vendedor por nada (la ciudad es justamente el desempate del anti-duplicados).
    /// </summary>
    [Fact]
    public async Task AltaSimple_MismoNombreEnOtraCiudad_NoFrena()
    {
        await using var context = CreateContext();
        context.Rates.Add(BuildHotelRate(1, "Hotel Costa", "Mar del Plata"));
        await context.SaveChangesAsync();
        var service = CreateRateService(context);

        var result = await service.CreateSimpleProductAsync(new CreateSimpleProductRequest
        {
            ServiceType = "Hotel",
            Name = "Hotel Costa",
            City = "Villa Gesell",
            Price = 40m
        }, CancellationToken.None);

        Assert.NotNull(result.Created);
        Assert.Equal(2, await context.Rates.CountAsync());
    }

    [Fact]
    public async Task AltaSimple_HotelSinCiudad_Rechaza()
    {
        await using var context = CreateContext();
        var service = CreateRateService(context);

        var error = await Assert.ThrowsAsync<RateValidationException>(() => service.CreateSimpleProductAsync(
            new CreateSimpleProductRequest { ServiceType = "Hotel", Name = "Hotel sin ciudad", Price = 10m },
            CancellationToken.None));

        // El texto se compara COMPLETO: es lo que ve el usuario, no puede cambiar sin que un test avise.
        Assert.Equal("La ciudad es obligatoria para crear un hotel.", error.Message);
        Assert.Equal(0, await context.Rates.CountAsync());
    }

    // =====================================================================================
    // (3) Lista de productos aprendidos: agrupado, precios por operador y enmascarado (M-1/M-2, F-14)
    // =====================================================================================

    /// <summary>
    /// El mismo hotel cargado dos veces en el tarifario viejo (dos habitaciones) aparece UNA sola vez,
    /// con un renglon por operador y el precio que dejo cada venta.
    /// </summary>
    [Fact]
    public async Task ProductosAprendidos_AgrupaElMismoProducto_YMuestraUnRenglonPorOperador()
    {
        await using var context = CreateContext();
        var ola = new Supplier { Id = 1, Name = "Ola Mayorista" };
        var julia = new Supplier { Id = 2, Name = "Julia Tours" };
        var reserva = new Reserva { Id = 1, NumeroReserva = "F-2026-1042", Name = "Posadas" };
        context.Suppliers.AddRange(ola, julia);
        context.Reservas.Add(reserva);
        context.Rates.AddRange(
            BuildHotelRate(1, "Maitei Posadas", "Posadas"),
            BuildHotelRate(2, "maitei  posadas", "posadas"));
        context.RateSupplierSales.AddRange(
            BuildSale(rateId: 1, supplierId: ola.Id, netCost: 48m, salePrice: 60m,
                soldAt: DateTime.UtcNow.AddDays(-5), reservaId: reserva.Id),
            BuildSale(rateId: 2, supplierId: julia.Id, netCost: 52m, salePrice: 70m,
                soldAt: DateTime.UtcNow.AddDays(-2), reservaId: null));
        await context.SaveChangesAsync();
        var service = CreateRateService(context);

        var page = await service.GetLearnedProductsAsync(new LearnedProductsQuery(), CancellationToken.None);

        var product = Assert.Single(page.Items);
        Assert.Equal("Hotel", product.ServiceType);
        Assert.Equal(2, Rows(product).Count);
        // El precio mas nuevo va arriba.
        Assert.Equal("Julia Tours", Rows(product)[0].SupplierName);
        Assert.Equal(52m, Rows(product)[0].Price);
        Assert.Equal("Ola Mayorista", Rows(product)[1].SupplierName);
        Assert.Equal("F-2026-1042", Rows(product)[1].NumeroReserva);
        Assert.Equal("por noche", Rows(product)[1].PriceUnitLabel);
    }

    /// <summary>F-14: sin permiso de costos NUNCA viaja el costo; en su lugar viaja el precio de venta.</summary>
    [Fact]
    public async Task ProductosAprendidos_SinPermisoDeCostos_MuestraLaVentaYNoElCosto()
    {
        await using var context = CreateContext();
        context.Suppliers.Add(new Supplier { Id = 1, Name = "Ola Mayorista" });
        context.Rates.Add(BuildHotelRate(1, "Maitei Posadas", "Posadas"));
        context.RateSupplierSales.Add(BuildSale(
            rateId: 1, supplierId: 1, netCost: 48m, salePrice: 60m,
            soldAt: DateTime.UtcNow.AddDays(-5), reservaId: null));
        await context.SaveChangesAsync();

        var conCostos = CreateRateService(context, canSeeCost: true);
        var sinCostos = CreateRateService(context, canSeeCost: false);

        var visto = Rows((await conCostos.GetLearnedProductsAsync(new LearnedProductsQuery(), CancellationToken.None))
            .Items.Single()).Single();
        var enmascarado = Rows((await sinCostos.GetLearnedProductsAsync(new LearnedProductsQuery(), CancellationToken.None))
            .Items.Single()).Single();

        Assert.Equal(48m, visto.Price);
        Assert.Equal("Costo", visto.PriceKind);
        Assert.Equal(60m, enmascarado.Price);
        Assert.Equal("Venta", enmascarado.PriceKind);
    }

    /// <summary>
    /// Una tarifa vieja que NUNCA se vendio entra igual a la lista (P2=A), con el precio cargado a mano.
    /// Y si ese precio quedo viejo (mas dias que el umbral), el motor lo marca para pintarlo en ámbar.
    /// </summary>
    [Fact]
    public async Task ProductosAprendidos_TarifaViejaSinVentas_EntraYMarcaElPrecioViejo()
    {
        await using var context = CreateContext();
        context.Suppliers.Add(new Supplier { Id = 1, Name = "Ola Mayorista" });
        var rate = BuildHotelRate(1, "Maitei Posadas", "Posadas");
        rate.SupplierId = 1;
        rate.CreatedAt = DateTime.UtcNow.AddDays(-200);
        context.Rates.Add(rate);
        await context.SaveChangesAsync();
        var service = CreateRateService(context, staleDays: 60);

        var product = Assert.Single(
            (await service.GetLearnedProductsAsync(new LearnedProductsQuery(), CancellationToken.None)).Items);

        var row = Assert.Single(Rows(product));
        Assert.Equal("Ola Mayorista", row.SupplierName);
        Assert.True(row.IsOldPrice);
        Assert.Contains("hace", row.PriceAgeText);
        Assert.Null(row.NumeroReserva);
    }

    [Fact]
    public async Task ProductosAprendidos_FiltraPorTipoYPorOperador()
    {
        await using var context = CreateContext();
        var ola = new Supplier { Id = 1, Name = "Ola Mayorista" };
        var aeromundo = new Supplier { Id = 2, Name = "Aeromundo" };
        context.Suppliers.AddRange(ola, aeromundo);

        var hotel = BuildHotelRate(1, "Maitei Posadas", "Posadas");
        hotel.SupplierId = ola.Id;
        var vuelo = new Rate
        {
            Id = 2,
            ServiceType = "Aereo",
            ProductName = "Buenos Aires – Miami",
            Origin = "EZE",
            Destination = "MIA",
            SupplierId = aeromundo.Id,
            NetCost = 700m,
            SalePrice = 780m,
            Currency = "USD",
            PriceUnit = "pasajero",
            IsActive = true,
            SearchName = TextNormalizer.NormalizeForCatalog("Buenos Aires – Miami")
        };
        context.Rates.AddRange(hotel, vuelo);
        await context.SaveChangesAsync();
        var service = CreateRateService(context);

        var soloAereos = await service.GetLearnedProductsAsync(
            new LearnedProductsQuery { ServiceType = "Aereo" }, CancellationToken.None);
        Assert.Equal("Buenos Aires – Miami", Assert.Single(soloAereos.Items).Name);
        Assert.Equal("Aéreo", soloAereos.Items[0].ServiceTypeLabel);

        var soloOla = await service.GetLearnedProductsAsync(
            new LearnedProductsQuery { SupplierId = ola.PublicId.ToString() }, CancellationToken.None);
        Assert.Equal("Maitei Posadas", Assert.Single(soloOla.Items).Name);
    }

    // =====================================================================================
    // (4) Textos exactos de los rechazos del alta (los ve el usuario tal cual)
    // =====================================================================================

    [Theory]
    [InlineData("", "Hotel Test", "Posadas", 10, "Elegí qué tipo de producto es.")]
    [InlineData("Hotel", "", "Posadas", 10, "El nombre del producto es obligatorio.")]
    [InlineData("Hotel", "Hotel Test", null, 10, "La ciudad es obligatoria para crear un hotel.")]
    [InlineData("Paquete", "Bariloche 4 noches", null, -1, "El precio no puede ser menor a cero.")]
    public async Task AltaSimple_RechazosConMensajeParaElUsuario(
        string serviceType, string name, string? city, decimal price, string expectedMessage)
    {
        await using var context = CreateContext();
        var service = CreateRateService(context);

        var error = await Assert.ThrowsAsync<RateValidationException>(() => service.CreateSimpleProductAsync(
            new CreateSimpleProductRequest
            {
                ServiceType = serviceType,
                Name = name,
                City = city,
                Price = price
            },
            CancellationToken.None));

        // El mensaje viaja TAL CUAL al usuario: se compara completo, sin "contains".
        Assert.Equal(expectedMessage, error.Message);
        Assert.Equal(0, await context.Rates.CountAsync());
    }

    [Fact]
    public async Task AltaSimple_ConOperadorInexistente_AvisaEnCriollo()
    {
        await using var context = CreateContext();
        var service = CreateRateService(context);

        var error = await Assert.ThrowsAsync<RateValidationException>(() => service.CreateSimpleProductAsync(
            new CreateSimpleProductRequest
            {
                ServiceType = "Paquete",
                Name = "Bariloche 4 noches",
                SupplierId = Guid.NewGuid().ToString(),
                Price = 100m
            },
            CancellationToken.None));

        Assert.Equal("No encontramos ese operador.", error.Message);
    }

    /// <summary>
    /// Doble clic / doble submit: el mismo alta enviada dos veces NO puede terminar en dos productos, ni
    /// siquiera con "crear igual" confirmado (esa confirmacion es para OTRO producto con el mismo nombre,
    /// no para el mismo formulario mandado dos veces).
    /// </summary>
    [Fact]
    public async Task AltaSimple_DobleSubmit_NoCreaDosProductos()
    {
        await using var context = CreateContext();
        var service = CreateRateService(context);

        CreateSimpleProductRequest Pedido() => new()
        {
            ServiceType = "Hotel",
            Name = "Maitei Posadas",
            City = "Posadas",
            Price = 48m,
            Currency = "USD",
            CreateAnyway = true
        };

        var primera = await service.CreateSimpleProductAsync(Pedido(), CancellationToken.None);
        var segunda = await service.CreateSimpleProductAsync(Pedido(), CancellationToken.None);

        Assert.NotNull(primera.Created);
        Assert.NotNull(segunda.Created);
        // La segunda devuelve EL MISMO producto, no uno nuevo.
        Assert.Equal(primera.Created!.PublicId, segunda.Created!.PublicId);
        Assert.Equal(1, await context.Rates.CountAsync());
    }

    /// <summary>
    /// El freno tiene UMBRAL: solo para el alta cuando el nombre es el MISMO. Un parecido flojo (comparten
    /// palabras pero son productos distintos) acompaña como sugerencia pero no traba nada.
    /// </summary>
    [Fact]
    public async Task AltaSimple_ParecidoFlojo_NoFrena()
    {
        await using var context = CreateContext();
        var vuelo = new Rate
        {
            Id = 1,
            ServiceType = "Aereo",
            ProductName = "Buenos Aires Miami",
            NetCost = 700m,
            SalePrice = 780m,
            Currency = "USD",
            PriceUnit = "pasajero",
            IsActive = true,
            SearchName = TextNormalizer.NormalizeForCatalog("Buenos Aires Miami")
        };
        context.Rates.Add(vuelo);
        await context.SaveChangesAsync();
        var service = CreateRateService(context);

        var result = await service.CreateSimpleProductAsync(new CreateSimpleProductRequest
        {
            ServiceType = "Aereo",
            Name = "Buenos Aires Madrid",
            Price = 900m,
            Currency = "USD"
        }, CancellationToken.None);

        Assert.NotNull(result.Created);
        Assert.Equal(2, await context.Rates.CountAsync());
    }

    // =====================================================================================
    // (5) Renombrar el PRODUCTO entero (§2.2)
    // =====================================================================================

    [Fact]
    public async Task Renombrar_CorrigeTODASLasTarifasDelProducto()
    {
        await using var context = CreateContext();
        // El mismo hotel cargado dos veces (dos habitaciones): es UN producto con DOS tarifas.
        context.Rates.AddRange(
            BuildHotelRate(1, "Maitei Posadas", "Posadas"),
            BuildHotelRate(2, "maitei  posadas", "posadas"));
        await context.SaveChangesAsync();
        var service = CreateRateService(context);

        var result = await service.RenameLearnedProductAsync(new RenameLearnedProductRequest
        {
            ServiceType = "Hotel",
            Name = "Maitei Posadas",
            City = "Posadas",
            NewName = "Hotel Maitei",
            NewCity = "Posadas"
        }, CancellationToken.None);

        Assert.Equal(2, result.RenamedRates);
        Assert.Equal("Hotel Maitei", result.Name);

        var rates = await context.Rates.AsNoTracking().ToListAsync();
        Assert.All(rates, rate => Assert.Equal("Hotel Maitei", rate.HotelName));
        Assert.All(rates, rate => Assert.Equal("Hotel Maitei", rate.ProductName));
        // El nombre normalizado se actualiza: si no, el buscador seguiria encontrandolo por el nombre viejo.
        Assert.All(rates, rate =>
            Assert.Equal(TextNormalizer.NormalizeForCatalog("Hotel Maitei"), rate.SearchName));

        // Y la lista lo sigue mostrando como UN producto, no como dos.
        var page = await service.GetLearnedProductsAsync(new LearnedProductsQuery(), CancellationToken.None);
        Assert.Single(page.Items);
    }

    [Fact]
    public async Task Renombrar_SiElNombreYaLoTieneOtroProducto_AvisaYNoTocaNada()
    {
        await using var context = CreateContext();
        context.Rates.AddRange(
            BuildHotelRate(1, "Maitei Posadas", "Posadas"),
            BuildHotelRate(2, "Hotel Maitei", "Posadas"));
        await context.SaveChangesAsync();
        var service = CreateRateService(context);

        var error = await Assert.ThrowsAsync<RateProductNameTakenException>(() =>
            service.RenameLearnedProductAsync(new RenameLearnedProductRequest
            {
                ServiceType = "Hotel",
                Name = "Maitei Posadas",
                City = "Posadas",
                NewName = "Hotel Maitei",
                NewCity = "Posadas"
            }, CancellationToken.None));

        // Texto COMPLETO: es el cartel que lee el usuario cuando el sistema no fusiona.
        Assert.Equal(
            "Ya tenés un producto que se llama \"Hotel Maitei\" en Posadas. " +
            "Poné otro nombre, o usá el que ya existe para no tenerlo dos veces.",
            error.Message);
        // NO se fusiona ni se pisa nada: los dos productos siguen como estaban.
        var rates = await context.Rates.AsNoTracking().OrderBy(rate => rate.Id).ToListAsync();
        Assert.Equal("Maitei Posadas", rates[0].HotelName);
        Assert.Equal("Hotel Maitei", rates[1].HotelName);
    }

    [Fact]
    public async Task Renombrar_MismoNombreEnOtraCiudad_NoEsColision()
    {
        await using var context = CreateContext();
        context.Rates.AddRange(
            BuildHotelRate(1, "Hotel Costa", "Mar del Plata"),
            BuildHotelRate(2, "Hotel Costa", "Villa Gesell"));
        await context.SaveChangesAsync();
        var service = CreateRateService(context);

        // Renombrar el de Mar del Plata a "Costa Marplatense" no choca con el de Villa Gesell.
        var result = await service.RenameLearnedProductAsync(new RenameLearnedProductRequest
        {
            ServiceType = "Hotel",
            Name = "Hotel Costa",
            City = "Mar del Plata",
            NewName = "Costa Marplatense",
            NewCity = "Mar del Plata"
        }, CancellationToken.None);

        Assert.Equal(1, result.RenamedRates);
        Assert.Equal("Costa Marplatense", (await context.Rates.AsNoTracking().SingleAsync(r => r.Id == 1)).HotelName);
        Assert.Equal("Hotel Costa", (await context.Rates.AsNoTracking().SingleAsync(r => r.Id == 2)).HotelName);
    }

    [Fact]
    public async Task Renombrar_ProductoQueNoExiste_Avisa()
    {
        await using var context = CreateContext();
        var service = CreateRateService(context);

        var error = await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.RenameLearnedProductAsync(new RenameLearnedProductRequest
            {
                ServiceType = "Hotel",
                Name = "No existe",
                City = "Posadas",
                NewName = "Otro",
                NewCity = "Posadas"
            }, CancellationToken.None));

        Assert.Equal("No encontramos ese producto en el tarifario.", error.Message);
    }

    [Theory]
    [InlineData("", "Maitei Posadas", "Hotel Maitei", "Posadas", "Elegí qué tipo de producto es.")]
    [InlineData("Hotel", "", "Hotel Maitei", "Posadas", "Falta el nombre actual del producto.")]
    [InlineData("Hotel", "Maitei Posadas", "", "Posadas", "El nombre del producto es obligatorio.")]
    [InlineData("Hotel", "Maitei Posadas", "Hotel Maitei", null, "La ciudad es obligatoria para un hotel.")]
    public async Task Renombrar_RechazosConMensajeParaElUsuario(
        string serviceType, string name, string newName, string? newCity, string expectedMessage)
    {
        await using var context = CreateContext();
        context.Rates.Add(BuildHotelRate(1, "Maitei Posadas", "Posadas"));
        await context.SaveChangesAsync();
        var service = CreateRateService(context);

        var error = await Assert.ThrowsAsync<RateValidationException>(() =>
            service.RenameLearnedProductAsync(new RenameLearnedProductRequest
            {
                ServiceType = serviceType,
                Name = name,
                City = "Posadas",
                NewName = newName,
                NewCity = newCity
            }, CancellationToken.None));

        Assert.Equal(expectedMessage, error.Message);
        // Nada se toco: el producto sigue llamandose igual.
        Assert.Equal("Maitei Posadas", (await context.Rates.AsNoTracking().SingleAsync()).HotelName);
    }

    // =====================================================================================
    // (7) Editar una tarifa SIN permiso de costos no puede borrar el costo (R1)
    // =====================================================================================

    /// <summary>
    /// A quien no ve costos, la pantalla le muestra 0 en costo e impuesto. Si al guardar se escribiera ese
    /// 0, corregir el NOMBRE de una tarifa borraria el costo real del operador sin que nadie se entere.
    /// El costo persistido se conserva y la ganancia se recalcula con el costo conservado.
    /// </summary>
    [Fact]
    public async Task EditarTarifa_SinPermisoDeCostos_NoBorraElCostoGuardado()
    {
        await using var context = CreateContext();
        var rate = BuildHotelRate(1, "Maitei Posadas", "Posadas");
        rate.NetCost = 100m;
        rate.Tax = 15m;
        rate.SalePrice = 160m;
        rate.Commission = 45m;
        context.Rates.Add(rate);
        await context.SaveChangesAsync();

        var service = CreateRateService(context, canSeeCost: false);

        // El request llega como lo manda el formulario de un caller enmascarado: costo e impuesto en 0.
        await service.UpdateAsync(rate.Id, new RateDto(
            SupplierId: null,
            ServiceType: "Hotel",
            ProductName: "Maitei Posadas corregido",
            Description: null,
            PriceUnit: "noche",
            NetCost: 0m,
            Tax: 0m,
            SalePrice: 180m,
            Currency: "ARS",
            ValidFrom: null,
            ValidTo: null,
            InternalNotes: null,
            HotelName: "Maitei Posadas corregido",
            City: "Posadas"), CancellationToken.None);

        var persisted = await context.Rates.AsNoTracking().SingleAsync();
        Assert.Equal("Maitei Posadas corregido", persisted.HotelName); // el nombre SI se corrigio
        Assert.Equal(100m, persisted.NetCost);                          // el costo NO se perdio
        Assert.Equal(15m, persisted.Tax);
        Assert.Equal(180m, persisted.SalePrice);                        // la venta si la puede editar
        Assert.Equal(180m - 100m - 15m, persisted.Commission);          // ganancia con el costo conservado
    }

    [Fact]
    public async Task EditarTarifa_ConPermisoDeCostos_GuardaLoQueMandaElRequest()
    {
        await using var context = CreateContext();
        var rate = BuildHotelRate(1, "Maitei Posadas", "Posadas");
        rate.NetCost = 100m;
        rate.Tax = 15m;
        context.Rates.Add(rate);
        await context.SaveChangesAsync();

        var service = CreateRateService(context, canSeeCost: true);

        await service.UpdateAsync(rate.Id, new RateDto(
            SupplierId: null,
            ServiceType: "Hotel",
            ProductName: "Maitei Posadas",
            Description: null,
            PriceUnit: "noche",
            NetCost: 120m,
            Tax: 20m,
            SalePrice: 200m,
            Currency: "ARS",
            ValidFrom: null,
            ValidTo: null,
            InternalNotes: null,
            HotelName: "Maitei Posadas",
            City: "Posadas"), CancellationToken.None);

        var persisted = await context.Rates.AsNoTracking().SingleAsync();
        Assert.Equal(120m, persisted.NetCost);
        Assert.Equal(20m, persisted.Tax);
        Assert.Equal(200m - 120m - 20m, persisted.Commission);
    }

    // =====================================================================================
    // (7) Dos agujeros que quedaron anotados en la revision anterior
    // =====================================================================================

    /// <summary>
    /// Func-N10: el tipo de producto se compara SIN importar como este escrito. Antes, un cliente que
    /// mandara "hotel" en minuscula contra un "Hotel" guardado recibia un "no existe" mentiroso.
    /// </summary>
    [Theory]
    [InlineData("hotel")]
    [InlineData("HOTEL")]
    [InlineData("Hotel")]
    public async Task Renombrar_ElTipoSeComparaSinImportarComoEsteEscrito(string serviceTypeAsSent)
    {
        await using var context = CreateContext();
        context.Rates.Add(BuildHotelRate(1, "Maitei Posadas", "Posadas"));
        await context.SaveChangesAsync();
        var service = CreateRateService(context);

        var result = await service.RenameLearnedProductAsync(new RenameLearnedProductRequest
        {
            ServiceType = serviceTypeAsSent,
            Name = "Maitei Posadas",
            City = "Posadas",
            NewName = "Hotel Maitei",
            NewCity = "Posadas"
        }, CancellationToken.None);

        Assert.Equal(1, result.RenamedRates);
    }

    /// <summary>
    /// Sec-R3: un producto APAGADO tambien ocupa el nombre. Si no contara, se podria dejar dos productos
    /// identicos esperando a que alguien vuelva a prender el viejo — justo lo que P7 manda evitar.
    /// </summary>
    [Fact]
    public async Task Renombrar_ChocaTambienConUnProductoApagado()
    {
        await using var context = CreateContext();
        var vivo = BuildHotelRate(1, "Maitei Posadas", "Posadas");
        var apagado = BuildHotelRate(2, "Hotel Maitei", "Posadas");
        apagado.IsActive = false;
        context.Rates.AddRange(vivo, apagado);
        await context.SaveChangesAsync();
        var service = CreateRateService(context);

        await Assert.ThrowsAsync<RateProductNameTakenException>(() =>
            service.RenameLearnedProductAsync(new RenameLearnedProductRequest
            {
                ServiceType = "Hotel",
                Name = "Maitei Posadas",
                City = "Posadas",
                NewName = "Hotel Maitei",
                NewCity = "Posadas"
            }, CancellationToken.None));
    }

    /// <summary>
    /// La excepcion de Sec-R3: el producto que ESTE MISMO absorbio al unirse no choca. Su nombre es
    /// historia propia, no un producto compitiendo por el mismo lugar.
    /// </summary>
    [Fact]
    public async Task Renombrar_NoChocaConLoQueElMismoProductoAbsorbio()
    {
        await using var context = CreateContext();
        var vivo = BuildHotelRate(1, "Maitei Posadas", "Posadas");
        var absorbido = BuildHotelRate(2, "Hotel Maitei", "Posadas");
        absorbido.IsActive = false;
        absorbido.MergedIntoRateId = vivo.Id;
        absorbido.MergedAt = DateTime.UtcNow;
        context.Rates.AddRange(vivo, absorbido);
        await context.SaveChangesAsync();
        var service = CreateRateService(context);

        var result = await service.RenameLearnedProductAsync(new RenameLearnedProductRequest
        {
            ServiceType = "Hotel",
            Name = "Maitei Posadas",
            City = "Posadas",
            NewName = "Hotel Maitei",
            NewCity = "Posadas"
        }, CancellationToken.None);

        Assert.Equal(1, result.RenamedRates);
    }

    // =====================================================================================
    // (6) Nada de codigos internos en pantalla: las etiquetas SIEMPRE vienen escritas
    // =====================================================================================

    [Theory]
    [InlineData("Aereo", "Aéreo")]
    [InlineData("Asistencia", "Asistencia")]
    [InlineData("Traslado", "Traslado")]
    public async Task ProductosAprendidos_LaEtiquetaDelTipoNuncaEsElCodigoInterno(
        string serviceType, string expectedLabel)
    {
        await using var context = CreateContext();
        context.Suppliers.Add(new Supplier { Id = 1, Name = "Operador" });
        context.Rates.Add(new Rate
        {
            Id = 1,
            ServiceType = serviceType,
            ProductName = "Producto de prueba",
            SupplierId = 1,
            NetCost = 100m,
            SalePrice = 150m,
            Currency = "ARS",
            PriceUnit = CatalogPriceUnits.PasajeroDia,
            IsActive = true,
            SearchName = TextNormalizer.NormalizeForCatalog("Producto de prueba")
        });
        await context.SaveChangesAsync();
        var service = CreateRateService(context);

        var product = Assert.Single(
            (await service.GetLearnedProductsAsync(new LearnedProductsQuery(), CancellationToken.None)).Items);

        Assert.Equal(expectedLabel, product.ServiceTypeLabel);
        // La unidad tambien llega escrita: nunca "pasajero_dia" en la pantalla.
        var row = Assert.Single(Rows(product));
        Assert.Equal("por pasajero por día", row.PriceUnitLabel);
        Assert.DoesNotContain("_", row.PriceUnitLabel);
    }

    // =====================================================================================
    // Helpers de siembra
    // =====================================================================================

    /// <summary>
    /// Aplana los renglones de precio de un producto (ahora vienen agrupados por habitacion). Los tests
    /// que no miran la habitacion en si usan esto para seguir mirando "los precios del producto".
    /// </summary>
    private static IReadOnlyList<LearnedProductPriceDto> Rows(LearnedProductDto product)
        => product.Variants.SelectMany(variant => variant.Suppliers).ToList();

    private static Rate BuildHotelRate(int id, string hotelName, string city)
        => new()
        {
            Id = id,
            ServiceType = "Hotel",
            ProductName = $"Tarifa {hotelName}",
            HotelName = hotelName,
            City = city,
            RoomType = "Doble",
            NetCost = 100m,
            Tax = 0m,
            SalePrice = 160m,
            Commission = 60m,
            Currency = "ARS",
            PriceUnit = "noche",
            IsActive = true,
            // Producto que ya estaba en el tarifario desde antes (no recien creado): asi no lo agarra el
            // freno de "doble clic", que solo mira los ultimos segundos.
            CreatedAt = DateTime.UtcNow.AddDays(-30),
            SearchName = TextNormalizer.NormalizeForCatalog(hotelName)
        };

    private static RateSupplierSale BuildSale(
        int rateId, int supplierId, decimal netCost, decimal salePrice, DateTime soldAt, int? reservaId)
        => new()
        {
            RateId = rateId,
            SupplierId = supplierId,
            LastSoldAt = soldAt,
            LastNetCost = netCost,
            LastTax = 0m,
            LastSalePrice = salePrice,
            LastCurrency = "USD",
            LastPriceUnit = CatalogPriceUnits.NocheHabitacion,
            SalesCount = 1,
            LastReservaId = reservaId
        };
}
