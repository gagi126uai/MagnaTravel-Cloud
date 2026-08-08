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
using TravelApi.Infrastructure.Services.Reservations;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Tarifario inteligente, fase 1 (spec firmada 2026-08-07): el tarifario recuerda el precio POR
/// HABITACIÓN (M-12/M-13), lo muestra agrupado con solapas (M-14), sugiere el precio de la habitación
/// que se está vendiendo (M-15), recuerda los nombres finos escritos a mano (M-19) y deja corregirlos
/// sin tocar un solo importe (M-18).
///
/// <para>Lo que se protege acá, en criollo: <b>vender una triple no puede pisar el precio de la doble</b>.</para>
/// </summary>
public class TarifarioVariantesTests
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

    private static Mock<IOperationalFinanceSettingsService> BuildSettings(int staleDays = 60)
    {
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings { StaleCostReferenceDays = staleDays });
        return settings;
    }

    private static RateService CreateRateService(AppDbContext context, bool canSeeCost = true)
    {
        const string userId = "vendedor-variantes";
        var resolver = canSeeCost ? BuildResolver(userId, SeeCostPermission) : BuildResolver(userId);
        return new RateService(
            context, NullLogger<RateService>.Instance, resolver, BuildAccessor(userId), BuildSettings().Object);
    }

    private static CatalogLibrarianService CreateLibrarian(AppDbContext context)
        => new(context, NullLogger<CatalogLibrarianService>.Instance, BuildAccessor("vendedor-variantes"));

    private static BookingService CreateBookingService(AppDbContext context, IMapper mapper)
    {
        var reservaService = new Mock<IReservaService>();
        reservaService.Setup(s => s.UpdateBalanceAsync(It.IsAny<int>())).Returns(Task.CompletedTask);
        reservaService.Setup(s => s.UpdateBalanceAsync(It.IsAny<int>(), It.IsAny<bool>())).Returns(Task.CompletedTask);

        var supplierService = new Mock<ISupplierService>();
        supplierService.Setup(s => s.UpdateBalanceAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        const string userId = "vendedor-variantes";
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

    private static async Task<(Reserva reserva, Supplier supplier)> SeedAsync(AppDbContext context)
    {
        var supplier = new Supplier { Id = 1, Name = "Ola Mayorista" };
        var reserva = new Reserva { Id = 1, NumeroReserva = "F-2026-1042", Name = "Iguazú" };
        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();
        return (reserva, supplier);
    }

    private static CreateHotelRequest HotelSale(
        string supplierPublicId, string productName, string roomType, string mealPlan,
        decimal netCost, decimal salePrice, string? roomCategory = null)
        => new(
            SupplierId: supplierPublicId, HotelName: productName, StarRating: 4, City: "Puerto Iguazú",
            Country: "Argentina",
            CheckIn: DateTime.UtcNow.Date.AddDays(10), CheckOut: DateTime.UtcNow.Date.AddDays(12),
            RoomType: roomType, MealPlan: mealPlan, Adults: 2, Children: 0, Rooms: 1,
            ConfirmationNumber: null,
            NetCost: netCost, SalePrice: salePrice, Commission: salePrice - netCost, Notes: null,
            Currency: "USD",
            NewCatalogProduct: new NewCatalogProductRequest(productName, "Puerto Iguazú", supplierPublicId),
            RoomCategory: roomCategory);

    // =====================================================================================
    // M-12 / M-13 — la triple ya no pisa a la doble
    // =====================================================================================

    [Fact]
    public async Task VenderDosHabitaciones_GuardaDosPreciosDistintos_NoSePisan()
    {
        await using var context = CreateContext();
        var (reserva, supplier) = await SeedAsync(context);
        var booking = CreateBookingService(context, CreateMapper());
        var operador = supplier.PublicId.ToString();

        // Primero una doble con desayuno a 48 la noche (96 por 2 noches).
        await booking.CreateHotelAsync(
            reserva.Id, HotelSale(operador, "Sheraton Iguazú", "Doble", "Desayuno", 96m, 140m),
            CancellationToken.None);

        // Despues una triple del MISMO hotel y el MISMO operador, mas cara.
        await booking.CreateHotelAsync(
            reserva.Id, HotelSale(operador, "Sheraton Iguazú", "Triple", "Desayuno", 140m, 200m),
            CancellationToken.None);

        var sales = await context.RateSupplierSales.AsNoTracking().ToListAsync();
        Assert.Equal(2, sales.Count);

        var doble = sales.Single(sale => sale.VariantKey.StartsWith("doble", StringComparison.Ordinal));
        var triple = sales.Single(sale => sale.VariantKey.StartsWith("triple", StringComparison.Ordinal));

        Assert.Equal(48m, doble.LastNetCost);   // 96 / 2 noches
        Assert.Equal(70m, triple.LastNetCost);  // 140 / 2 noches
        Assert.Equal("Doble con desayuno", doble.VariantLabel);
        Assert.Equal("Triple con desayuno", triple.VariantLabel);
        // Un solo producto: el hotel NO se duplico por tener dos habitaciones.
        Assert.Equal(1, await context.Rates.CountAsync());
    }

    [Fact]
    public async Task VenderDosVecesLaMismaHabitacion_ActualizaElMismoPrecio()
    {
        await using var context = CreateContext();
        var (reserva, supplier) = await SeedAsync(context);
        var booking = CreateBookingService(context, CreateMapper());
        var operador = supplier.PublicId.ToString();

        await booking.CreateHotelAsync(
            reserva.Id, HotelSale(operador, "Sheraton Iguazú", "Doble", "Desayuno", 96m, 140m),
            CancellationToken.None);
        await booking.CreateHotelAsync(
            reserva.Id, HotelSale(operador, "doble", "Doble", "Desayuno", 110m, 160m) with
            {
                HotelName = "Sheraton Iguazú",
                NewCatalogProduct = null,
                RateId = (await context.Rates.AsNoTracking().SingleAsync()).PublicId.ToString()
            },
            CancellationToken.None);

        var sale = Assert.Single(await context.RateSupplierSales.AsNoTracking().ToListAsync());
        Assert.Equal(55m, sale.LastNetCost); // 110 / 2 noches: gana la venta mas nueva
        Assert.Equal(2, sale.SalesCount);
    }

    [Fact]
    public async Task ElNombreFinoDeLaHabitacion_EsParteDeLaVariante()
    {
        await using var context = CreateContext();
        var (reserva, supplier) = await SeedAsync(context);
        var booking = CreateBookingService(context, CreateMapper());
        var operador = supplier.PublicId.ToString();

        await booking.CreateHotelAsync(
            reserva.Id, HotelSale(operador, "Sheraton Iguazú", "Doble", "Desayuno", 96m, 140m),
            CancellationToken.None);
        await booking.CreateHotelAsync(
            reserva.Id,
            HotelSale(operador, "Sheraton Iguazú", "Doble", "Desayuno", 130m, 180m, roomCategory: "Superior"),
            CancellationToken.None);

        var sales = await context.RateSupplierSales.AsNoTracking().ToListAsync();
        Assert.Equal(2, sales.Count);
        Assert.Contains(sales, sale => sale.VariantLabel == "Doble con desayuno");
        Assert.Contains(sales, sale => sale.VariantLabel == "Doble Superior con desayuno");
    }

    /// <summary>
    /// El agujero silencioso que cierra el índice PARCIAL: si una unión dejó escondida la fila de esa
    /// habitación, una venta nueva NO puede caer adentro de ella (el precio quedaría donde nadie lo ve).
    /// Tiene que nacer una fila VISIBLE.
    /// </summary>
    [Fact]
    public async Task VentaNueva_SobreUnaHabitacionConFilaEscondida_SeAprendeEnUnaFilaVISIBLE()
    {
        await using var context = CreateContext();
        context.Suppliers.Add(new Supplier { Id = 1, Name = "Ola" });
        context.Rates.Add(HotelRate(1, "Maitei Posadas", "Posadas"));
        var escondida = Sale(1, 1, "doble|desayuno|", "Doble con desayuno", 48m, DateTime.UtcNow.AddDays(-30));
        escondida.AbsorbedByTidyUpActionId = 999; // la escondió una unión
        context.RateSupplierSales.Add(escondida);
        await context.SaveChangesAsync();

        await CatalogSaleUpsert.UpsertAsync(
            context, rateId: 1, supplierId: 1,
            new CatalogUnitization.Unitized(55m, 0m, 80m, 2, CatalogPriceUnits.NocheHabitacion),
            currency: "USD", soldAt: DateTime.UtcNow, reservaId: null,
            variant: CatalogVariant.ForHotel("Doble", "Desayuno", null), CancellationToken.None);

        var sales = await context.RateSupplierSales.AsNoTracking().ToListAsync();
        Assert.Equal(2, sales.Count);

        var visible = Assert.Single(sales.Where(sale => sale.AbsorbedByTidyUpActionId == null));
        Assert.Equal(55m, visible.LastNetCost);
        Assert.Equal(1, visible.SalesCount);

        // La escondida quedó intacta: la venta nueva no la tocó ni la resucitó.
        var hidden = sales.Single(sale => sale.AbsorbedByTidyUpActionId != null);
        Assert.Equal(48m, hidden.LastNetCost);
    }

    [Fact]
    public async Task PaqueteYAsistencia_NoTienenVariante()
    {
        await using var context = CreateContext();
        var (reserva, supplier) = await SeedAsync(context);
        var booking = CreateBookingService(context, CreateMapper());

        await booking.CreatePackageAsync(reserva.Id, new CreatePackageRequest(
            SupplierId: supplier.PublicId.ToString(), PackageName: "Bariloche 4 noches", Destination: "Bariloche",
            StartDate: DateTime.UtcNow.Date.AddDays(10), EndDate: DateTime.UtcNow.Date.AddDays(14),
            IncludesHotel: true, IncludesFlight: true, IncludesTransfer: false, IncludesExcursions: false,
            IncludesMeals: false, Adults: 2, Children: 0, Itinerary: null,
            NetCost: 800m, SalePrice: 1000m, Commission: 200m, Notes: null,
            Currency: "USD",
            NewCatalogProduct: new NewCatalogProductRequest(
                "Bariloche 4 noches", "Bariloche", supplier.PublicId.ToString())),
            CancellationToken.None);

        var sale = Assert.Single(await context.RateSupplierSales.AsNoTracking().ToListAsync());
        Assert.Equal(string.Empty, sale.VariantKey);
        Assert.Equal(string.Empty, sale.VariantLabel);
    }

    // =====================================================================================
    // M-14 — la lista: agrupada por habitación, con solapas y tope de 3
    // =====================================================================================

    [Fact]
    public async Task Listado_AgrupaPorHabitacionYAdentroLosOperadores()
    {
        await using var context = CreateContext();
        var ola = new Supplier { Id = 1, Name = "Ola Mayorista" };
        var julia = new Supplier { Id = 2, Name = "Julia Tours" };
        context.Suppliers.AddRange(ola, julia);
        context.Rates.Add(HotelRate(1, "Maitei Posadas", "Posadas"));
        context.RateSupplierSales.AddRange(
            Sale(1, ola.Id, "doble|desayuno|", "Doble con desayuno", 48m, DateTime.UtcNow.AddDays(-5)),
            Sale(1, julia.Id, "doble|desayuno|", "Doble con desayuno", 52m, DateTime.UtcNow.AddDays(-2)),
            Sale(1, ola.Id, "triple|desayuno|", "Triple con desayuno", 70m, DateTime.UtcNow.AddDays(-1)));
        await context.SaveChangesAsync();

        var page = await CreateRateService(context)
            .GetLearnedProductsAsync(new LearnedProductsQuery(), CancellationToken.None);

        var product = Assert.Single(page.Items);
        Assert.Equal(2, product.Variants.Count);

        // La habitación del precio más nuevo va arriba.
        Assert.Equal("Triple con desayuno", product.Variants[0].VariantLabel);
        Assert.Single(product.Variants[0].Suppliers);

        var doble = product.Variants[1];
        Assert.Equal("Doble con desayuno", doble.VariantLabel);
        Assert.Equal(2, doble.Suppliers.Count);
        Assert.Equal("Julia Tours", doble.Suppliers[0].SupplierName); // el más nuevo primero
    }

    [Fact]
    public async Task Listado_MuestraHastaTresPreciosYAvisaCuantosFaltan()
    {
        await using var context = CreateContext();
        context.Suppliers.AddRange(
            new Supplier { Id = 1, Name = "Ola" }, new Supplier { Id = 2, Name = "Julia" },
            new Supplier { Id = 3, Name = "Ñandú" }, new Supplier { Id = 4, Name = "Aeromundo" });
        context.Rates.Add(HotelRate(1, "Maitei Posadas", "Posadas"));
        for (var supplierId = 1; supplierId <= 4; supplierId++)
        {
            context.RateSupplierSales.Add(Sale(
                1, supplierId, "doble|desayuno|", "Doble con desayuno", 40m + supplierId,
                DateTime.UtcNow.AddDays(-supplierId)));
        }
        await context.SaveChangesAsync();

        var page = await CreateRateService(context)
            .GetLearnedProductsAsync(new LearnedProductsQuery(), CancellationToken.None);

        var product = Assert.Single(page.Items);
        Assert.Equal(4, product.TotalPriceRows);
        Assert.Equal(3, product.Variants.Sum(variant => variant.Suppliers.Count));
        Assert.Equal(1, product.HiddenPriceRows);
        Assert.Equal("+ 1 precio más — tocá el hotel para verlos", product.MorePricesText);

        // La ficha del producto los trae TODOS, sin tope.
        var ficha = await CreateRateService(context)
            .GetLearnedProductAsync(product.ProductPublicId, CancellationToken.None);
        Assert.NotNull(ficha);
        Assert.Equal(4, ficha!.Variants.Sum(variant => variant.Suppliers.Count));
        Assert.Equal(string.Empty, ficha.MorePricesText);
    }

    [Fact]
    public async Task Listado_TraeLasSeisSolapasConSuConteo()
    {
        await using var context = CreateContext();
        context.Rates.AddRange(
            HotelRate(1, "Maitei Posadas", "Posadas"),
            HotelRate(2, "Howard Johnson", "Posadas"),
            OtherTypeRate(3, "Aereo", "Buenos Aires – Miami"));
        await context.SaveChangesAsync();

        var page = await CreateRateService(context)
            .GetLearnedProductsAsync(new LearnedProductsQuery(), CancellationToken.None);

        // Seis desde el addendum firmado 2026-08-08 (V17=C): se sumó "Excursiones".
        Assert.Equal(6, page.Tabs.Count);
        Assert.Equal(2, page.Tabs.Single(tab => tab.ServiceType == "Hotel").Count);
        Assert.Equal(1, page.Tabs.Single(tab => tab.ServiceType == "Aereo").Count);
        Assert.Equal(0, page.Tabs.Single(tab => tab.ServiceType == "Paquete").Count);
        Assert.Equal("Hoteles", page.Tabs.Single(tab => tab.ServiceType == "Hotel").Label);
        Assert.Equal("Aéreos", page.Tabs.Single(tab => tab.ServiceType == "Aereo").Label);
        Assert.Equal("Excursiones", page.Tabs.Single(tab => tab.ServiceType == "Excursion").Label);
    }

    // =====================================================================================
    // V17=C (addendum firmado 2026-08-08): la excursión tiene solapa propia; "Otro" queda afuera
    // =====================================================================================

    /// <summary>
    /// El agujero que tapa este test: una excursión se podía cargar y vender, pero no tenía solapa —
    /// quedaba invisible en el Tarifario.
    /// </summary>
    [Fact]
    public async Task Excursion_LoQueSeVendeSeAprendeYAparceEnSuSolapa()
    {
        await using var context = CreateContext();
        context.Suppliers.Add(new Supplier { Id = 1, Name = "Ola" });
        context.Rates.Add(OtherTypeRate(1, "Excursion", "Cataratas día completo"));
        await context.SaveChangesAsync();

        // El ÚNICO escritor de la memoria de precios (por donde pasa toda venta).
        await CatalogSaleUpsert.UpsertAsync(
            context, rateId: 1, supplierId: 1,
            new CatalogUnitization.Unitized(45m, 0m, 60m, 1, CatalogPriceUnits.Pasajero),
            currency: "USD", soldAt: DateTime.UtcNow.AddDays(-2), reservaId: null,
            variant: CatalogVariant.None, CancellationToken.None);

        var page = await CreateRateService(context)
            .GetLearnedProductsAsync(new LearnedProductsQuery(), CancellationToken.None);

        Assert.Equal(1, page.Tabs.Single(tab => tab.ServiceType == "Excursion").Count);

        var excursion = Assert.Single(page.Items);
        Assert.Equal("Cataratas día completo", excursion.Name);
        // La excursión no tiene habitación ni cabina: su variante va vacía, como paquete y asistencia.
        var variant = Assert.Single(excursion.Variants);
        Assert.Equal(string.Empty, variant.VariantKey);
        Assert.Equal(45m, Assert.Single(variant.Suppliers).Price);
    }

    [Fact]
    public async Task AltaAMano_DeTipoOtro_SeRechazaConUnMensajeQueSeEntiende()
    {
        await using var context = CreateContext();

        var error = await Assert.ThrowsAsync<RateValidationException>(() =>
            CreateRateService(context).CreateSimpleProductAsync(new CreateSimpleProductRequest
            {
                ServiceType = "Otro",
                Name = "Cargo de gestión",
                Price = 5000m,
                Currency = "ARS"
            }, CancellationToken.None));

        Assert.Equal(
            "\"Otro\" no se carga en el tarifario. Elegí el tipo que corresponda, o cargalo directo en la reserva.",
            error.Message);
        Assert.Equal(0, await context.Rates.CountAsync());
    }

    [Fact]
    public async Task Listado_LosProductosDeTipoOtro_NoSeListanNiTienenSolapa()
    {
        await using var context = CreateContext();
        context.Rates.AddRange(
            HotelRate(1, "Maitei Posadas", "Posadas"),
            OtherTypeRate(2, "Otro", "Cargo de gestión"));
        await context.SaveChangesAsync();

        var page = await CreateRateService(context)
            .GetLearnedProductsAsync(new LearnedProductsQuery(), CancellationToken.None);

        Assert.Single(page.Items);
        Assert.Equal("Maitei Posadas", page.Items[0].Name);
        Assert.DoesNotContain(page.Tabs, tab => tab.ServiceType == "Otro");
        // Y el dato NO se borró: sigue en la base (nada se borra, 2026-08-03).
        Assert.Equal(2, await context.Rates.CountAsync());
    }

    // =====================================================================================
    // El producto que no existe: pedirlo no puede reventar
    // =====================================================================================

    [Fact]
    public async Task Ficha_DeUnProductoQueNoExiste_DevuelveNadaSinReventar()
    {
        await using var context = CreateContext();
        var service = CreateRateService(context);

        // El id de un producto que nunca existió (o que alguien borró): tiene que responder "no está",
        // nunca caerse. Antes, un descuido de llaves lo hacía explotar con un error técnico.
        Assert.Null(await service.GetByPublicIdAsync(Guid.NewGuid().ToString(), CancellationToken.None));
        Assert.Null(await service.GetByPublicIdAsync("esto-no-es-un-id", CancellationToken.None));
        Assert.Null(await service.GetLearnedProductAsync(Guid.NewGuid(), CancellationToken.None));
    }

    /// <summary>V3=A: sin dato de habitación, la celda va VACÍA. Nunca "Sin especificar".</summary>
    [Fact]
    public async Task Listado_SinHabitacionCargada_LaEtiquetaVaVacia()
    {
        await using var context = CreateContext();
        context.Suppliers.Add(new Supplier { Id = 1, Name = "Ola" });
        context.Rates.Add(HotelRate(1, "Maitei Posadas", "Posadas"));
        context.RateSupplierSales.Add(
            Sale(1, 1, string.Empty, string.Empty, 48m, DateTime.UtcNow.AddDays(-3)));
        await context.SaveChangesAsync();

        var page = await CreateRateService(context)
            .GetLearnedProductsAsync(new LearnedProductsQuery(), CancellationToken.None);

        var variant = Assert.Single(Assert.Single(page.Items).Variants);
        Assert.Equal(string.Empty, variant.VariantLabel);
    }

    // =====================================================================================
    // M-15 — qué precio sugerir al vender
    // =====================================================================================

    [Fact]
    public async Task Sugerencia_DeLaMismaHabitacion_SePuedePrecargar()
    {
        await using var context = CreateContext();
        context.Suppliers.Add(new Supplier { Id = 1, Name = "Ola Mayorista" });
        var rate = HotelRate(1, "Maitei Posadas", "Posadas");
        context.Rates.Add(rate);
        context.RateSupplierSales.Add(
            Sale(1, 1, "doble|desayuno|", "Doble con desayuno", 48m, new DateTime(2026, 5, 22, 0, 0, 0, DateTimeKind.Utc)));
        await context.SaveChangesAsync();

        var suggestion = await CreateRateService(context).GetVariantPriceSuggestionAsync(
            new VariantPriceSuggestionQuery
            {
                RatePublicId = rate.PublicId,
                RoomType = "Doble",
                MealPlan = "Desayuno"
            }, CancellationToken.None);

        Assert.NotNull(suggestion);
        Assert.True(suggestion!.IsSameVariant);
        Assert.Equal(48m, suggestion.Price);
        Assert.Equal("Costo", suggestion.PriceKind);
        Assert.Contains("Ola Mayorista", suggestion.SuggestionText);
        Assert.Contains("Doble con desayuno", suggestion.SuggestionText);
    }

    /// <summary>
    /// V9=A: si de esa habitación no hay precio, el de la parecida NO se precarga — viaja marcado y con
    /// una frase que dice de cuál habitación es.
    /// </summary>
    [Fact]
    public async Task Sugerencia_DeOtraHabitacion_ViajaMarcadaParaNoPrecargar()
    {
        await using var context = CreateContext();
        context.Suppliers.Add(new Supplier { Id = 1, Name = "Ola Mayorista" });
        var rate = HotelRate(1, "Maitei Posadas", "Posadas");
        context.Rates.Add(rate);
        context.RateSupplierSales.Add(
            Sale(1, 1, "doble|desayuno|", "Doble con desayuno", 48m, DateTime.UtcNow.AddDays(-10)));
        await context.SaveChangesAsync();

        var suggestion = await CreateRateService(context).GetVariantPriceSuggestionAsync(
            new VariantPriceSuggestionQuery
            {
                RatePublicId = rate.PublicId,
                RoomType = "Triple",
                MealPlan = "Desayuno"
            }, CancellationToken.None);

        Assert.NotNull(suggestion);
        Assert.False(suggestion!.IsSameVariant);
        Assert.Equal("Doble con desayuno", suggestion.VariantLabel);
        Assert.Contains("No hay precio de esa habitación", suggestion.SuggestionText);
    }

    /// <summary>
    /// Sin ninguna venta Y sin precio cargado a mano no hay nada que sugerir: no se inventa un cero.
    /// (Si hubiera precio cargado a mano, SÍ se sugiere — ver el test del alta a mano más abajo.)
    /// </summary>
    [Fact]
    public async Task Sugerencia_SinPreciosDeNingunTipo_NoDevuelveNada()
    {
        await using var context = CreateContext();
        var rate = HotelRate(1, "Maitei Posadas", "Posadas");
        rate.NetCost = 0m;
        rate.SalePrice = 0m;
        context.Rates.Add(rate);
        await context.SaveChangesAsync();

        var suggestion = await CreateRateService(context).GetVariantPriceSuggestionAsync(
            new VariantPriceSuggestionQuery { RatePublicId = rate.PublicId, RoomType = "Doble", MealPlan = "Desayuno" },
            CancellationToken.None);

        Assert.Null(suggestion);
    }

    /// <summary>F-14: sin permiso de costos, la sugerencia trae el precio de VENTA, nunca el costo.</summary>
    [Fact]
    public async Task Sugerencia_SinPermisoDeCostos_TraeLaVenta()
    {
        await using var context = CreateContext();
        context.Suppliers.Add(new Supplier { Id = 1, Name = "Ola Mayorista" });
        var rate = HotelRate(1, "Maitei Posadas", "Posadas");
        context.Rates.Add(rate);
        var sale = Sale(1, 1, "doble|desayuno|", "Doble con desayuno", 48m, DateTime.UtcNow.AddDays(-3));
        sale.LastSalePrice = 65m;
        context.RateSupplierSales.Add(sale);
        await context.SaveChangesAsync();

        var suggestion = await CreateRateService(context, canSeeCost: false).GetVariantPriceSuggestionAsync(
            new VariantPriceSuggestionQuery { RatePublicId = rate.PublicId, RoomType = "Doble", MealPlan = "Desayuno" },
            CancellationToken.None);

        Assert.NotNull(suggestion);
        Assert.Equal(65m, suggestion!.Price);
        Assert.Equal("Venta", suggestion.PriceKind);
    }

    // =====================================================================================
    // M-19 — el texto libre con memoria
    // =====================================================================================

    [Fact]
    public async Task NombresFinos_OfreceLosQueYaSeUsaronTalComoSeEscribieron()
    {
        await using var context = CreateContext();
        context.Suppliers.Add(new Supplier { Id = 1, Name = "Ola" });
        // La memoria sale de lo que YA esta escrito: la habitacion de una venta y la de un producto
        // cargado a mano. Se guarda el texto tal cual lo escribio la persona.
        var conNombreFino = HotelRate(1, "Maitei Posadas", "Posadas");
        conNombreFino.RoomCategory = "Superior";
        var otro = HotelRate(2, "Amerian Posadas", "Posadas");
        otro.RoomCategory = "Vista al mar";
        context.Rates.AddRange(conNombreFino, otro);
        await context.SaveChangesAsync();

        var names = await CreateRateService(context)
            .GetVariantNameSuggestionsAsync("Hotel", search: null, CancellationToken.None);

        // Tal como se escribieron: "Vista al mar", NUNCA "Vista Al Mar" ni la clave en minuscula.
        Assert.Contains("Superior", names);
        Assert.Contains("Vista al mar", names);
    }

    [Theory]
    [InlineData("SUPERIOR", "Superior")]
    [InlineData("superio", "Superior")]
    [InlineData("sup", "Superior")]
    public async Task NombresFinos_UnificaLasVariacionesDeTipeo(string written, string expected)
    {
        await using var context = CreateContext();
        var rate = HotelRate(1, "Maitei Posadas", "Posadas");
        rate.RoomCategory = "Superior";
        context.Rates.Add(rate);
        await context.SaveChangesAsync();

        var resolved = await CreateRateService(context)
            .ResolveVariantNameAsync("Hotel", written, CancellationToken.None);

        Assert.Equal(expected, resolved);
    }

    /// <summary>
    /// La memoria mira TODOS los nombres conocidos, no una pagina de sugerencias: con muchos nombres
    /// cargados, escribir "sup" tiene que seguir encontrando "Superior" aunque no entre en los primeros 10.
    /// </summary>
    [Fact]
    public async Task NombresFinos_ConMuchosNombresCargados_IgualUnifica()
    {
        await using var context = CreateContext();
        for (var i = 1; i <= 14; i++)
        {
            var rate = HotelRate(i, $"Hotel {i}", "Posadas");
            // "Superior" queda ultimo alfabeticamente entre los "Vista ..." para que no sea el primero.
            rate.RoomCategory = i == 14 ? "Superior" : $"Vista {i:00}";
            context.Rates.Add(rate);
        }
        await context.SaveChangesAsync();

        var resolved = await CreateRateService(context)
            .ResolveVariantNameAsync("hotel", "sup", CancellationToken.None);

        Assert.Equal("Superior", resolved);
    }

    /// <summary>Func-N10 tambien aca: el tipo se compara sin importar como este escrito.</summary>
    [Theory]
    [InlineData("hotel")]
    [InlineData("HOTEL")]
    public async Task NombresFinos_ElTipoSeComparaSinImportarMayusculas(string serviceTypeAsSent)
    {
        await using var context = CreateContext();
        var rate = HotelRate(1, "Maitei Posadas", "Posadas");
        rate.RoomCategory = "Superior";
        context.Rates.Add(rate);
        await context.SaveChangesAsync();

        var resolved = await CreateRateService(context)
            .ResolveVariantNameAsync(serviceTypeAsSent, "SUP", CancellationToken.None);

        Assert.Equal("Superior", resolved);
    }

    [Fact]
    public async Task NombresFinos_AlgoNuevoSeRespetaTalCual()
    {
        await using var context = CreateContext();
        var rate = HotelRate(1, "Maitei Posadas", "Posadas");
        rate.RoomCategory = "Superior";
        context.Rates.Add(rate);
        await context.SaveChangesAsync();

        var resolved = await CreateRateService(context)
            .ResolveVariantNameAsync("Hotel", "Vista al lago", CancellationToken.None);

        Assert.Equal("Vista al lago", resolved);
    }

    // =====================================================================================
    // M-18 — corregir la etiqueta (sin tocar importes)
    // =====================================================================================

    [Fact]
    public async Task CorregirHabitacion_CambiaLaEtiquetaYNoTocaLosImportes()
    {
        await using var context = CreateContext();
        context.Suppliers.Add(new Supplier { Id = 1, Name = "Ola" });
        var rate = HotelRate(1, "Maitei Posadas", "Posadas");
        context.Rates.Add(rate);
        context.RateSupplierSales.Add(
            Sale(1, 1, "doble|desayuno|", "Doble con desayuno", 48m, DateTime.UtcNow.AddDays(-3)));
        await context.SaveChangesAsync();

        var result = await CreateRateService(context).RenameVariantAsync(new RenameVariantRequest
        {
            ProductPublicId = rate.PublicId,
            CurrentVariantKey = "doble|desayuno|",
            RoomType = "Doble",
            MealPlan = "Media pensión"
        }, CancellationToken.None);

        Assert.Equal("Doble con media pensión", result.VariantLabel);
        Assert.False(result.MergedWithExisting);

        var sale = Assert.Single(await context.RateSupplierSales.AsNoTracking().ToListAsync());
        Assert.Equal("doble|media_pension|", sale.VariantKey);
        Assert.Equal(48m, sale.LastNetCost); // el importe no se tocó
    }

    /// <summary>
    /// El caso que ANTES borraba plata: al corregir, la habitación quedaba igual que otra que ya existía y
    /// la fila perdedora se ELIMINABA. Ahora se esconde con su foto y se puede deshacer (regla del dueño
    /// 2026-08-03: nada se borra).
    /// </summary>
    [Fact]
    public async Task CorregirHabitacion_SiQuedaIgualQueOtra_LaOtraSeEsconde_NoSeBorra()
    {
        await using var context = CreateContext();
        context.Suppliers.Add(new Supplier { Id = 1, Name = "Ola" });
        var rate = HotelRate(1, "Maitei Posadas", "Posadas");
        context.Rates.Add(rate);
        context.RateSupplierSales.AddRange(
            Sale(1, 1, "doble|desayuno|", "Doble con desayuno", 48m, DateTime.UtcNow.AddDays(-10)),
            Sale(1, 1, "doble|media_pension|", "Doble con media pensión", 61m, DateTime.UtcNow.AddDays(-1)));
        await context.SaveChangesAsync();

        // Corregir la de desayuno a media pensión: quedan las dos iguales.
        var result = await CreateRateService(context).RenameVariantAsync(new RenameVariantRequest
        {
            ProductPublicId = rate.PublicId,
            CurrentVariantKey = "doble|desayuno|",
            RoomType = "Doble",
            MealPlan = "Media pensión"
        }, CancellationToken.None);

        Assert.True(result.MergedWithExisting);

        // Se ve UNA sola, con el precio más nuevo...
        var visible = Assert.Single(await context.RateSupplierSales.AsNoTracking()
            .Where(sale => sale.AbsorbedByTidyUpActionId == null).ToListAsync());
        Assert.Equal(61m, visible.LastNetCost);
        Assert.Equal(1, visible.SalesCount); // el contador no se infla: la otra sigue con el suyo

        // ...pero las DOS siguen en la base: la que perdió quedó escondida, con su importe intacto.
        Assert.Equal(2, await context.RateSupplierSales.CountAsync());
        var hidden = await context.RateSupplierSales.AsNoTracking()
            .SingleAsync(sale => sale.AbsorbedByTidyUpActionId != null);
        Assert.Equal(48m, hidden.LastNetCost);

        // Y quedó el rastro con su Deshacer, igual que cuando el bibliotecario une dos productos.
        var action = await context.CatalogTidyUpActions.AsNoTracking().SingleAsync();
        Assert.Equal("HabitacionCorregida", action.Kind);

        // Dos fotos: la de la fila que se escondió y la de la que se queda (a esa se le pisa la etiqueta,
        // así que también tiene que poder volver a como estaba).
        var fotos = await context.CatalogTidyUpSaleChanges.AsNoTracking().ToListAsync();
        Assert.Equal(2, fotos.Count);
        var escondida = fotos.Single(foto => foto.Kind == "Escondida");
        Assert.Equal(48m, escondida.PreviousNetCost);
        Assert.Equal("doble|desayuno|", escondida.PreviousVariantKey);
        Assert.Contains(fotos, foto => foto.Kind == "Pisada" && foto.PreviousNetCost == 61m);

        var log = await CreateLibrarian(context).GetTidyUpLogAsync(CancellationToken.None);
        var line = Assert.Single(log.Actions);
        Assert.Equal("Doble con desayuno → Doble con media pensión", line.Summary);
        Assert.Equal("en Maitei Posadas", line.Detail);
        Assert.True(line.CanUndo);
    }

    [Fact]
    public async Task CorregirHabitacion_SePuedeDeshacerYVuelveTodoComoEstaba()
    {
        await using var context = CreateContext();
        context.Suppliers.Add(new Supplier { Id = 1, Name = "Ola" });
        var rate = HotelRate(1, "Maitei Posadas", "Posadas");
        context.Rates.Add(rate);
        context.RateSupplierSales.AddRange(
            Sale(1, 1, "doble|desayuno|", "Doble con desayuno", 48m, DateTime.UtcNow.AddDays(-10)),
            Sale(1, 1, "doble|media_pension|", "Doble con media pensión", 61m, DateTime.UtcNow.AddDays(-1)));
        await context.SaveChangesAsync();

        await CreateRateService(context).RenameVariantAsync(new RenameVariantRequest
        {
            ProductPublicId = rate.PublicId,
            CurrentVariantKey = "doble|desayuno|",
            RoomType = "Doble",
            MealPlan = "Media pensión"
        }, CancellationToken.None);

        var action = await context.CatalogTidyUpActions.AsNoTracking().SingleAsync();
        await CreateLibrarian(context).UndoTidyUpActionAsync(action.PublicId, CancellationToken.None);

        var sales = await context.RateSupplierSales.AsNoTracking().OrderBy(sale => sale.Id).ToListAsync();
        Assert.Equal(2, sales.Count);
        Assert.All(sales, sale => Assert.Null(sale.AbsorbedByTidyUpActionId));
        Assert.Contains(sales, sale => sale.VariantKey == "doble|desayuno|" && sale.LastNetCost == 48m);
        Assert.Contains(sales, sale => sale.VariantKey == "doble|media_pension|" && sale.LastNetCost == 61m);

        // El producto ni se enteró: corregir una habitación nunca lo apagó ni le tocó el nombre.
        var product = await context.Rates.AsNoTracking().SingleAsync();
        Assert.True(product.IsActive);
        Assert.Equal("Maitei Posadas", product.HotelName);
    }

    /// <summary>
    /// Una fila que una unión escondió NO es una habitación del producto: corregir otra habitación para que
    /// quede igual a la escondida no la puede "juntar" con ella (si no, escondería la buena contra una que
    /// ya nadie ve).
    /// </summary>
    [Fact]
    public async Task CorregirHabitacion_NoSeJuntaConUnaFilaEscondida()
    {
        await using var context = CreateContext();
        context.Suppliers.Add(new Supplier { Id = 1, Name = "Ola" });
        var rate = HotelRate(1, "Maitei Posadas", "Posadas");
        context.Rates.Add(rate);
        var visible = Sale(1, 1, "doble|desayuno|", "Doble con desayuno", 48m, DateTime.UtcNow.AddDays(-10));
        var escondida = Sale(1, 1, "doble|media_pension|", "Doble con media pensión", 61m, DateTime.UtcNow.AddDays(-1));
        escondida.AbsorbedByTidyUpActionId = 12345; // la escondió una unión anterior
        context.RateSupplierSales.AddRange(visible, escondida);
        await context.SaveChangesAsync();

        var result = await CreateRateService(context).RenameVariantAsync(new RenameVariantRequest
        {
            ProductPublicId = rate.PublicId,
            CurrentVariantKey = "doble|desayuno|",
            RoomType = "Doble",
            MealPlan = "Media pensión"
        }, CancellationToken.None);

        Assert.False(result.MergedWithExisting);

        var after = await context.RateSupplierSales.AsNoTracking().OrderBy(sale => sale.Id).ToListAsync();
        Assert.Equal(2, after.Count);
        // La visible se corrigió y SIGUE visible (no se escondió contra la invisible).
        var corregida = after.Single(sale => sale.Id == visible.Id);
        Assert.Equal("doble|media_pension|", corregida.VariantKey);
        Assert.Null(corregida.AbsorbedByTidyUpActionId);
        // Y la escondida sigue escondida, sin que nadie la toque.
        Assert.Equal(12345, after.Single(sale => sale.Id == escondida.Id).AbsorbedByTidyUpActionId);
    }

    /// <summary>
    /// Corregir una habitación de una fila que YA tiene foto del bibliotecario (porque una unión la movió).
    /// Antes se intentaba BORRAR esa fila y la base lo impedía (la foto la referencia): 500 en la cara.
    /// </summary>
    [Fact]
    public async Task CorregirHabitacion_DeUnaFilaQueElBibliotecarioYaHabiaMovido_NoRompe()
    {
        await using var context = CreateContext();
        context.Suppliers.Add(new Supplier { Id = 1, Name = "Ola" });
        var survivor = HotelRate(1, "Sheraton Iguazú", "Puerto Iguazú");
        var absorbed = HotelRate(2, "Sheraton Iguazú - Doble Superior", "Puerto Iguazú");
        absorbed.MealPlan = "Desayuno";
        context.Rates.AddRange(survivor, absorbed);
        // La fila del absorbido (se va a mudar al sobreviviente y va a quedar con foto)...
        context.RateSupplierSales.Add(Sale(2, 1, string.Empty, string.Empty, 55m, DateTime.UtcNow.AddDays(-3)));
        // ...y una del sobreviviente con la habitación a la que después la vamos a corregir.
        context.RateSupplierSales.Add(
            Sale(1, 1, "triple|desayuno|", "Triple con desayuno", 70m, DateTime.UtcNow.AddDays(-1)));
        await context.SaveChangesAsync();

        var librarian = CreateLibrarian(context);
        await librarian.MergeProductsAsync(new MergeProductsRequest
        {
            SurvivorPublicId = survivor.PublicId,
            AbsorbedPublicId = absorbed.PublicId
        }, CancellationToken.None);

        var movida = await context.RateSupplierSales.AsNoTracking()
            .SingleAsync(sale => sale.LastNetCost == 55m);

        // Ahora se corrige esa misma fila para que quede igual que la triple: la perdedora se esconde.
        var result = await CreateRateService(context).RenameVariantAsync(new RenameVariantRequest
        {
            ProductPublicId = survivor.PublicId,
            CurrentVariantKey = movida.VariantKey,
            RoomType = "Triple",
            MealPlan = "Desayuno"
        }, CancellationToken.None);

        Assert.True(result.MergedWithExisting);
        // Nada se borró: las dos filas siguen ahí y la foto vieja del bibliotecario sigue apuntando a la suya.
        Assert.Equal(2, await context.RateSupplierSales.CountAsync());
        Assert.True(await context.CatalogTidyUpSaleChanges
            .AnyAsync(change => change.RateSupplierSaleId == movida.Id));
    }

    /// <summary>
    /// Lo que necesita la pantalla para que "Corregir" arranque con la habitación REAL: las piezas sueltas,
    /// escritas como las ofrecen los desplegables. Sin esto el formulario arrancaba siempre en
    /// "Doble / Desayuno" y corregir el nombre fino de una triple la convertía en doble.
    /// </summary>
    [Fact]
    public async Task Listado_CadaHabitacionTraeSusPiezasParaElFormularioDeCorregir()
    {
        await using var context = CreateContext();
        context.Suppliers.Add(new Supplier { Id = 1, Name = "Ola" });
        context.Rates.Add(HotelRate(1, "Maitei Posadas", "Posadas"));
        context.Rates.Add(OtherTypeRate(2, "Aereo", "Buenos Aires – Miami"));
        context.RateSupplierSales.AddRange(
            Sale(1, 1, "triple|media_pension|superior", "Triple Superior con media pensión", 70m,
                DateTime.UtcNow.AddDays(-1)),
            Sale(2, 1, "ejecutiva", "Ejecutiva", 900m, DateTime.UtcNow.AddDays(-2)));
        await context.SaveChangesAsync();

        var page = await CreateRateService(context)
            .GetLearnedProductsAsync(new LearnedProductsQuery(), CancellationToken.None);

        var hotel = page.Items.Single(item => item.ServiceType == "Hotel").Variants.Single();
        Assert.Equal("Triple", hotel.RoomType);
        Assert.Equal("Media Pension", hotel.MealPlan);
        Assert.Equal("Superior", hotel.RoomCategory);
        Assert.Null(hotel.CabinClass);

        var aereo = page.Items.Single(item => item.ServiceType == "Aereo").Variants.Single();
        Assert.Equal("Business", aereo.CabinClass);
        Assert.Null(aereo.RoomType);
    }

    /// <summary>
    /// Correcciones ENCADENADAS deshechas fuera de orden (doble→triple, después triple→suite): deshacer la
    /// PRIMERA dejaría la habitación en "doble" mientras la segunda sigue figurando como vigente — ni el
    /// original ni el último. Se frena, con el mismo aviso que las uniones encadenadas.
    /// </summary>
    [Fact]
    public async Task CorregirHabitacion_DeshacerLaPrimeraDeDosCorrecciones_SeRechaza()
    {
        await using var context = CreateContext();
        context.Suppliers.Add(new Supplier { Id = 1, Name = "Ola" });
        var rate = HotelRate(1, "Maitei Posadas", "Posadas");
        context.Rates.Add(rate);
        context.RateSupplierSales.Add(
            Sale(1, 1, "doble|desayuno|", "Doble con desayuno", 48m, DateTime.UtcNow.AddDays(-10)));
        await context.SaveChangesAsync();
        var service = CreateRateService(context);

        await service.RenameVariantAsync(new RenameVariantRequest
        {
            ProductPublicId = rate.PublicId,
            CurrentVariantKey = "doble|desayuno|",
            RoomType = "Triple",
            MealPlan = "Desayuno"
        }, CancellationToken.None);

        await service.RenameVariantAsync(new RenameVariantRequest
        {
            ProductPublicId = rate.PublicId,
            CurrentVariantKey = "triple|desayuno|",
            RoomType = "Suite",
            MealPlan = "Desayuno"
        }, CancellationToken.None);

        var primera = await context.CatalogTidyUpActions.AsNoTracking()
            .OrderBy(action => action.Id).FirstAsync();

        var error = await Assert.ThrowsAsync<CatalogTidyUpNotReversibleException>(() =>
            CreateLibrarian(context).UndoTidyUpActionAsync(primera.PublicId, CancellationToken.None));
        Assert.Equal(
            "Después de esto se ordenaron esos mismos precios otra vez. Deshacé primero el movimiento más nuevo.",
            error.Message);

        // La habitación quedó como la dejó la última corrección: no se reescribió a medias.
        Assert.Equal("suite|desayuno|", (await context.RateSupplierSales.AsNoTracking().SingleAsync()).VariantKey);
    }

    /// <summary>
    /// El caso más traicionero: la segunda corrección NO mueve la fila de la primera — le pisa los importes
    /// EN EL LUGAR (es el "gemelo" que se queda). Mirando la fila, todo parece intacto: mismo producto,
    /// misma habitación, visible. Deshacer la primera corrección se llevaría los importes del OTRO precio y
    /// haría desaparecer una habitación. Lo frena la regla madre: hay un movimiento más nuevo encima.
    /// </summary>
    [Fact]
    public async Task Deshacer_CuandoUnMovimientoMasNuevoLePisoLosImportesEnElLugar_SeRechaza()
    {
        await using var context = CreateContext();
        context.Suppliers.Add(new Supplier { Id = 1, Name = "Ola" });
        var rate = HotelRate(1, "Maitei Posadas", "Posadas");
        context.Rates.Add(rate);
        var primeraFila = Sale(1, 1, "doble|desayuno|", "Doble con desayuno", 48m, DateTime.UtcNow.AddDays(-10));
        var segundaFila = Sale(1, 1, "suite|desayuno|", "Suite con desayuno", 61m, DateTime.UtcNow.AddDays(-2));
        context.RateSupplierSales.AddRange(primeraFila, segundaFila);
        await context.SaveChangesAsync();
        var service = CreateRateService(context);

        // Corrección 1: la doble pasa a triple (mueve la clave de la PRIMERA fila).
        await service.RenameVariantAsync(new RenameVariantRequest
        {
            ProductPublicId = rate.PublicId,
            CurrentVariantKey = "doble|desayuno|",
            RoomType = "Triple",
            MealPlan = "Desayuno"
        }, CancellationToken.None);

        // Corrección 2: la suite TAMBIÉN pasa a triple. La primera fila es la gemela: se le pisan los
        // importes con los de la suite (más nueva) sin moverla ni cambiarle la clave.
        await service.RenameVariantAsync(new RenameVariantRequest
        {
            ProductPublicId = rate.PublicId,
            CurrentVariantKey = "suite|desayuno|",
            RoomType = "Triple",
            MealPlan = "Desayuno"
        }, CancellationToken.None);

        var acciones = await context.CatalogTidyUpActions.AsNoTracking().OrderBy(a => a.Id).ToListAsync();
        Assert.Equal(2, acciones.Count);
        var librarian = CreateLibrarian(context);

        var error = await Assert.ThrowsAsync<CatalogTidyUpNotReversibleException>(() =>
            librarian.UndoTidyUpActionAsync(acciones[0].PublicId, CancellationToken.None));
        Assert.Equal(
            "Después de esto se ordenaron esos mismos precios otra vez. Deshacé primero el movimiento más nuevo.",
            error.Message);

        // La fila quedó tal cual la dejó la corrección 2: no se le tocó ni un importe.
        var despues = await context.RateSupplierSales.AsNoTracking().SingleAsync(s => s.Id == primeraFila.Id);
        Assert.Equal(61m, despues.LastNetCost);
        Assert.Equal("triple|desayuno|", despues.VariantKey);

        // Y el camino correcto sigue abierto: deshacer PRIMERO el movimiento más nuevo.
        var log = await librarian.GetTidyUpLogAsync(CancellationToken.None);
        Assert.True(log.Actions.Single(linea => linea.PublicId == acciones[1].PublicId).CanUndo);

        await librarian.UndoTidyUpActionAsync(acciones[1].PublicId, CancellationToken.None);
        var restauradas = await context.RateSupplierSales.AsNoTracking().OrderBy(s => s.Id).ToListAsync();
        Assert.Equal(48m, restauradas.Single(s => s.Id == primeraFila.Id).LastNetCost);
        Assert.Equal(61m, restauradas.Single(s => s.Id == segundaFila.Id).LastNetCost);
        Assert.All(restauradas, fila => Assert.Null(fila.AbsorbedByTidyUpActionId));

        // Y recién ahí la primera se puede deshacer sola.
        await librarian.UndoTidyUpActionAsync(acciones[0].PublicId, CancellationToken.None);
        Assert.Equal("doble|desayuno|", (await context.RateSupplierSales.AsNoTracking()
            .SingleAsync(s => s.Id == primeraFila.Id)).VariantKey);
    }

    /// <summary>
    /// Unir dos productos y DESPUÉS corregir la habitación de ese precio: deshacer la unión ya no es fiel
    /// (devolvería el precio con una habitación que nadie eligió). Se frena.
    /// </summary>
    [Fact]
    public async Task Unir_YDespuesCorregirLaHabitacion_YaNoSePuedeDeshacerLaUnion()
    {
        await using var context = CreateContext();
        context.Suppliers.Add(new Supplier { Id = 1, Name = "Ola" });
        var survivor = HotelRate(1, "Maitei Posadas", "Posadas");
        var absorbed = HotelRate(2, "maitei posadas", "posadas");
        context.Rates.AddRange(survivor, absorbed);
        context.RateSupplierSales.Add(
            Sale(2, 1, "doble|desayuno|", "Doble con desayuno", 55m, DateTime.UtcNow.AddDays(-3)));
        await context.SaveChangesAsync();
        var librarian = CreateLibrarian(context);

        var merge = await librarian.MergeProductsAsync(new MergeProductsRequest
        {
            SurvivorPublicId = survivor.PublicId,
            AbsorbedPublicId = absorbed.PublicId
        }, CancellationToken.None);

        await CreateRateService(context).RenameVariantAsync(new RenameVariantRequest
        {
            ProductPublicId = survivor.PublicId,
            CurrentVariantKey = "doble|desayuno|",
            RoomType = "Triple",
            MealPlan = "Desayuno"
        }, CancellationToken.None);

        var error = await Assert.ThrowsAsync<CatalogTidyUpNotReversibleException>(() =>
            librarian.UndoTidyUpActionAsync(merge.TidyUpActionPublicId, CancellationToken.None));
        Assert.Equal(
            "Después de esto se ordenaron esos mismos precios otra vez. Deshacé primero el movimiento más nuevo.",
            error.Message);

        // Nada a medio deshacer: el producto absorbido sigue apagado y el precio sigue corregido.
        Assert.False((await context.Rates.AsNoTracking().SingleAsync(r => r.Id == absorbed.Id)).IsActive);
        Assert.Equal("triple|desayuno|", (await context.RateSupplierSales.AsNoTracking().SingleAsync()).VariantKey);
    }

    /// <summary>
    /// Guardar la corrección sin haber cambiado nada no puede ensuciar "Ver qué ordenó" con un movimiento
    /// que no movió nada (pasa seguido: el formulario ahora arranca con la habitación real cargada).
    /// </summary>
    [Fact]
    public async Task CorregirHabitacion_SiQuedaExactamenteIgual_NoDejaMovimiento()
    {
        await using var context = CreateContext();
        context.Suppliers.Add(new Supplier { Id = 1, Name = "Ola" });
        var rate = HotelRate(1, "Maitei Posadas", "Posadas");
        context.Rates.Add(rate);
        context.RateSupplierSales.Add(
            Sale(1, 1, "doble|desayuno|", "Doble con desayuno", 48m, DateTime.UtcNow.AddDays(-3)));
        await context.SaveChangesAsync();

        var result = await CreateRateService(context).RenameVariantAsync(new RenameVariantRequest
        {
            ProductPublicId = rate.PublicId,
            CurrentVariantKey = "doble|desayuno|",
            RoomType = "Doble",
            MealPlan = "Desayuno"
        }, CancellationToken.None);

        Assert.Equal("doble|desayuno|", result.VariantKey);
        Assert.False(result.MergedWithExisting);
        Assert.Equal(0, await context.CatalogTidyUpActions.CountAsync());
        Assert.Equal(0, await context.CatalogTidyUpSaleChanges.CountAsync());
    }

    /// <summary>Sin habitación elegida no se corrige nada, y el aviso habla en criollo de CADA tipo.</summary>
    [Theory]
    [InlineData("Hotel", "Elegí la habitación y el régimen.")]
    [InlineData("Aereo", "Elegí la cabina.")]
    [InlineData("Traslado", "Elegí el vehículo.")]
    public async Task CorregirHabitacion_SinElegirNada_AvisaConLaPalabraDeEseTipo(
        string serviceType, string expectedMessage)
    {
        await using var context = CreateContext();
        var rate = serviceType == "Hotel"
            ? HotelRate(1, "Maitei Posadas", "Posadas")
            : OtherTypeRate(1, serviceType, "Producto de prueba");
        context.Rates.Add(rate);
        await context.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<RateValidationException>(() =>
            CreateRateService(context).RenameVariantAsync(new RenameVariantRequest
            {
                ProductPublicId = rate.PublicId,
                CurrentVariantKey = string.Empty
            }, CancellationToken.None));

        Assert.Equal(expectedMessage, error.Message);
    }

    // =====================================================================================
    // Alta a mano CON habitación (spec §8 / V16=A): que el precio cargado a mano quede comparable
    // con los que el sistema aprende vendiendo
    // =====================================================================================

    [Fact]
    public async Task AltaAMano_ConHabitacionYRegimen_GuardaLaVariante()
    {
        await using var context = CreateContext();
        var service = CreateRateService(context);

        var result = await service.CreateSimpleProductAsync(new CreateSimpleProductRequest
        {
            ServiceType = "Hotel",
            Name = "Amerian Posadas",
            City = "Posadas",
            Price = 91000m,
            Currency = "ARS",
            RoomType = "Triple",
            MealPlan = "Media pensión"
        }, CancellationToken.None);

        Assert.NotNull(result.Created);
        Assert.Equal("triple|media_pension|", result.Created!.VariantKey);
        Assert.Equal("Triple con media pensión", result.Created.VariantLabel);

        var rate = await context.Rates.AsNoTracking().SingleAsync();
        Assert.Equal("Triple", rate.RoomType);
        Assert.Equal("Media pensión", rate.MealPlan);
    }

    [Fact]
    public async Task AltaAMano_SinVariante_SigueIgualQueAntes()
    {
        await using var context = CreateContext();
        var service = CreateRateService(context);

        var result = await service.CreateSimpleProductAsync(new CreateSimpleProductRequest
        {
            ServiceType = "Paquete",
            Name = "Bariloche 4 noches",
            Price = 410000m,
            Currency = "ARS"
        }, CancellationToken.None);

        Assert.NotNull(result.Created);
        Assert.Equal(string.Empty, result.Created!.VariantKey);
        Assert.Equal(string.Empty, result.Created.VariantLabel);

        var rate = await context.Rates.AsNoTracking().SingleAsync();
        Assert.Null(rate.RoomType);
        Assert.Null(rate.MealPlan);
    }

    /// <summary>
    /// M-19 en el alta a mano: el nombre fino pasa por la MEMORIA. Si en la agencia ya se escribió
    /// "Superior", cargar "sup" o "SUPERIOR" NO fabrica una habitación nueva.
    /// </summary>
    [Theory]
    [InlineData("sup")]
    [InlineData("SUPERIOR")]
    [InlineData("superio")]
    public async Task AltaAMano_ElNombreFinoPasaPorLaMemoria(string escritoAhora)
    {
        await using var context = CreateContext();
        context.Suppliers.Add(new Supplier { Id = 1, Name = "Ola" });
        // Ya hay una "Superior" escrita en el tarifario: esa es la escritura que manda.
        var yaCargado = HotelRate(1, "Maitei Posadas", "Posadas");
        yaCargado.RoomCategory = "Superior";
        context.Rates.Add(yaCargado);
        await context.SaveChangesAsync();
        var service = CreateRateService(context);

        var result = await service.CreateSimpleProductAsync(new CreateSimpleProductRequest
        {
            ServiceType = "Hotel",
            Name = "Amerian Posadas",
            City = "Posadas",
            Price = 90m,
            Currency = "USD",
            RoomType = "Doble",
            MealPlan = "Desayuno",
            RoomCategory = escritoAhora
        }, CancellationToken.None);

        Assert.NotNull(result.Created);
        // Misma clave que la que ya existía: no se fabricó "sup" ni "SUPERIOR" como habitaciones aparte.
        Assert.Equal("doble|desayuno|superior", result.Created!.VariantKey);
        Assert.Equal("Doble Superior con desayuno", result.Created.VariantLabel);
        Assert.Equal("Superior", (await context.Rates.AsNoTracking().SingleAsync(r => r.Id != 1)).RoomCategory);
    }

    [Fact]
    public async Task AltaAMano_ConCabinaYConVehiculo_GuardanSuVariante()
    {
        await using var context = CreateContext();
        var service = CreateRateService(context);

        var aereo = await service.CreateSimpleProductAsync(new CreateSimpleProductRequest
        {
            ServiceType = "Aereo",
            Name = "Buenos Aires – Miami",
            Price = 780m,
            Currency = "USD",
            CabinClass = "Economy"
        }, CancellationToken.None);

        var traslado = await service.CreateSimpleProductAsync(new CreateSimpleProductRequest
        {
            ServiceType = "Traslado",
            Name = "Aeropuerto – Hotel",
            Price = 30m,
            Currency = "USD",
            VehicleType = "van"
        }, CancellationToken.None);

        Assert.Equal("Económica", aereo.Created!.VariantLabel);
        Assert.Equal("Van", traslado.Created!.VariantLabel);
    }

    /// <summary>
    /// El punto de V16=A: el precio cargado a mano tiene que servir cuando alguien vende ESA habitación,
    /// igual que si lo hubiera aprendido de una venta.
    /// </summary>
    [Fact]
    public async Task AltaAMano_SuPrecioSeSugiereAlVenderEsaHabitacion()
    {
        await using var context = CreateContext();
        var service = CreateRateService(context);

        var created = await service.CreateSimpleProductAsync(new CreateSimpleProductRequest
        {
            ServiceType = "Hotel",
            Name = "Amerian Posadas",
            City = "Posadas",
            Price = 91000m,
            Currency = "ARS",
            RoomType = "Triple",
            MealPlan = "Media pensión"
        }, CancellationToken.None);

        var suggestion = await service.GetVariantPriceSuggestionAsync(new VariantPriceSuggestionQuery
        {
            RatePublicId = created.Created!.PublicId,
            RoomType = "Triple",
            MealPlan = "Media pensión"
        }, CancellationToken.None);

        Assert.NotNull(suggestion);
        Assert.True(suggestion!.IsSameVariant);
        Assert.Equal("Triple con media pensión", suggestion.VariantLabel);

        // Y si venden OTRA habitación, se avisa que el precio es de otra (no se precarga).
        var otra = await service.GetVariantPriceSuggestionAsync(new VariantPriceSuggestionQuery
        {
            RatePublicId = created.Created.PublicId,
            RoomType = "Doble",
            MealPlan = "Desayuno"
        }, CancellationToken.None);

        Assert.NotNull(otra);
        Assert.False(otra!.IsSameVariant);
    }

    // =====================================================================================
    // El armado de la etiqueta (lo que ve la persona)
    // =====================================================================================

    [Theory]
    [InlineData("Doble", "Desayuno", null, "Doble con desayuno")]
    [InlineData("Doble", "BB", "Superior", "Doble Superior con desayuno")]
    [InlineData("Triple", "HB", null, "Triple con media pensión")]
    [InlineData("dbl", "ai", null, "Doble con todo incluido")]
    [InlineData(null, "Desayuno", null, "Con desayuno")]
    [InlineData("Doble", null, null, "Doble")]
    public void EtiquetaDeHabitacion_SeEscribeEnCriollo(
        string? roomType, string? mealPlan, string? fineName, string expected)
    {
        var variant = CatalogVariant.ForHotel(roomType, mealPlan, fineName);
        Assert.Equal(expected, variant.Label);
    }

    [Fact]
    public void EtiquetaDeHabitacion_SinNingunDato_QuedaVacia()
    {
        var variant = CatalogVariant.ForHotel(null, null, null);
        Assert.Equal(string.Empty, variant.Key);
        Assert.Equal(string.Empty, variant.Label);
    }

    [Theory]
    [InlineData("Economy", "Económica")]
    [InlineData("business", "Ejecutiva")]
    public void EtiquetaDeCabina_SeEscribeEnCriollo(string cabin, string expected)
        => Assert.Equal(expected, CatalogVariant.ForFlight(cabin).Label);

    // =====================================================================================
    // Helpers de siembra
    // =====================================================================================

    private static Rate HotelRate(int id, string hotelName, string city) => new()
    {
        Id = id,
        ServiceType = "Hotel",
        ProductName = hotelName,
        HotelName = hotelName,
        City = city,
        NetCost = 100m,
        SalePrice = 160m,
        Currency = "USD",
        PriceUnit = "noche_habitacion",
        IsActive = true,
        CreatedAt = DateTime.UtcNow.AddDays(-30),
        SearchName = TextNormalizer.NormalizeForCatalog(hotelName)
    };

    /// <summary>Un producto que NO es hotel (aéreo, excursión, "Otro"...), para las solapas.</summary>
    private static Rate OtherTypeRate(int id, string serviceType, string productName) => new()
    {
        Id = id,
        ServiceType = serviceType,
        ProductName = productName,
        NetCost = 700m,
        SalePrice = 780m,
        Currency = "USD",
        PriceUnit = "pasajero",
        IsActive = true,
        CreatedAt = DateTime.UtcNow.AddDays(-30),
        SearchName = TextNormalizer.NormalizeForCatalog(productName)
    };

    private static RateSupplierSale Sale(
        int rateId, int supplierId, string variantKey, string variantLabel, decimal netCost, DateTime soldAt)
        => new()
        {
            RateId = rateId,
            SupplierId = supplierId,
            LastSoldAt = soldAt,
            LastNetCost = netCost,
            LastTax = 0m,
            LastSalePrice = netCost + 20m,
            LastCurrency = "USD",
            LastPriceUnit = CatalogPriceUnits.NocheHabitacion,
            SalesCount = 1,
            VariantKey = variantKey,
            VariantLabel = variantLabel
        };
}
