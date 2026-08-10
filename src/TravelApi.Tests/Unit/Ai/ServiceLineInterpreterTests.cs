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
using TravelApi.Application.Ai;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Helpers;
using TravelApi.Infrastructure.Ai;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit.Ai;

/// <summary>
/// "La linea inteligente" (spec firmada 2026-08-07 §3 / M-20 a M-23 y M-27).
///
/// <para>Los tests corren sobre EF Core InMemory y con un modelo de mentira
/// (<see cref="FakeAiChatProvider"/>): NO se llama a ningun proveedor de verdad. Lo que se verifica
/// aca no es "que tan bien entiende el modelo" (eso no se puede testear en unitarios), sino que el
/// MOTOR haga bien su parte: descartar lo que no cierra, no filtrar costos, no mandar datos de
/// personas al proveedor, y no romper nunca cuando la inteligencia artificial falla.</para>
/// </summary>
public class ServiceLineInterpreterTests
{
    private const string Frase = "sheraton iguazu doble desayuno ola 48 usd del 12 al 15/9";

    // ============================================================
    // Camino feliz
    // ============================================================

    [Fact]
    public async Task Interpreta_la_frase_completa_con_producto_operador_habitacion_precio_y_fechas()
    {
        await using var db = BuildDb();
        SeedTarifario(db);
        await db.SaveChangesAsync();

        var interpreter = BuildInterpreter(db, ModelAnswer(
            producto: "Sheraton Iguazu",
            operador: "Ola Mayorista",
            habitacion: "doble",
            regimen: "desayuno",
            precio: 48,
            moneda: "USD",
            desde: "2026-09-12",
            hasta: "2026-09-15"));

        var result = await interpreter.InterpretAsync(Frase, "Hotel", CancellationToken.None);

        Assert.True(result.Interpreted);

        Assert.NotNull(result.Product);
        Assert.Equal("Sheraton Iguazu", result.Product!.Name);
        Assert.Equal(InterpretationConfidence.High, result.Product.Confidence);

        Assert.NotNull(result.Supplier);
        Assert.Equal("Ola Mayorista", result.Supplier!.Name);
        Assert.Equal(InterpretationConfidence.High, result.Supplier.Confidence);

        Assert.NotNull(result.Variant);
        Assert.Equal("Doble", result.Variant!.RoomType);
        Assert.Equal("Desayuno", result.Variant.MealPlan);
        Assert.Equal("Doble con desayuno", result.Variant.Label);

        Assert.NotNull(result.Price);
        Assert.Equal(48m, result.Price!.Amount);
        Assert.Equal("USD", result.Price.Currency);
        Assert.Equal(CatalogPriceUnits.NocheHabitacion, result.Price.PriceUnit);
        Assert.Equal("por noche", result.Price.PriceUnitLabel);

        Assert.NotNull(result.Dates);
        Assert.Equal(new DateTime(2026, 9, 12, 0, 0, 0, DateTimeKind.Utc), result.Dates!.From);
        Assert.Equal(new DateTime(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc), result.Dates.To);

        // Los parecidos salen del MISMO buscador de la ficha, con su forma de siempre.
        Assert.Single(result.ProductCandidates);
    }

    [Fact]
    public async Task Si_el_producto_no_esta_en_el_tarifario_no_lo_inventa_pero_conserva_el_resto()
    {
        await using var db = BuildDb();
        db.Suppliers.Add(BuildSupplier(1, "Julia Tours"));
        await db.SaveChangesAsync();

        var interpreter = BuildInterpreter(db, ModelAnswer(
            producto: "Amerian Posadas",
            operador: "Julia Tours",
            habitacion: "triple",
            regimen: "media pension",
            precio: 91000,
            moneda: "ARS"));

        var result = await interpreter.InterpretAsync(
            "hotel amerian posadas triple mp julia 91000 pesos por noche", "Hotel", CancellationToken.None);

        Assert.True(result.Interpreted);
        Assert.Null(result.Product);
        Assert.Empty(result.ProductCandidates);
        // El nombre viaja igual, para la ultima opcion "crear ..." de la lista (§3.4), y sale TAL COMO
        // lo escribio el vendedor (en minuscula, en este caso), no como lo redacto el modelo.
        Assert.Equal("amerian posadas", result.ProductSearchText);
        Assert.NotNull(result.Supplier);
        Assert.NotNull(result.Price);
    }

    // ============================================================
    // M-23 — degradacion: nunca un error, nunca nada tecnico
    // ============================================================

