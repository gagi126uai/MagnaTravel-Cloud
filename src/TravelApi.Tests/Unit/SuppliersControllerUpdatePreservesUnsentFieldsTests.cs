using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// config-multas-por-proveedor-decisiones (2026-07-14, "mini-tanda futura" anotada por el reviewer): el PUT de
/// operador (<c>SuppliersController.UpdateSupplier</c>) NO debe pisar <c>PenaltyBehavior</c> ni
/// <c>TreasuryFxAssumedByOverride</c> cuando un cliente HTTP omite esos campos del JSON. Pero OJO — estos tests
/// van a nivel HTTP real (JSON crudo con <see cref="StringContent"/>, no el DTO tipado) porque la unica forma de
/// distinguir "el cliente omitio el campo" de "el cliente mando a proposito el valor default" es mirando si la
/// propiedad esta presente en el body. Un test que arme el DTO en C# no puede simular esa diferencia: los records
/// serializan SIEMPRE todas sus propiedades.
///
/// Cubre ambos lados de la moneda para no relajar la config ya cargada: el campo ausente debe preservar el valor
/// actual (bug que se corrige aca) Y el campo presente con el valor "vacio" (Unknown/null) debe poder resetear la
/// config a proposito (feature ya en produccion desde la ficha del operador, ver
/// <c>SupplierPenaltyBehaviorTests.UpdateSupplierAsync_PersistsPenaltyBehaviorChange</c> y
/// <c>SupplierTreasuryFxAssumedByOverrideTests.UpdateSupplierAsync_CanSetChangeAndClearOverride</c> a nivel
/// servicio).
/// </summary>
public class SuppliersControllerUpdatePreservesUnsentFieldsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public SuppliersControllerUpdatePreservesUnsentFieldsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task<Guid> SeedSupplierAsync(
        SupplierPenaltyBehavior penaltyBehavior = SupplierPenaltyBehavior.Unknown,
        TreasuryFxAssumedBy? treasuryFxAssumedByOverride = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var supplier = new Supplier
        {
            Name = "Operador test " + Guid.NewGuid().ToString("N")[..8],
            IsActive = true,
            PenaltyBehavior = penaltyBehavior,
            TreasuryFxAssumedByOverride = treasuryFxAssumedByOverride,
        };
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync();
        return supplier.PublicId;
    }

    private async Task<Supplier> ReloadSupplierAsync(Guid publicId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Suppliers.AsNoTracking().SingleAsync(s => s.PublicId == publicId);
    }

    private static StringContent JsonBody(string rawJson) => new(rawJson, Encoding.UTF8, "application/json");

    [Fact]
    public async Task PUT_SinPenaltyBehaviorEnElBody_PreservaElValorYaCargado()
    {
        var publicId = await SeedSupplierAsync(penaltyBehavior: SupplierPenaltyBehavior.RarelyCharges);

        var client = _factory.CreateClient();
        // El body NO menciona "penaltyBehavior" en absoluto (simula un cliente HTTP que solo quiere
        // cambiar el telefono y no sabe que este campo existe).
        var resp = await client.PutAsync(
            $"/api/suppliers/{publicId}",
            JsonBody("{ \"name\": \"Operador editado\", \"phone\": \"11-2222-3333\" }"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var reloaded = await ReloadSupplierAsync(publicId);
        Assert.Equal(SupplierPenaltyBehavior.RarelyCharges, reloaded.PenaltyBehavior);
    }

    [Fact]
    public async Task PUT_ConPenaltyBehaviorUnknownExplicito_ReseteaAProposito()
    {
        // Caso espejo del anterior: la ficha del operador SI permite volver a "no se sabe" a proposito
        // (opcion real del desplegable). Si el fix tratara CUALQUIER Unknown como "campo ausente", este
        // reset dejaria de funcionar en produccion.
        var publicId = await SeedSupplierAsync(penaltyBehavior: SupplierPenaltyBehavior.UsuallyCharges);

        var client = _factory.CreateClient();
        var resp = await client.PutAsync(
            $"/api/suppliers/{publicId}",
            JsonBody("{ \"name\": \"Operador editado\", \"penaltyBehavior\": 0 }"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var reloaded = await ReloadSupplierAsync(publicId);
        Assert.Equal(SupplierPenaltyBehavior.Unknown, reloaded.PenaltyBehavior);
    }

    [Fact]
    public async Task PUT_ConPenaltyBehaviorPresente_ActualizaAlValorNuevo()
    {
        var publicId = await SeedSupplierAsync(penaltyBehavior: SupplierPenaltyBehavior.Unknown);

        var client = _factory.CreateClient();
        var resp = await client.PutAsync(
            $"/api/suppliers/{publicId}",
            JsonBody("{ \"name\": \"Operador editado\", \"penaltyBehavior\": 2 }"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var reloaded = await ReloadSupplierAsync(publicId);
        Assert.Equal(SupplierPenaltyBehavior.UsuallyCharges, reloaded.PenaltyBehavior);
    }

    [Fact]
    public async Task PUT_SinTreasuryFxAssumedByOverrideEnElBody_PreservaElValorYaCargado()
    {
        var publicId = await SeedSupplierAsync(treasuryFxAssumedByOverride: TreasuryFxAssumedBy.Agency);

        var client = _factory.CreateClient();
        var resp = await client.PutAsync(
            $"/api/suppliers/{publicId}",
            JsonBody("{ \"name\": \"Operador editado\" }"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var reloaded = await ReloadSupplierAsync(publicId);
        Assert.Equal(TreasuryFxAssumedBy.Agency, reloaded.TreasuryFxAssumedByOverride);
    }

    [Fact]
    public async Task PUT_ConTreasuryFxAssumedByOverrideNullExplicito_LimpiaLaExcepcionAProposito()
    {
        // Caso espejo: la ficha permite volver a "como la config general" (null a proposito) desde el
        // select. Si el fix tratara CUALQUIER null como "campo ausente", limpiar la excepcion dejaria de
        // funcionar en produccion.
        var publicId = await SeedSupplierAsync(treasuryFxAssumedByOverride: TreasuryFxAssumedBy.Client);

        var client = _factory.CreateClient();
        var resp = await client.PutAsync(
            $"/api/suppliers/{publicId}",
            JsonBody("{ \"name\": \"Operador editado\", \"treasuryFxAssumedByOverride\": null }"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var reloaded = await ReloadSupplierAsync(publicId);
        Assert.Null(reloaded.TreasuryFxAssumedByOverride);
    }

    [Fact]
    public async Task PUT_ConTreasuryFxAssumedByOverridePresente_ActualizaAlValorNuevo()
    {
        var publicId = await SeedSupplierAsync(treasuryFxAssumedByOverride: null);

        var client = _factory.CreateClient();
        var resp = await client.PutAsync(
            $"/api/suppliers/{publicId}",
            JsonBody("{ \"name\": \"Operador editado\", \"treasuryFxAssumedByOverride\": 1 }"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var reloaded = await ReloadSupplierAsync(publicId);
        Assert.Equal(TreasuryFxAssumedBy.Agency, reloaded.TreasuryFxAssumedByOverride);
    }

    [Fact]
    public async Task PUT_SinName_Devuelve400EnVezDe500()
    {
        // Fix T-1 (review 2026-08-05): al pasar el body por JsonElement en vez del binder tipado de siempre,
        // el PUT dejo de aprovechar la validacion automatica de ASP.NET Core para el constructor-parametro
        // "name" (sin default): un PUT sin ese campo llegaba con Name null hasta el SaveChanges, y la columna
        // NOT NULL de la base explotaba con un DbUpdateException -> 500 crudo. El guard de
        // UpdateSupplierAsync (mismo que ya tenia CreateSupplierAsync) lo corta antes, con un 400 de negocio.
        var publicId = await SeedSupplierAsync();

        var client = _factory.CreateClient();
        var resp = await client.PutAsync(
            $"/api/suppliers/{publicId}",
            JsonBody("{ \"phone\": \"11-2222-3333\" }"));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var content = await resp.Content.ReadAsStringAsync();
        Assert.Contains("nombre", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PUT_ConNumeroMandadoComoStringJson_SigueEntrando()
    {
        // Fix T-8 (review 2026-08-05): las opciones de deserializacion manual solo tenian
        // PropertyNameCaseInsensitive, mas estrictas que el binder viejo de ASP.NET Core (que usa
        // JsonSerializerDefaults.Web, con AllowReadingFromString incluido). Un cliente que manda un numero
        // como string JSON (patron comun en forms HTML que no tipan bien el payload) dejaba de andar en
        // silencio. Con el fix (JsonSerializerDefaults.Web) vuelve a entrar igual que con el binder tipado.
        var publicId = await SeedSupplierAsync();

        var client = _factory.CreateClient();
        var resp = await client.PutAsync(
            $"/api/suppliers/{publicId}",
            JsonBody("{ \"name\": \"Operador editado\", \"defaultPaymentTermDays\": \"30\" }"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var reloaded = await ReloadSupplierAsync(publicId);
        Assert.Equal(30, reloaded.DefaultPaymentTermDays);
    }

    // Fix hallazgo 4 (review 2026-08-05): el nombre/comentario original de este test sugeria que el catch de
    // JsonException del controller era el que devolvia el 400 para CUALQUIER body invalido. Eso NO es asi: un
    // JSON con sintaxis rota (como "{ \"name\": ") ni siquiera llega a la accion — lo rechaza el INPUT
    // FORMATTER de ASP.NET Core (el mismo binder automatico que arma el JsonElement de [FromBody]) ANTES de
    // ejecutar UpdateSupplier, con su propio 400 automatico. El catch DE NUESTRO codigo solo entra en juego con
    // un body que es JSON VALIDO pero no matchea el DTO esperado (el caso mas simple: el literal "null", que
    // Deserialize<T> interpreta como "sin objeto" y dispara nuestro throw explicito). Se dejan los DOS casos
    // por separado para que cada uno pruebe lo que realmente prueba.

    [Fact]
    public async Task PUT_ConSintaxisJsonRota_ElInputFormatterDevuelve400SinExponerElErrorCrudo()
    {
        var publicId = await SeedSupplierAsync();

        var client = _factory.CreateClient();
        // JSON con sintaxis invalida: nunca llega a DeserializeSupplierUpsertRequest. El 400 lo arma el
        // pipeline de ASP.NET Core (input formatter) al intentar bindear el JsonElement de [FromBody].
        var resp = await client.PutAsync(
            $"/api/suppliers/{publicId}",
            JsonBody("{ \"name\": "));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var content = await resp.Content.ReadAsStringAsync();
        // Gate de exposicion de datos tecnicos: el mensaje al cliente no debe traer texto de la excepcion
        // de System.Text.Json (rutas de propiedad, tipos internos, numeros de linea/columna).
        Assert.DoesNotContain("JsonException", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("System.Text.Json", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PUT_ConBodyJsonValidoPeroSinObjeto_NuestroCatchDevuelve400SinExponerElErrorCrudo()
    {
        var publicId = await SeedSupplierAsync();

        var client = _factory.CreateClient();
        // "null" es JSON SINTACTICAMENTE VALIDO: llega hasta DeserializeSupplierUpsertRequest, que devuelve
        // null y dispara nuestro throw explicito -> ESTE es el catch de JsonException que escribimos nosotros.
        var resp = await client.PutAsync(
            $"/api/suppliers/{publicId}",
            JsonBody("null"));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var content = await resp.Content.ReadAsStringAsync();
        Assert.DoesNotContain("JsonException", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("System.Text.Json", content, StringComparison.OrdinalIgnoreCase);
    }
}
