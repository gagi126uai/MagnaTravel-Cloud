using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Http;

/// <summary>
/// Regresion 2026-06-06 (bug real reportado por Gaston probando en su servidor): los atributos de
/// validacion de <see cref="NewCatalogProductRequest"/> estaban como <c>[property: Required]</c> en
/// un record. En records con constructor primario, ASP.NET exige que la metadata de validacion vaya
/// en el PARAMETRO (sin "property:"); con [property:] el pipeline MVC tira InvalidOperationException
/// ("validation metadata must be associated with the constructor parameter") al validar el body
/// -> 500 en runtime en cualquier POST de create que mande <c>newCatalogProduct</c>.
///
/// <para>NINGUN test lo atrapo porque los tests del catalogo (BookingServiceCatalogTests) llaman al
/// service DIRECTO (saltean el model binding/validation de MVC) y los tests HTTP existentes no
/// mandaban <c>newCatalogProduct</c>. Por eso estos tests van por HTTP real (CustomWebApplicationFactory,
/// host completo + InMemory): la unica capa que ejecuta la validacion del record es el pipeline MVC.</para>
///
/// <para>Lo que se pinea NO es el resultado de negocio del POST sino que el MODEL VALIDATION del
/// pipeline no reviente: con el bug, la respuesta era 500 ANTES de llegar a la action (la excepcion
/// la captura GlobalExceptionHandler via UseExceptionHandler). El namespace NO contiene "Integration"
/// a proposito para que estos tests entren en la suite unit (filtro FullyQualifiedName!~Integration).</para>
/// </summary>
public class HotelBookingsControllerNewCatalogProductValidationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public HotelBookingsControllerNewCatalogProductValidationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// Prende el flag <c>EnableCatalogFindOrCreate</c> y siembra una Reserva + Supplier minimos para
    /// que el POST (como Admin default, que bypassea permisos y ownership) pueda llegar hasta el
    /// service. Identifiers nuevos por test: la BD InMemory se comparte entre tests de la clase.
    /// </summary>
    private async Task<(Guid ReservaPublicId, Guid SupplierPublicId)> SeedReservaWithCatalogFlagOnAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var settingsService = scope.ServiceProvider.GetRequiredService<IOperationalFinanceSettingsService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var settings = await settingsService.GetEntityAsync(CancellationToken.None);
        settings.EnableCatalogFindOrCreate = true;

        var reserva = new Reserva
        {
            PublicId = Guid.NewGuid(),
            Name = "Reserva catalogo " + Guid.NewGuid().ToString("N")[..6],
            NumeroReserva = "F-CAT-" + Guid.NewGuid().ToString("N")[..6],
            ResponsibleUserId = "test-user",
            // Confirmed: no fuerza "Solicitado" ni bloquea el alta de servicios.
            Status = EstadoReserva.Confirmed
        };
        var supplier = new Supplier
        {
            PublicId = Guid.NewGuid(),
            Name = "Operador Catalogo " + Guid.NewGuid().ToString("N")[..6]
        };
        db.Reservas.Add(reserva);
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync();

        return (reserva.PublicId, supplier.PublicId);
    }

    /// <summary>
    /// Body completo del create de hotel en modo "producto nuevo": sin RateId (mutuamente excluyente
    /// con newCatalogProduct), con Currency (obligatoria con el flag ON) y el sub-objeto
    /// newCatalogProduct, que es exactamente lo que disparaba el 500.
    /// </summary>
    private static CreateHotelRequest BuildCreateRequestWithNewCatalogProduct(
        Guid supplierPublicId, string productName)
    {
        return new CreateHotelRequest(
            SupplierId: supplierPublicId.ToString(),
            HotelName: "Hotel Nuevo Catalogo",
            StarRating: 4,
            City: "Bariloche",
            Country: "Argentina",
            CheckIn: DateTime.UtcNow.Date.AddDays(10),
            CheckOut: DateTime.UtcNow.Date.AddDays(13),
            RoomType: "Doble",
            MealPlan: "Desayuno",
            Adults: 2,
            Children: 0,
            Rooms: 1,
            ConfirmationNumber: null,
            NetCost: 500m,
            SalePrice: 800m,
            Commission: 300m,
            Notes: null,
            Currency: "ARS",
            NewCatalogProduct: new NewCatalogProductRequest(
                Name: productName,
                City: "Bariloche",
                SupplierPublicId: supplierPublicId.ToString()));
    }

    [Fact]
    public async Task POST_Hotel_WithNewCatalogProduct_ModelValidationDoesNotBlowUpThePipeline()
    {
        var (reservaPublicId, supplierPublicId) = await SeedReservaWithCatalogFlagOnAsync();

        // Admin default del TestAuthHandler (sin headers): bypass de permisos y ownership,
        // para que lo unico bajo prueba sea el pipeline de model binding/validation + el create.
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/api/reservas/{reservaPublicId}/hotels",
            BuildCreateRequestWithNewCatalogProduct(supplierPublicId, "Hotel Pinado " + Guid.NewGuid().ToString("N")[..6]));

        // ASSERT CLAVE (el pin de la regresion): NO 500. Con [property: Required] en el record,
        // la validacion del body explotaba con InvalidOperationException antes de la action y el
        // GlobalExceptionHandler devolvia 500. El codigo de negocio concreto (200/400/404 segun el
        // seed) es secundario; si esto vuelve a dar 500, se rompio la asociacion de los atributos
        // de validacion con los parametros del constructor del record.
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);

        // Con el seed completo (reserva + supplier + flag ON + currency) el create debe salir bien.
        // Si una regla de negocio futura legitima cambia esto a 400/404, relajar SOLO este assert;
        // el NotEqual(500) de arriba es el que no se negocia.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task POST_Hotel_WithNewCatalogProduct_EmptyName_Returns400WithValidationMessage_Not500()
    {
        var (reservaPublicId, supplierPublicId) = await SeedReservaWithCatalogFlagOnAsync();

        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/api/reservas/{reservaPublicId}/hotels",
            BuildCreateRequestWithNewCatalogProduct(supplierPublicId, productName: ""));

        // Pin complementario: los atributos quedaron asociados al PARAMETRO del constructor y por
        // eso SI validan. [Required] sobre Name vacio -> ModelState invalido -> 400 automatico de
        // [ApiController] (ValidationProblemDetails), nunca 500.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // El error tiene que nombrar al campo (key "NewCatalogProduct.Name" en el problem details).
        // Si la validacion del record dejara de ejecutarse (atributos sueltos en properties), este
        // request llegaria al service y fallaria por otro mensaje, o peor, con 500.
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("NewCatalogProduct.Name", body);
    }
}
