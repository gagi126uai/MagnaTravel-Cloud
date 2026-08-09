using System;
using System.Collections.Generic;
using System.Text;
using TravelApi.Application.Ai;

namespace TravelApi.Infrastructure.Ai;

/// <summary>
/// Lo que la agencia le presta al modelo para que entienda la frase (M-21).
///
/// <para><b>Regla de privacidad, y es dura</b>: aca adentro entran SOLO nombres del tarifario y de
/// los operadores de la agencia. Jamas un pasajero, un cliente, un documento, un telefono, un mail,
/// ni un importe de otra reserva. La frase que escribio el vendedor sale del servidor hacia un
/// proveedor de afuera: todo lo que se le sume viaja con ella.</para>
/// </summary>
public sealed record ServiceLineCatalogContext(
    IReadOnlyList<string> ProductNames,
    IReadOnlyList<string> SupplierNames,
    IReadOnlyList<string> VariantNames)
{
    public static ServiceLineCatalogContext Empty { get; } = new(
        Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
}

/// <summary>
/// Arma el pedido que se le manda al modelo: las instrucciones + el contexto acotado de la agencia +
/// la frase del vendedor.
///
/// <para><b>Por que es una clase aparte y PURA</b>: para poder mirar con un test exactamente que
/// texto sale del servidor. La regla "nunca datos de pasajeros ni clientes" no se puede verificar
/// leyendo un servicio de 400 lineas; se verifica leyendo lo que este metodo devuelve.</para>
/// </summary>
public static class ServiceLinePromptBuilder
{
    /// <summary>Tope de listas dentro del prompt. Mas que esto no mejora la respuesta y encarece cada llamada.</summary>
    public const int MaxProductNames = 25;
    public const int MaxSupplierNames = 60;
    public const int MaxVariantNames = 15;

    /// <summary>
    /// Construye el pedido de UN turno: un mensaje de sistema con las reglas y el contexto, y un
    /// mensaje de usuario con la frase cruda.
    /// </summary>
    public static AiChatRequest Build(
        string freeText,
        string serviceType,
        ServiceLineCatalogContext context,
        DateTime today)
    {
        var instructions = BuildInstructions(serviceType, context, today);

        var messages = new List<AiChatMessage>
        {
            AiChatMessage.System(instructions),
            AiChatMessage.User(freeText),
        };

        var options = new AiProviderOptions
        {
            // Respuesta chica: el JSON pedido entra de sobra. Un tope bajo tambien es un freno de
            // gasto si el modelo se pone verborragico.
            MaxTokens = 400,
            // Temperatura en cero: esto es extraccion de datos, no redaccion. Queremos que la misma
            // frase de siempre el mismo resultado.
            Temperature = 0,
            RequestJsonObject = true,
        };

        return new AiChatRequest(messages, options);
    }

    private static string BuildInstructions(
        string serviceType,
        ServiceLineCatalogContext context,
        DateTime today)
    {
        var builder = new StringBuilder();

        builder.AppendLine(
            "Sos un asistente que EXTRAE datos de una frase escrita por un vendedor de una agencia de viajes.");
        builder.AppendLine(
            "Tu unica salida es un objeto JSON. No expliques nada, no agregues texto fuera del JSON.");
        builder.AppendLine();

        builder.AppendLine($"Tipo de servicio que se esta cargando: {serviceType}.");
        builder.AppendLine($"Fecha de hoy: {today:yyyy-MM-dd}.");
        builder.AppendLine();

        builder.AppendLine("Formato EXACTO de la respuesta (todas las claves, usa null donde no haya dato):");
        builder.AppendLine("""
            {
              "producto": null,
              "operador": null,
              "habitacion": null,
              "regimen": null,
              "nombreFino": null,
              "cabina": null,
              "vehiculo": null,
              "precio": null,
              "moneda": null,
              "fechaDesde": null,
              "fechaHasta": null,
              "confianza": {
                "producto": "alta",
                "operador": "alta",
                "variante": "alta",
                "precio": "alta",
                "fechas": "alta"
              }
            }
            """);
        builder.AppendLine();

        builder.AppendLine("Reglas:");
        builder.AppendLine("- Si un dato no esta en la frase, devolve null. NUNCA lo inventes ni lo completes por tu cuenta.");
        builder.AppendLine("- \"producto\" es el nombre del hotel / vuelo / paquete, sin el operador, sin el precio y sin las fechas.");
        builder.AppendLine("- \"operador\" es el mayorista, escrito como aparece en la frase.");
        builder.AppendLine("- \"precio\" es solo el numero, sin puntos de miles ni simbolos. \"moneda\" es \"ARS\" o \"USD\".");
        builder.AppendLine("- Las fechas van en formato AAAA-MM-DD. Si la frase no dice el año, usa el año que haga que la fecha sea la mas proxima a hoy sin quedar en el pasado.");
        builder.AppendLine("- \"confianza\" vale \"alta\", \"media\" o \"baja\" para cada grupo de datos. Usa \"baja\" si estas adivinando.");
        builder.AppendLine("- Respeta la ortografia de las listas de abajo cuando la frase claramente se refiere a uno de esos nombres.");
        builder.AppendLine();

        AppendList(builder, "Productos que esta agencia ya tiene cargados de este tipo", context.ProductNames, MaxProductNames);
        AppendList(builder, "Operadores de esta agencia", context.SupplierNames, MaxSupplierNames);
        AppendList(builder, "Nombres de habitacion/vehiculo que esta agencia ya usa", context.VariantNames, MaxVariantNames);

        return builder.ToString();
    }

    /// <summary>Escribe una lista acotada; si esta vacia, no escribe el titulo (no gastamos tokens en la nada).</summary>
    private static void AppendList(StringBuilder builder, string title, IReadOnlyList<string> values, int max)
    {
        if (values.Count == 0)
        {
            return;
        }

        builder.AppendLine($"{title}:");
        var shown = Math.Min(values.Count, max);
        for (var index = 0; index < shown; index++)
        {
            builder.AppendLine($"- {values[index]}");
        }

        builder.AppendLine();
    }
}
