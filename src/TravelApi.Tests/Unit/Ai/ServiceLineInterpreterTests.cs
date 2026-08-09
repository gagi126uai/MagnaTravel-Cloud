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
        // texto libre DENTRO de una reserva, donde termina escrito cualquier cosa. Ahora esa lista sale
        // SOLO del tarifario.
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
        // El nombre fino que SI esta cargado en el tarifario viaja normal.
        Assert.Contains("Superior", enviado, StringComparison.Ordinal);
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
        int timeoutSeconds = 8)
        => BuildInterpreter(
            db, new FakeAiChatProvider(AiChatResult.Success(modelAnswer)), aiUsable, canSeeCost, timeoutSeconds);

    private static ServiceLineInterpreter BuildInterpreter(
        AppDbContext db,
        IAiChatProvider provider,
        bool aiUsable = true,
        bool canSeeCost = true,
        int timeoutSeconds = 8)
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
    public async Task<AiChatResult> ChatAsync(AiChatRequest request, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return AiChatResult.Degraded("inalcanzable");
    }
}