    [Fact]
    public async Task Sin_configuracion_de_IA_no_interpreta_y_ni_siquiera_llama_al_modelo()
    {
        await using var db = BuildDb();
        SeedTarifario(db);
        await db.SaveChangesAsync();

        var provider = new FakeAiChatProvider(AiChatResult.Success(ModelAnswer()));
        var interpreter = BuildInterpreter(db, provider, aiUsable: false);

        var result = await interpreter.InterpretAsync(Frase, "Hotel", CancellationToken.None);

        Assert.False(result.Interpreted);
        Assert.Empty(result.ProductCandidates);
        Assert.Null(result.Product);
        Assert.Null(result.Doubt);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task Con_el_proveedor_caido_no_interpreta_y_no_lanza()
    {
        await using var db = BuildDb();
        SeedTarifario(db);
        await db.SaveChangesAsync();

        var interpreter = BuildInterpreter(db, new FakeAiChatProvider(AiChatResult.Degraded("error de red")));

        var result = await interpreter.InterpretAsync(Frase, "Hotel", CancellationToken.None);

        Assert.False(result.Interpreted);
        Assert.Null(result.Price);
    }

    [Fact]
    public async Task Con_respuesta_ilegible_incluso_tras_el_reintento_no_interpreta()
    {
        await using var db = BuildDb();
        SeedTarifario(db);
        await db.SaveChangesAsync();

        // Dos respuestas seguidas que NO son el JSON pedido: el cerebro reintenta una vez y degrada.
        var provider = new FakeAiChatProvider(
            AiChatResult.Success("no soy json"),
            AiChatResult.Success("{ \"campoQueNoExiste\": 1 }"));
        var interpreter = BuildInterpreter(db, provider);

        var result = await interpreter.InterpretAsync(Frase, "Hotel", CancellationToken.None);

        Assert.False(result.Interpreted);
        Assert.Equal(2, provider.CallCount);
    }

    [Fact]
    public async Task Si_el_modelo_tarda_mas_de_la_cuenta_se_corta_y_no_interpreta()
    {
        await using var db = BuildDb();
        SeedTarifario(db);
        await db.SaveChangesAsync();

        // Un modelo que nunca contesta: el unico que lo frena es NUESTRO reloj.
        var interpreter = BuildInterpreter(db, new NeverAnsweringChatProvider(), timeoutSeconds: 1);

        var result = await interpreter.InterpretAsync(Frase, "Hotel", CancellationToken.None);

        Assert.False(result.Interpreted);
    }

    [Fact]
    public async Task Si_el_que_pregunta_se_va_la_cancelacion_se_propaga()
    {
        await using var db = BuildDb();
        SeedTarifario(db);
        await db.SaveChangesAsync();

        var interpreter = BuildInterpreter(db, new NeverAnsweringChatProvider());
        using var callerCancellation = new CancellationTokenSource();
        callerCancellation.CancelAfter(TimeSpan.FromMilliseconds(100));

        // Cerrar la ficha NO es "no entendi": es una cancelacion legitima y se propaga tal cual.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => interpreter.InterpretAsync(Frase, "Hotel", callerCancellation.Token));
    }

    // ============================================================
    // Bug de PROD (2026-08-1x): una excepcion del proveedor no puede ser un 500
    // ============================================================

    [Fact]
    public async Task Provider_que_explota_no_rompe_y_responde_no_interpretado()
    {
        await using var db = BuildDb();
        SeedTarifario(db);
        await db.SaveChangesAsync();

        var provider = new ThrowingChatProvider();
        var interpreter = BuildInterpreter(db, provider);

        var result = await interpreter.InterpretAsync(Frase, "Hotel", CancellationToken.None);

        Assert.False(result.Interpreted);
        Assert.Null(result.Product);
        Assert.Null(result.Doubt);
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task Provider_que_explota_cachea_el_negativo_y_la_segunda_llamada_inmediata_no_lo_martilla()
    {
        await using var db = BuildDb();
        SeedTarifario(db);
        await db.SaveChangesAsync();

        var provider = new ThrowingChatProvider();
        var cache = new ServiceLineInterpretationCache();
        var interpreter = BuildInterpreter(db, provider, cache: cache);

        var primero = await interpreter.InterpretAsync(Frase, "Hotel", CancellationToken.None);
        var segundo = await interpreter.InterpretAsync(Frase, "Hotel", CancellationToken.None);

        Assert.False(primero.Interpreted);
        Assert.False(segundo.Interpreted);
        // El negativo quedo cacheado (TTL corto): la segunda pregunta NO volvio a golpear al
        // proveedor caido. Antes del fix esto fallaba: CallCount daba 2.
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task Provider_que_explota_se_reintenta_pasado_el_ttl_corto_del_negativo()
    {
        await using var db = BuildDb();
        SeedTarifario(db);
        await db.SaveChangesAsync();

        var provider = new ThrowingChatProvider();
        // TTL de milisegundos SOLO para el test (constructor interno de ServiceLineInterpretationCache).
        var cache = new ServiceLineInterpretationCache(
            interpretedTtl: TimeSpan.FromMilliseconds(30), notInterpretedTtl: TimeSpan.FromMilliseconds(30));
        var interpreter = BuildInterpreter(db, provider, cache: cache);

        await interpreter.InterpretAsync(Frase, "Hotel", CancellationToken.None);
        await Task.Delay(200);
        await interpreter.InterpretAsync(Frase, "Hotel", CancellationToken.None);

        Assert.Equal(2, provider.CallCount);
    }

    [Fact]
    public async Task La_cancelacion_del_caller_se_propaga_sin_cachear_y_el_proximo_pedido_llama_de_nuevo()
    {
        await using var db = BuildDb();
        SeedTarifario(db);
        await db.SaveChangesAsync();

        var provider = new NeverAnsweringChatProvider();
        var cache = new ServiceLineInterpretationCache();

        using var callerCancellation = new CancellationTokenSource();
        callerCancellation.CancelAfter(TimeSpan.FromMilliseconds(100));
        var interpreter = BuildInterpreter(db, provider, cache: cache);

        // Cerrar la ficha (cancelacion legitima) se propaga, como siempre.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => interpreter.InterpretAsync(Frase, "Hotel", callerCancellation.Token));
        Assert.Equal(1, provider.CallCount);

        // Un pedido NUEVO, sin cancelar, tiene que volver a golpear al proveedor: la cancelacion NO
        // es un resultado "negativo" del modelo y no se cachea. Se corta con el reloj CORTO del
        // interprete (no con la cancelacion del caller) para no colgar el test.
        var interpreterConRelojCorto = BuildInterpreter(db, provider, cache: cache, timeoutSeconds: 1);
        var result = await interpreterConRelojCorto.InterpretAsync(Frase, "Hotel", CancellationToken.None);

        Assert.False(result.Interpreted);
        Assert.Equal(2, provider.CallCount);
    }

    // ============================================================
    // M-22 — una sola duda grande, y primero la de plata
    // ============================================================

    [Fact]
    public async Task Cuando_hay_varias_dudas_se_muestra_la_de_plata()
    {
        await using var db = BuildDb();
        SeedTarifario(db);
        await db.SaveChangesAsync();

        // La frase no aclara si "48" es por noche NI trae el año: dos dudas posibles.
        var interpreter = BuildInterpreter(db, ModelAnswer(
            producto: "Sheraton Iguazu",
            operador: "ola",
            habitacion: "doble",
            precio: 48,
            moneda: "USD",
            desde: "2026-09-12",
            hasta: "2026-09-15"));

        var result = await interpreter.InterpretAsync(
            "sheraton iguazu doble ola 48 usd del 12 al 15/9", "Hotel", CancellationToken.None);

        Assert.NotNull(result.Doubt);
        Assert.Equal(ServiceLineDoubtCodes.PricePerNight, result.Doubt!.Code);
        Assert.Equal(ServiceLineDoubtFields.Price, result.Doubt.Field);
        Assert.Equal("¿US$ 48 es el precio por noche?", result.Doubt.Question);
    }

    [Fact]
    public async Task Cuando_el_operador_se_reconocio_por_un_pedazo_del_nombre_se_pregunta()
    {
        await using var db = BuildDb();
        SeedTarifario(db);
        await db.SaveChangesAsync();

        // Con la unidad del precio escrita ("por noche") y el año escrito, la unica duda es el operador.
        var interpreter = BuildInterpreter(db, ModelAnswer(
            producto: "Sheraton Iguazu",
            operador: "ola",
            habitacion: "doble",
            precio: 48,
            moneda: "USD",
            desde: "2026-09-12",
            hasta: "2026-09-15"));

        var result = await interpreter.InterpretAsync(
            "sheraton iguazu doble ola 48 usd por noche del 12 al 15/9/2026", "Hotel", CancellationToken.None);

        Assert.NotNull(result.Doubt);
        Assert.Equal(ServiceLineDoubtCodes.AmbiguousSupplier, result.Doubt!.Code);
        // La pregunta la escribe el MOTOR de punta a punta: no cita lo que devolvio el modelo. El unico
        // texto variable es el nombre del operador, que sale de nuestra base.
        Assert.Equal("¿El operador es Ola Mayorista?", result.Doubt.Question);
    }

    [Fact]
    public async Task Si_la_frase_no_trae_el_año_se_pregunta_por_las_fechas()
    {
        await using var db = BuildDb();
        SeedTarifario(db);
        await db.SaveChangesAsync();

        // Traslado: no hay duda de "por noche", y el operador se nombra entero. Queda la del año.
        var interpreter = BuildInterpreter(db, ModelAnswer(
            producto: "Traslado Aeropuerto",
            operador: "Ola Mayorista",
            vehiculo: "van",
            desde: "2026-09-12"));

        var result = await interpreter.InterpretAsync(
            "traslado aeropuerto van ola mayorista el 12/9", "Traslado", CancellationToken.None);

        Assert.NotNull(result.Doubt);
        Assert.Equal(ServiceLineDoubtCodes.DatesYear, result.Doubt!.Code);
        Assert.Equal(ServiceLineDoubtFields.Dates, result.Doubt.Field);
        Assert.Contains("septiembre de 2026", result.Doubt.Question);
    }

    [Fact]
    public async Task Si_la_frase_aclara_todo_no_se_pregunta_nada()
    {
        await using var db = BuildDb();
        SeedTarifario(db);
        await db.SaveChangesAsync();

        var interpreter = BuildInterpreter(db, ModelAnswer(
            producto: "Sheraton Iguazu",
            operador: "Ola Mayorista",
            habitacion: "doble",
            regimen: "desayuno",
            precio: 48,
            moneda: "USD",
            desde: "2026-09-12",
            hasta: "2026-09-15"));

        var result = await interpreter.InterpretAsync(
            "sheraton iguazu doble desayuno ola mayorista 48 usd por noche del 12 al 15/9/2026",
            "Hotel", CancellationToken.None);

        Assert.Null(result.Doubt);
    }

    // ============================================================
    // M-21 — al proveedor NO se le manda un solo dato de una persona
    // ============================================================

    [Fact]
    public async Task El_texto_que_sale_hacia_el_proveedor_no_lleva_pasajeros_ni_clientes()
    {
        await using var db = BuildDb();
        SeedTarifario(db);

        // Una reserva con un cliente y un pasajero de carne y hueso, con documento incluido.
        db.Customers.Add(new Customer
        {
            Id = 1,
            FullName = "Rosalinda Peralta Ramos",
            Email = "rosalinda@ejemplo.com",
            DocumentNumber = "27345678",
        });
        db.Reservas.Add(new Reserva
        {
            Id = 1,
            NumeroReserva = "F-2026-9001",
            Name = "Viaje de Rosalinda Peralta Ramos",
            PayerId = 1,
        });
        db.Passengers.Add(new Passenger
        {
            Id = 1,
            ReservaId = 1,
            FullName = "Ceferino Bustamante",
            DocumentType = "DNI",
            DocumentNumber = "30111222",
        });
        await db.SaveChangesAsync();

        var provider = new FakeAiChatProvider(AiChatResult.Success(ModelAnswer(producto: "Sheraton Iguazu")));
        var interpreter = BuildInterpreter(db, provider);

        await interpreter.InterpretAsync(Frase, "Hotel", CancellationToken.None);

        var enviado = string.Join("\n", provider.LastRequest!.Messages.Select(message => message.Content));

        Assert.DoesNotContain("Rosalinda", enviado, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Ceferino", enviado, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rosalinda@ejemplo.com", enviado, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("27345678", enviado, StringComparison.Ordinal);
        Assert.DoesNotContain("30111222", enviado, StringComparison.Ordinal);
        Assert.DoesNotContain("F-2026-9001", enviado, StringComparison.Ordinal);

        // Lo que SI viaja: los nombres del tarifario y de los operadores de la agencia.
        Assert.Contains("Sheraton Iguazu", enviado, StringComparison.Ordinal);
        Assert.Contains("Ola Mayorista", enviado, StringComparison.Ordinal);
    }

    [Fact]
    public async Task El_texto_que_sale_hacia_el_proveedor_no_lleva_lo_tipeado_dentro_de_las_reservas()
    {
        // Regresion del agujero que encontro la review de seguridad: la lista de nombres de habitacion
        // salia de la memoria general (M-19), que incluye HotelBooking.RoomCategory — una casilla de
        // texto libre DENTRO de una reserva, donde termina escrito cualquier cosa.
        //
        // Desde la obra "prompt mas barato" (2026-08-10) esa lista NI SIQUIERA se arma (el modelo ya no
        // recibe nombres de habitacion/vehiculo, ver ServiceLinePromptBuilder.MaxVariantNames = 0): el
        // riesgo que este test cubria desaparecio por completo, no solo se filtro mejor. El test queda
        // como candado de que nunca vuelva a colarse un dato de una reserva ajena.
        await using var db = BuildDb();
        SeedTarifario(db);
        db.Reservas.Add(new Reserva { Id = 1, NumeroReserva = "F-2026-9002", Name = "Viaje" });
        db.HotelBookings.Add(new HotelBooking
        {
            Id = 1,
            ReservaId = 1,
            HotelName = "Sheraton Iguazu",
            RoomCategory = "Suite flia Peralta 27345678",
        });
        await db.SaveChangesAsync();

        var provider = new FakeAiChatProvider(AiChatResult.Success(ModelAnswer(producto: "Sheraton Iguazu")));
        var interpreter = BuildInterpreter(db, provider);

        await interpreter.InterpretAsync(Frase, "Hotel", CancellationToken.None);

        var enviado = string.Join("\n", provider.LastRequest!.Messages.Select(message => message.Content));

        Assert.DoesNotContain("Peralta", enviado, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("27345678", enviado, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Un_nombre_de_habitacion_tipeado_en_otra_reserva_no_se_precarga_en_la_ficha()
    {
        // El otro lado del mismo agujero: aunque el texto de la reserva ajena NO salga al proveedor, la
        // memoria de nombres podria devolverlo hacia ADENTRO ("suite" -> "Suite flia Peralta 27345678")
        // y terminar precargado en la ficha de otra reserva.
        await using var db = BuildDb();
        SeedTarifario(db);
        db.Reservas.Add(new Reserva { Id = 1, NumeroReserva = "F-2026-9003", Name = "Viaje" });
        db.HotelBookings.Add(new HotelBooking
        {
            Id = 1,
            ReservaId = 1,
            HotelName = "Sheraton Iguazu",
            RoomCategory = "Suite flia Peralta 27345678",
        });
        await db.SaveChangesAsync();

        var interpreter = BuildInterpreter(db, ModelAnswer(
            producto: "Sheraton Iguazu", habitacion: "doble", nombreFino: "suite"));

        var result = await interpreter.InterpretAsync(
            "sheraton iguazu doble ola 48 usd", "Hotel", CancellationToken.None);

        Assert.NotNull(result.Variant);
        Assert.Null(result.Variant!.RoomCategory);
        Assert.DoesNotContain("Peralta", result.Variant.Label, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task El_texto_que_sale_hacia_el_proveedor_no_lleva_importes_del_tarifario()
    {
        await using var db = BuildDb();
        SeedTarifario(db);
        await db.SaveChangesAsync();

        var provider = new FakeAiChatProvider(AiChatResult.Success(ModelAnswer(producto: "Sheraton Iguazu")));
        var interpreter = BuildInterpreter(db, provider);

        await interpreter.InterpretAsync(Frase, "Hotel", CancellationToken.None);

        var enviado = string.Join("\n", provider.LastRequest!.Messages.Select(message => message.Content));

        // 12345 es el costo cargado del producto sembrado: no tiene nada que hacer en el prompt.
        Assert.DoesNotContain("12345", enviado, StringComparison.Ordinal);
    }

    // ============================================================
    // M-27 / F-14 — sin permiso de costos, la respuesta no trae costos
    // ============================================================

    [Fact]
    public async Task Sin_permiso_de_ver_costos_la_respuesta_no_trae_el_precio()
    {
        await using var db = BuildDb();
        SeedTarifario(db);
        await db.SaveChangesAsync();

        var interpreter = BuildInterpreter(
            db,
            ModelAnswer(producto: "Sheraton Iguazu", precio: 48, moneda: "USD"),
            canSeeCost: false);

        var result = await interpreter.InterpretAsync(Frase, "Hotel", CancellationToken.None);

        Assert.Null(result.Price);
        // Y tampoco se pregunta por un precio que el vendedor no puede ver.
        Assert.Null(result.Doubt);
        // El resto de la interpretacion sigue sirviendo.
        Assert.NotNull(result.Product);
    }

    // ============================================================
    // El motor descarta lo incoherente: nunca burbujea a la pantalla
    // ============================================================

    [Theory]
    [InlineData(-48, "USD")]   // precio negativo
    [InlineData(0, "USD")]     // precio en cero
    [InlineData(48, "EUR")]    // moneda que el sistema no opera
    [InlineData(48, null)]     // numero sin moneda: no se adivina
    public async Task Un_precio_incoherente_se_descarta_y_el_casillero_queda_vacio(decimal precio, string? moneda)
    {
        await using var db = BuildDb();
        SeedTarifario(db);
        await db.SaveChangesAsync();

        var interpreter = BuildInterpreter(db, ModelAnswer(
            producto: "Sheraton Iguazu", precio: precio, moneda: moneda));

        var result = await interpreter.InterpretAsync(Frase, "Hotel", CancellationToken.None);

        Assert.Null(result.Price);
    }

    [Fact]
    public async Task Un_precio_que_nadie_escribio_en_la_frase_se_descarta()
    {
        await using var db = BuildDb();
        SeedTarifario(db);
        await db.SaveChangesAsync();

        // El modelo devuelve 999, un numero que NO esta en la frase: es invento.
        var interpreter = BuildInterpreter(db, ModelAnswer(
            producto: "Sheraton Iguazu", precio: 999, moneda: "USD"));

        var result = await interpreter.InterpretAsync(Frase, "Hotel", CancellationToken.None);

        Assert.Null(result.Price);
    }

    [Theory]
    [InlineData(1215)]   // sale de pegotear "…12 al 15…"
    [InlineData(812)]    // sale de pegotear "…48… 12…"
    [InlineData(48121)]  // sale de pegotear "…48 … 12 … 1(5)…"
    public async Task Un_precio_que_solo_existe_pegoteando_digitos_de_la_frase_se_descarta(decimal inventado)
    {
        // La frase tiene 48, 12, 15 y 9. Ninguno de estos numeros esta escrito; aparecian solo porque
        // el control viejo concatenaba TODOS los digitos ("4812159") y buscaba adentro.
        await using var db = BuildDb();
        SeedTarifario(db);
        await db.SaveChangesAsync();

        var interpreter = BuildInterpreter(db, ModelAnswer(
            producto: "Sheraton Iguazu", precio: inventado, moneda: "USD"));

        var result = await interpreter.InterpretAsync(Frase, "Hotel", CancellationToken.None);

        Assert.Null(result.Price);
    }

    [Fact]
    public async Task Un_precio_con_puntos_de_miles_se_reconoce()
    {
        await using var db = BuildDb();
        SeedTarifario(db);
        await db.SaveChangesAsync();

        var interpreter = BuildInterpreter(db, ModelAnswer(
            producto: "Sheraton Iguazu", precio: 91000, moneda: "ARS"));

        var result = await interpreter.InterpretAsync(
            "sheraton iguazu doble 91.000 pesos por noche", "Hotel", CancellationToken.None);

        Assert.NotNull(result.Price);
        Assert.Equal(91000m, result.Price!.Amount);
    }

    [Fact]
    public async Task Un_precio_que_el_modelo_manda_entre_comillas_se_entiende_igual()
    {
        // Los modelos mandan numeros como texto todo el tiempo. Antes eso hacia fallar el objeto
        // ENTERO por la lectura estricta y se perdia tambien el producto y las fechas.
        await using var db = BuildDb();
        SeedTarifario(db);
        await db.SaveChangesAsync();

        var answerConPrecioTexto = ModelAnswer(producto: "Sheraton Iguazu", moneda: "USD")
            .Replace("\"precio\": null", "\"precio\": \"48\"", StringComparison.Ordinal);

        var interpreter = BuildInterpreter(db, answerConPrecioTexto);

        var result = await interpreter.InterpretAsync(Frase, "Hotel", CancellationToken.None);

        Assert.NotNull(result.Price);
        Assert.Equal(48m, result.Price!.Amount);
        Assert.NotNull(result.Product);
    }

    [Fact]
    public async Task Si_el_precio_viene_como_texto_ilegible_se_pierde_solo_el_precio()
    {
        await using var db = BuildDb();
        SeedTarifario(db);
        await db.SaveChangesAsync();

        var answer = ModelAnswer(producto: "Sheraton Iguazu", moneda: "USD")
            .Replace("\"precio\": null", "\"precio\": \"a convenir\"", StringComparison.Ordinal);

        var interpreter = BuildInterpreter(db, answer);

        var result = await interpreter.InterpretAsync(Frase, "Hotel", CancellationToken.None);

        Assert.Null(result.Price);
        // El resto de la interpretacion se salva: no se tira todo por un campo.
        Assert.NotNull(result.Product);
    }

    [Fact]
    public async Task Fechas_al_reves_se_descartan_las_dos()
    {
        await using var db = BuildDb();
        SeedTarifario(db);
        await db.SaveChangesAsync();

        var interpreter = BuildInterpreter(db, ModelAnswer(
            producto: "Sheraton Iguazu", desde: "2026-09-15", hasta: "2026-09-12"));

        var result = await interpreter.InterpretAsync(Frase, "Hotel", CancellationToken.None);

        Assert.Null(result.Dates);
    }

    [Fact]
    public async Task Una_fecha_de_otro_siglo_se_descarta()
    {
        await using var db = BuildDb();
        SeedTarifario(db);
        await db.SaveChangesAsync();

        var interpreter = BuildInterpreter(db, ModelAnswer(
            producto: "Sheraton Iguazu", desde: "1912-09-12", hasta: "1912-09-15"));

        var result = await interpreter.InterpretAsync(Frase, "Hotel", CancellationToken.None);

        Assert.Null(result.Dates);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("null")]
    [InlineData("no especificado")]
    public async Task Una_habitacion_que_no_es_una_opcion_del_desplegable_se_descarta(string habitacion)
    {
        // Sin lista blanca, esto terminaba escrito en la ficha como "Doble Unknown con desayuno".
        await using var db = BuildDb();
        SeedTarifario(db);
        await db.SaveChangesAsync();

        var interpreter = BuildInterpreter(db, ModelAnswer(
            producto: "Sheraton Iguazu", habitacion: habitacion, regimen: "desayuno"));

        var result = await interpreter.InterpretAsync(Frase, "Hotel", CancellationToken.None);

        Assert.NotNull(result.Variant);
        Assert.Null(result.Variant!.RoomType);
        Assert.DoesNotContain(habitacion, result.Variant.Label, StringComparison.OrdinalIgnoreCase);
        // El regimen, que SI es una opcion valida, sigue viniendo.
        Assert.Equal("Desayuno", result.Variant.MealPlan);
    }

    [Fact]
    public async Task Un_regimen_inventado_se_descarta_y_no_aparece_en_la_etiqueta()
    {
        await using var db = BuildDb();
        SeedTarifario(db);
        await db.SaveChangesAsync();

        var interpreter = BuildInterpreter(db, ModelAnswer(
            producto: "Sheraton Iguazu", habitacion: "doble", regimen: "unknown"));

        var result = await interpreter.InterpretAsync(Frase, "Hotel", CancellationToken.None);

        Assert.NotNull(result.Variant);
        Assert.Null(result.Variant!.MealPlan);
        Assert.Equal("Doble", result.Variant.Label);
    }

    [Fact]
    public async Task Un_nombre_de_habitacion_que_el_vendedor_no_escribio_ni_esta_en_el_tarifario_se_descarta()
    {
        await using var db = BuildDb();
        SeedTarifario(db);
        await db.SaveChangesAsync();

        var interpreter = BuildInterpreter(db, ModelAnswer(
            producto: "Sheraton Iguazu",
            habitacion: "doble",
            nombreFino: "Habitacion premium con vista panoramica al rio y balcon privado"));

        var result = await interpreter.InterpretAsync(Frase, "Hotel", CancellationToken.None);

        Assert.NotNull(result.Variant);
        Assert.Null(result.Variant!.RoomCategory);
    }

    [Fact]
    public async Task Un_nombre_de_habitacion_que_esta_en_el_tarifario_si_se_precarga()
    {
        await using var db = BuildDb();
        SeedTarifario(db);
        await db.SaveChangesAsync();

        // "sup" es una abreviatura de "Superior", que SI esta cargada en el tarifario.
        var interpreter = BuildInterpreter(db, ModelAnswer(
            producto: "Sheraton Iguazu", habitacion: "doble", nombreFino: "sup"));

        var result = await interpreter.InterpretAsync(
            "sheraton iguazu doble sup ola 48 usd", "Hotel", CancellationToken.None);

        Assert.NotNull(result.Variant);
        Assert.Equal("Superior", result.Variant!.RoomCategory);
    }

    // ============================================================
    // Lo que se muestra del producto son PALABRAS DEL VENDEDOR
    // ============================================================

    [Fact]
    public async Task El_nombre_para_crear_el_producto_se_arma_con_las_palabras_del_vendedor()
    {
        await using var db = BuildDb();
        db.Suppliers.Add(BuildSupplier(1, "Julia Tours"));
        await db.SaveChangesAsync();

        // El modelo agrega texto de su cosecha; lo que se muestra sale de la frase, no de ahi.
        var interpreter = BuildInterpreter(db, ModelAnswer(
            producto: "Hotel Amerian Posadas (4 estrellas, excelente ubicacion segun mis datos)"));

        var result = await interpreter.InterpretAsync(
            "amerian posadas triple julia 91000 pesos", "Hotel", CancellationToken.None);

        Assert.Equal("amerian posadas", result.ProductSearchText);
    }

    [Fact]
    public async Task Un_producto_larguisimo_del_modelo_no_desborda_lo_que_se_muestra()
    {
        await using var db = BuildDb();
        SeedTarifario(db);
        await db.SaveChangesAsync();

        var interpreter = BuildInterpreter(db, ModelAnswer(
            producto: new string('x', 500) + " sheraton iguazu"));

        var result = await interpreter.InterpretAsync(Frase, "Hotel", CancellationToken.None);

        Assert.NotNull(result.ProductSearchText);
        Assert.True(result.ProductSearchText!.Length <= 80);
        Assert.Equal("sheraton iguazu", result.ProductSearchText);
    }

    [Fact]
    public async Task Un_producto_que_no_figura_en_la_frase_se_descarta()
    {
        await using var db = BuildDb();
        SeedTarifario(db);
        await db.SaveChangesAsync();

        // El modelo completa de memoria un hotel que nadie escribio.
        var interpreter = BuildInterpreter(db, ModelAnswer(producto: "Hilton Buenos Aires"));

        var result = await interpreter.InterpretAsync(Frase, "Hotel", CancellationToken.None);

        Assert.Null(result.Product);
        Assert.Null(result.ProductSearchText);
        Assert.Empty(result.ProductCandidates);
    }

    [Fact]
    public async Task Un_operador_que_no_existe_en_la_agencia_se_descarta()
    {
        await using var db = BuildDb();
        SeedTarifario(db);
        await db.SaveChangesAsync();

        var interpreter = BuildInterpreter(db, ModelAnswer(
            producto: "Sheraton Iguazu", operador: "iguazu"));

        var result = await interpreter.InterpretAsync(Frase, "Hotel", CancellationToken.None);

        Assert.Null(result.Supplier);
    }

    [Fact]
    public async Task Lo_que_el_modelo_marca_con_confianza_baja_se_descarta()
    {
        await using var db = BuildDb();
        SeedTarifario(db);
        await db.SaveChangesAsync();

        var answer = ModelAnswer(
            producto: "Sheraton Iguazu",
            operador: "Ola Mayorista",
            precio: 48,
            moneda: "USD",
            confianzaPrecio: "baja",
            confianzaOperador: "baja");

        var interpreter = BuildInterpreter(db, answer);

        var result = await interpreter.InterpretAsync(Frase, "Hotel", CancellationToken.None);

        Assert.Null(result.Price);
        Assert.Null(result.Supplier);
        Assert.NotNull(result.Product);
    }

    [Fact]
    public async Task Sin_texto_no_se_llama_al_modelo()
    {
        await using var db = BuildDb();
        SeedTarifario(db);
        await db.SaveChangesAsync();

        var provider = new FakeAiChatProvider(AiChatResult.Success(ModelAnswer()));
        var interpreter = BuildInterpreter(db, provider);

        var result = await interpreter.InterpretAsync("   ", "Hotel", CancellationToken.None);

        Assert.False(result.Interpreted);
        Assert.Equal(0, provider.CallCount);
    }

    // ============================================================
    // Obra "prompt mas barato" (2026-08-10) — cache de respuestas + prompt recortado
    // ============================================================

    [Fact]
    public async Task Dos_pedidos_identicos_usan_la_cache_y_llaman_al_modelo_una_sola_vez()
    {
        await using var db = BuildDb();
        SeedTarifario(db);
        await db.SaveChangesAsync();

        // UN solo resultado cargado a proposito: si el motor le preguntara al modelo dos veces, el
        // segundo pedido se queda sin script y el fake devuelve degradado (ver FakeAiChatProvider) —
        // el test fallaria por el segundo Assert, no por una excepcion rara.
        var provider = new FakeAiChatProvider(AiChatResult.Success(ModelAnswer(producto: "Sheraton Iguazu")));
        var cache = new ServiceLineInterpretationCache();
        var interpreter = BuildInterpreter(db, provider, cache: cache);

        var primero = await interpreter.InterpretAsync(Frase, "Hotel", CancellationToken.None);
        // Misma frase, pero con mayusculas y espacios distintos: la clave de cache normaliza
        // (TextNormalizer.NormalizeForMatch), asi que tiene que pegarle a la MISMA entrada.
        var segundo = await interpreter.InterpretAsync("  " + Frase.ToUpperInvariant() + "  ", "hotel", CancellationToken.None);

        Assert.Equal(1, provider.CallCount);
        Assert.True(primero.Interpreted);
        Assert.True(segundo.Interpreted);
        Assert.Equal(primero.Product?.Name, segundo.Product?.Name);
    }

    [Fact]
    public async Task Pedidos_con_clave_distinta_llaman_al_modelo_de_nuevo()
    {
        await using var db = BuildDb();
        SeedTarifario(db);
        await db.SaveChangesAsync();

        var provider = new FakeAiChatProvider(
            AiChatResult.Success(ModelAnswer(producto: "Sheraton Iguazu")),
            AiChatResult.Success(ModelAnswer(producto: "Sheraton Iguazu")));
        var cache = new ServiceLineInterpretationCache();
        var interpreter = BuildInterpreter(db, provider, cache: cache);

        await interpreter.InterpretAsync(Frase, "Hotel", CancellationToken.None);
        // Otra frase: es OTRA clave de cache (no una variacion cosmetica de la misma), asi que se le
        // vuelve a preguntar al modelo.
        await interpreter.InterpretAsync("sheraton iguazu otra vez distinto", "Hotel", CancellationToken.None);

        Assert.Equal(2, provider.CallCount);
    }

    [Fact]
    public async Task Pasado_el_ttl_la_cache_vence_y_se_vuelve_a_llamar_al_modelo()
    {
        await using var db = BuildDb();
        SeedTarifario(db);
        await db.SaveChangesAsync();

        var provider = new FakeAiChatProvider(
            AiChatResult.Success(ModelAnswer(producto: "Sheraton Iguazu")),
            AiChatResult.Success(ModelAnswer(producto: "Sheraton Iguazu")));
        // TTL de milisegundos SOLO para este test (constructor interno, ver ServiceLineInterpretationCache):
        // asi se prueba el vencimiento de verdad sin esperar los 10 minutos reales.
        var cache = new ServiceLineInterpretationCache(
            interpretedTtl: TimeSpan.FromMilliseconds(30), notInterpretedTtl: TimeSpan.FromMilliseconds(30));
        var interpreter = BuildInterpreter(db, provider, cache: cache);

        await interpreter.InterpretAsync(Frase, "Hotel", CancellationToken.None);
        await Task.Delay(200);
        await interpreter.InterpretAsync(Frase, "Hotel", CancellationToken.None);

        Assert.Equal(2, provider.CallCount);
    }

    [Fact]
    public async Task El_prompt_ya_no_pide_variante_ni_precio_pero_la_duda_sigue_viajando_en_la_respuesta()
    {
        // El pedido al modelo se achico a producto + fechas + operador (obra 2026-08-10). La DUDA
        // nunca fue un campo que se le pidiera al modelo — la arma el MOTOR en C# (PickSingleDoubt) a
        // partir de lo que YA extrajo, asi que este recorte no le toca nada a esa parte.
        await using var db = BuildDb();
        SeedTarifario(db);
        await db.SaveChangesAsync();

        // "ola" es un pedazo del nombre del operador (no el nombre entero): dispara la duda.
        var provider = new FakeAiChatProvider(AiChatResult.Success(ModelAnswer(
            producto: "Sheraton Iguazu", operador: "ola")));
        var interpreter = BuildInterpreter(db, provider);

        var result = await interpreter.InterpretAsync(Frase, "Hotel", CancellationToken.None);

        var enviado = string.Join("\n", provider.LastRequest!.Messages.Select(message => message.Content));

        // El prompt ya NO pide estos campos (comparado con las claves JSON exactas, no con la palabra
        // suelta: "precio" sigue apareciendo en la regla "el producto va sin el precio").
        Assert.DoesNotContain("\"habitacion\"", enviado, StringComparison.Ordinal);
        Assert.DoesNotContain("\"regimen\"", enviado, StringComparison.Ordinal);
        Assert.DoesNotContain("\"nombreFino\"", enviado, StringComparison.Ordinal);
        Assert.DoesNotContain("\"cabina\"", enviado, StringComparison.Ordinal);
        Assert.DoesNotContain("\"vehiculo\"", enviado, StringComparison.Ordinal);
        Assert.DoesNotContain("\"precio\"", enviado, StringComparison.Ordinal);
        Assert.DoesNotContain("\"moneda\"", enviado, StringComparison.Ordinal);
        Assert.DoesNotContain("\"variante\"", enviado, StringComparison.Ordinal);

        // Y sin embargo la duda sigue viajando en la respuesta, igual que siempre.
        Assert.NotNull(result.Doubt);
        Assert.Equal(ServiceLineDoubtCodes.AmbiguousSupplier, result.Doubt!.Code);
        Assert.Equal("¿El operador es Ola Mayorista?", result.Doubt.Question);
    }

    // ============================================================
    // C-1 (bloqueante, review 2026-08-1x) — la cache SOLO guarda lo que dijo el modelo
    // ============================================================

    [Fact]
    public async Task Un_cache_hit_no_filtra_costos_entre_quien_ve_y_quien_no_ve()
    {
        // Regresion del hallazgo de la review: ANTES la cache guardaba la RESPUESTA ENTERA, con los
        // costos ya enmascarados segun el permiso del PRIMERO que pregunto. Un vendedor sin permiso
        // heredaba el costo real que vio un administrador (fuga F-14). Ahora la cache solo guarda la
        // extraccion del modelo; el enmascarado se recalcula EN CADA PEDIDO con el permiso de quien
        // pregunta, cache hit o no.
        await using var db = BuildDb();
        SeedTarifario(db);
        db.RateSupplierSales.Add(new RateSupplierSale
        {
            Id = 1,
            RateId = 1,
            SupplierId = 1,
            LastSoldAt = DateTime.UtcNow.AddDays(-1),
            LastNetCost = 100m,
            LastSalePrice = 160m,
            LastCurrency = "USD",
            LastPriceUnit = "noche_habitacion",
            SalesCount = 1,
        });
        await db.SaveChangesAsync();

        var cache = new ServiceLineInterpretationCache();
        var provider = new FakeAiChatProvider(AiChatResult.Success(ModelAnswer(
            producto: "Sheraton Iguazu", precio: 48, moneda: "USD")));

        // Pregunta primero un vendedor CON permiso de ver costos: es el que "llena" la cache.
        var interpreterConCosto = BuildInterpreter(db, provider, canSeeCost: true, cache: cache);
        var resultConCosto = await interpreterConCosto.InterpretAsync(Frase, "Hotel", CancellationToken.None);

        // Pregunta EXACTAMENTE lo mismo un vendedor SIN permiso: hit de cache en la extraccion del
        // modelo, pero la respuesta se arma de nuevo con SU permiso (F-14).
        var interpreterSinCosto = BuildInterpreter(db, provider, canSeeCost: false, cache: cache);
        var resultSinCosto = await interpreterSinCosto.InterpretAsync(Frase, "Hotel", CancellationToken.None);

        // El hit de cache evito la segunda llamada al modelo (eso SI se cachea).
        Assert.Equal(1, provider.CallCount);

        // El que tiene permiso ve el precio y el costo de la ultima venta.
        Assert.NotNull(resultConCosto.Price);
        Assert.NotNull(resultConCosto.ProductCandidates.Single().LastSale?.NetCost);

        // El que NO tiene permiso no ve NINGUNO de los dos, pese al cache hit.
        Assert.Null(resultSinCosto.Price);
        Assert.Null(resultSinCosto.ProductCandidates.Single().LastSale?.NetCost);
    }

    [Fact]
    public async Task Un_producto_creado_entre_dos_pedidos_identicos_aparece_en_el_segundo()
    {
        // Regresion del hallazgo de la review: ANTES la cache congelaba tambien los candidatos del
        // tarifario por 10 minutos, asi que un producto recien cargado no aparecia -> se duplicaba
        // (P7). Ahora la BUSQUEDA de catalogo se recalcula siempre, aunque la extraccion venga de cache.
        await using var db = BuildDb();
        // Arranca SIN nada en el tarifario todavia.
        await db.SaveChangesAsync();

        var cache = new ServiceLineInterpretationCache();
        var provider = new FakeAiChatProvider(AiChatResult.Success(ModelAnswer(producto: "Sheraton Iguazu")));
        var interpreter = BuildInterpreter(db, provider, cache: cache);

        var primero = await interpreter.InterpretAsync(Frase, "Hotel", CancellationToken.None);
        Assert.Empty(primero.ProductCandidates);

        // Entre pedido y pedido, alguien carga el producto en el tarifario (misma reserva o no, da igual).
        SeedTarifario(db);
        await db.SaveChangesAsync();

        var segundo = await interpreter.InterpretAsync(Frase, "Hotel", CancellationToken.None);

        // La extraccion vino de cache (no se le volvio a preguntar al modelo)...
        Assert.Equal(1, provider.CallCount);
        // ...pero el candidato SI aparece: la busqueda al tarifario es SIEMPRE fresca.
        Assert.Single(segundo.ProductCandidates);
        Assert.Equal("Sheraton Iguazu", segundo.Product?.Name);
    }

    // ============================================================
    // Duda de PRODUCTO (aprobada por el dueño, 2026-08-10) — logica pura, sin modelo
    // ============================================================

    private static Rate BuildHotelRate(int id, string name, string city)
        => new()
        {
            Id = id,
            ServiceType = "Hotel",
            ProductName = name,
            HotelName = name,
            City = city,
            Currency = "USD",
            PriceUnit = "noche_habitacion",
            IsActive = true,
            SearchName = TextNormalizer.NormalizeForCatalog(name),
        };

    [Fact]
    public async Task Dos_candidatos_con_el_mismo_nombre_en_ciudades_distintas_disparan_duda_de_producto()
    {
        await using var db = BuildDb();
        db.Rates.Add(BuildHotelRate(1, "Panamericano", "Buenos Aires"));
        db.Rates.Add(BuildHotelRate(2, "Panamericano", "Bariloche"));
        await db.SaveChangesAsync();

        var interpreter = BuildInterpreter(db, ModelAnswer(producto: "Panamericano"));

        var result = await interpreter.InterpretAsync("panamericano", "Hotel", CancellationToken.None);

        Assert.Equal(2, result.ProductCandidates.Count);
        Assert.NotNull(result.Doubt);
        Assert.Equal(ServiceLineDoubtCodes.AmbiguousProduct, result.Doubt!.Code);
        Assert.Equal(ServiceLineDoubtFields.Product, result.Doubt.Field);
        Assert.Contains("Panamericano", result.Doubt.Question, StringComparison.Ordinal);
        Assert.Contains("Buenos Aires", result.Doubt.Question, StringComparison.Ordinal);
        Assert.Contains("Bariloche", result.Doubt.Question, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dos_filas_del_mismo_hotel_y_la_misma_ciudad_no_disparan_duda()
    {
        // El buscador YA las unifica en un solo candidato (dedupe por nombre+ciudad, ver RateService):
        // sin dos candidatos distintos, la duda de producto ni se evalua.
        await using var db = BuildDb();
        db.Rates.Add(BuildHotelRate(1, "Panamericano", "Buenos Aires"));
        db.Rates.Add(BuildHotelRate(2, "Panamericano", "Buenos Aires"));
        await db.SaveChangesAsync();

        var interpreter = BuildInterpreter(db, ModelAnswer(producto: "Panamericano"));

        var result = await interpreter.InterpretAsync("panamericano", "Hotel", CancellationToken.None);

        Assert.Single(result.ProductCandidates);
        Assert.Null(result.Doubt);
    }

    [Fact]
    public async Task Candidatos_con_nombres_distintos_no_disparan_duda_de_producto()
    {
        await using var db = BuildDb();
        db.Rates.Add(BuildHotelRate(1, "Panamericano", "Buenos Aires"));
        db.Rates.Add(BuildHotelRate(2, "Alvear Palace", "Buenos Aires"));
        await db.SaveChangesAsync();

        var interpreter = BuildInterpreter(db, ModelAnswer(producto: "panamericano alvear"));

        var result = await interpreter.InterpretAsync(
            "panamericano alvear ambos hoteles", "Hotel", CancellationToken.None);

        // Los dos nombres son suficientemente distintos: el buscador los devuelve juntos (cada uno
        // cubre una palabra de la busqueda) pero NO son "el mismo hotel en otro lugar".
        Assert.Equal(2, result.ProductCandidates.Count);
        Assert.Null(result.Doubt);
    }

    [Fact]
    public async Task La_duda_de_producto_gana_sobre_la_duda_de_operador()
    {
        await using var db = BuildDb();
        db.Suppliers.Add(BuildSupplier(1, "Ola Mayorista"));
        db.Rates.Add(BuildHotelRate(1, "Panamericano", "Buenos Aires"));
        db.Rates.Add(BuildHotelRate(2, "Panamericano", "Bariloche"));
        await db.SaveChangesAsync();

        // "ola" es un pedazo del nombre del operador: por si sola dispararia la duda de operador
        // (ver "Cuando_el_operador_se_reconocio_por_un_pedazo_del_nombre_se_pregunta"). Con el
        // producto TAMBIEN ambiguo, tiene que ganar el producto — es una sola duda a la vez.
        var interpreter = BuildInterpreter(db, ModelAnswer(producto: "Panamericano", operador: "ola"));

        var result = await interpreter.InterpretAsync("panamericano ola", "Hotel", CancellationToken.None);

        Assert.NotNull(result.Supplier);
        Assert.Equal(InterpretationConfidence.Medium, result.Supplier!.Confidence);
        Assert.NotNull(result.Doubt);
        Assert.Equal(ServiceLineDoubtCodes.AmbiguousProduct, result.Doubt!.Code);
    }

    [Fact]
    public async Task Dos_candidatos_de_tipos_distintos_nombran_el_tipo_en_la_pregunta()
    {
        // C-3c (review 2026-08-1x): el buscador cruza tipos, asi que "Panamericano" puede aparecer
        // como Hotel Y como Paquete. Sin nombrar el tipo, la pregunta sonaria como el mismo producto en
        // dos ciudades — pero ni siquiera es el mismo TIPO de servicio.
        await using var db = BuildDb();
        db.Rates.Add(BuildHotelRate(1, "Panamericano", "Buenos Aires"));
        db.Rates.Add(new Rate
        {
            Id = 2,
            ServiceType = "Paquete",
            ProductName = "Panamericano",
            // Paquete no tiene City: el subtitulo del buscador sale de Destination (BuildCatalogSubtitle).
            Destination = "Bariloche",
            Currency = "USD",
            PriceUnit = "pasajero",
            IsActive = true,
            SearchName = TextNormalizer.NormalizeForCatalog("Panamericano"),
        });
        await db.SaveChangesAsync();

        var interpreter = BuildInterpreter(db, ModelAnswer(producto: "Panamericano"));

        var result = await interpreter.InterpretAsync("panamericano", "Hotel", CancellationToken.None);

        Assert.Equal(2, result.ProductCandidates.Count);
        Assert.NotNull(result.Doubt);
        Assert.Equal(ServiceLineDoubtCodes.AmbiguousProduct, result.Doubt!.Code);
        Assert.Contains("Panamericano", result.Doubt.Question, StringComparison.Ordinal);
        Assert.Contains("Buenos Aires", result.Doubt.Question, StringComparison.Ordinal);
        Assert.Contains("Bariloche", result.Doubt.Question, StringComparison.Ordinal);
        // El tipo del PAQUETE (la segunda alternativa, el que no es del tipo de la solapa Hotel) se
        // nombra en criollo, minuscula, con articulo — igual que CatalogDisplayLabels.TheProduct.
        Assert.Contains("el paquete de Bariloche", result.Doubt.Question, StringComparison.Ordinal);
    }

    // ============================================================
    // Andamiaje
    // ============================================================

    private static AppDbContext BuildDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    /// <summary>Un hotel y un operador: lo minimo para que la linea tenga contra que reconocer.</summary>
    private static void SeedTarifario(AppDbContext db)
    {
        db.Suppliers.Add(BuildSupplier(1, "Ola Mayorista"));
        db.Rates.Add(new Rate
        {
            Id = 1,
            ServiceType = "Hotel",
            ProductName = "Sheraton Iguazu",
            HotelName = "Sheraton Iguazu",
            City = "Puerto Iguazu",
            // Nombre fino cargado EN EL TARIFARIO: es el unico origen valido para el prompt y para la
            // lista blanca del nombre de habitacion.
            RoomCategory = "Superior",
            NetCost = 12345m,
            SalePrice = 20000m,
            Currency = "USD",
            PriceUnit = "noche",
            IsActive = true,
            SearchName = TextNormalizer.NormalizeForCatalog("Sheraton Iguazu"),
        });
    }

    private static Supplier BuildSupplier(int id, string name)
        => new() { Id = id, Name = name, IsActive = true };

    /// <summary>El JSON que devolveria el modelo. Todo opcional, como el contrato real.</summary>
    private static string ModelAnswer(
        string? producto = null,
        string? operador = null,
        string? habitacion = null,
        string? regimen = null,
        string? nombreFino = null,
        string? cabina = null,
        string? vehiculo = null,
        decimal? precio = null,
        string? moneda = null,
        string? desde = null,
        string? hasta = null,
        string confianzaProducto = "alta",
        string confianzaOperador = "alta",
        string confianzaVariante = "alta",
        string confianzaPrecio = "alta",
        string confianzaFechas = "alta")
    {
        string Texto(string? value) => value is null ? "null" : $"\"{value}\"";
        var precioJson = precio.HasValue
            ? precio.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "null";

        return $$"""
            {
              "producto": {{Texto(producto)}},
              "operador": {{Texto(operador)}},
              "habitacion": {{Texto(habitacion)}},
              "regimen": {{Texto(regimen)}},
              "nombreFino": {{Texto(nombreFino)}},
              "cabina": {{Texto(cabina)}},
              "vehiculo": {{Texto(vehiculo)}},
              "precio": {{precioJson}},
              "moneda": {{Texto(moneda)}},
              "fechaDesde": {{Texto(desde)}},
              "fechaHasta": {{Texto(hasta)}},
              "confianza": {
                "producto": "{{confianzaProducto}}",
                "operador": "{{confianzaOperador}}",
                "variante": "{{confianzaVariante}}",
                "precio": "{{confianzaPrecio}}",
                "fechas": "{{confianzaFechas}}"
              }
            }
            """;
    }

    private static ServiceLineInterpreter BuildInterpreter(
        AppDbContext db,
        string modelAnswer,
        bool aiUsable = true,
        bool canSeeCost = true,
        int timeoutSeconds = 8,
        ServiceLineInterpretationCache? cache = null)
        => BuildInterpreter(
            db, new FakeAiChatProvider(AiChatResult.Success(modelAnswer)), aiUsable, canSeeCost, timeoutSeconds, cache);

    private static ServiceLineInterpreter BuildInterpreter(
        AppDbContext db,
        IAiChatProvider provider,
        bool aiUsable = true,
        bool canSeeCost = true,
        int timeoutSeconds = 8,
        ServiceLineInterpretationCache? cache = null)
    {
        const string userId = "vendedor-test";
        var accessor = BuildHttpContextAccessor(userId);
        var permissions = canSeeCost
            ? BuildPermissionResolver(userId, Permissions.CobranzasSeeCost)
            : BuildPermissionResolver(userId);

        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings
            .Setup(service => service.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings());

        var rateService = new RateService(
            db, NullLogger<RateService>.Instance, permissions, accessor, settings.Object);

        var assistant = new AiAssistantService(provider, NullLogger<AiAssistantService>.Instance);

        return new ServiceLineInterpreter(
            db,
            rateService,
            assistant,
            new FakeAiConnectionResolver(aiUsable),
            // Cada test arranca con una cache PROPIA (nueva) salvo que explicitamente quiera compartir
            // una entre dos pedidos — eso es justamente lo que prueban los tests de cache mas abajo.
            cache ?? new ServiceLineInterpretationCache(),
            new ServiceLineInterpretationOptions { TimeoutSeconds = timeoutSeconds },
            NullLogger<ServiceLineInterpreter>.Instance,
            accessor,
            permissions);
    }

    private static IHttpContextAccessor BuildHttpContextAccessor(string userId)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        return new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")),
            },
        };
    }

    private static IUserPermissionResolver BuildPermissionResolver(string userId, params string[] permissions)
    {
        var mock = new Mock<IUserPermissionResolver>();
        IReadOnlySet<string> set = new HashSet<string>(permissions);
        mock.Setup(resolver => resolver.GetPermissionsAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(set);
        return mock.Object;
    }
}

/// <summary>Resolvedor de mentira: solo contesta "hay IA utilizable" o "no hay".</summary>
internal sealed class FakeAiConnectionResolver : IAiConnectionResolver
{
    private readonly bool _usable;

    public FakeAiConnectionResolver(bool usable)
    {
        _usable = usable;
    }

    public Task<AiConnectionResolution?> ResolveAsync(CancellationToken cancellationToken)
    {
        if (!_usable) return Task.FromResult<AiConnectionResolution?>(null);

        var options = new AiConnectionOptions
        {
            BaseUrl = "https://ejemplo.invalido/v1",
            ApiKey = "clave-de-prueba",
            Model = "modelo-de-prueba",
        };
        return Task.FromResult<AiConnectionResolution?>(
            new AiConnectionResolution(options, AiConfigurationSource.Environment));
    }

    public Task<bool> IsUsableAsync(CancellationToken cancellationToken) => Task.FromResult(_usable);
}

/// <summary>
/// Un modelo que NUNCA contesta: se queda esperando hasta que lo cancelen. Sirve para verificar que
/// el reloj de la linea inteligente existe de verdad y que la cancelacion del que pregunta se respeta.
/// </summary>
internal sealed class NeverAnsweringChatProvider : IAiChatProvider
{
    /// <summary>Cantidad de veces que se llamo a <see cref="ChatAsync"/> (bug PROD 2026-08-1x: para
    /// probar que un pedido cancelado NO deja un negativo cacheado, el proximo pedido tiene que volver
    /// a golpear al proveedor).</summary>
    public int CallCount { get; private set; }

    public async Task<AiChatResult> ChatAsync(AiChatRequest request, CancellationToken cancellationToken)
    {
        CallCount++;
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return AiChatResult.Degraded("inalcanzable");
    }
}

/// <summary>
/// Un proveedor que EXPLOTA en vez de degradar solo (bug PROD 2026-08-1x: una clave rechazada, o
/// cualquier otra cosa que <see cref="OpenAiCompatibleChatProvider"/> no haya contemplado en sus
/// propios catch de red/HTTP). Sirve para probar que el motor degrada igual — nunca un 500 al vendedor.
/// </summary>
internal sealed class ThrowingChatProvider : IAiChatProvider
{
    /// <summary>Cantidad de veces que se llamo a <see cref="ChatAsync"/> — para probar que un negativo
    /// cacheado evita martillar al proveedor caido en cada tecleo.</summary>
    public int CallCount { get; private set; }

    public Task<AiChatResult> ChatAsync(AiChatRequest request, CancellationToken cancellationToken)
    {
        CallCount++;
        throw new InvalidOperationException("simulado: el proveedor exploto sin degradar solo");
    }
}
