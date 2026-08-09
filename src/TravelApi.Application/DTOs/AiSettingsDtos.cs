using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace TravelApi.Application.DTOs;

/// <summary>
/// Los tres estados de la foto de arriba de la pantalla (§15.5 de la spec firmada 2026-08-07).
/// Viajan como CODIGO (regla T-13): la frase la elige el front, el motor no manda texto armado.
/// </summary>
public static class AiSettingsStatusCodes
{
    /// <summary>No hay nada configurado. El sistema anda igual, sin las ayudas inteligentes.</summary>
    public const string NotConfigured = "sinConfigurar";

    /// <summary>Hay configuracion utilizable y la ultima prueba (si la hubo) anduvo.</summary>
    public const string Working = "funcionando";

    /// <summary>Hay configuracion, pero la ultima prueba guardada fallo.</summary>
    public const string LastTestFailed = "ultimaPruebaFallo";
}

/// <summary>
/// De donde sale la clave que se esta usando. Sirve para el caso "la puso el tecnico al instalar"
/// (§15.8): ahi la pantalla avisa que si el dueño pega una, manda la suya.
/// </summary>
public static class AiApiKeySources
{
    /// <summary>No hay clave en ningun lado.</summary>
    public const string None = "ninguna";

    /// <summary>La cargo alguien desde la pantalla (vive cifrada en la base).</summary>
    public const string Saved = "guardada";

    /// <summary>La dejo el tecnico al instalar (variables del servidor). Es el respaldo.</summary>
    public const string Server = "servidor";
}

/// <summary>
/// Los cinco resultados posibles de "Probar conexion" (§15.4). SIEMPRE viaja el codigo, NUNCA el
/// texto crudo del proveedor ni un numero de error (P-17 + gate de exposicion de datos).
/// </summary>
public static class AiConnectionTestCodes
{
    public const string Ok = "ok";
    public const string InvalidKey = "claveInvalida";
    public const string NoResponse = "noResponde";
    public const string InvalidAddress = "direccionInvalida";
    public const string ModelNotFound = "modeloInexistente";
}

/// <summary>
/// Lo que el motor cuenta sobre la configuracion de IA. <b>La clave NO esta aca y no puede estar</b>:
/// este objeto es lo unico que sale por la API (write-only key, M-28).
/// </summary>
public class AiSettingsDto
{
    /// <summary>Uno de <see cref="AiSettingsStatusCodes"/>.</summary>
    public string StatusCode { get; set; } = AiSettingsStatusCodes.NotConfigured;

    /// <summary>Codigo del proveedor elegido ("groq", "openai", ...). Nunca el numero interno.</summary>
    public string ProviderCode { get; set; } = string.Empty;

    /// <summary>Como se llama en la calle ("Groq"), para la linea de estado.</summary>
    public string ProviderDisplayName { get; set; } = string.Empty;

    /// <summary>Direccion efectiva (la guardada, o la recomendada del preset si no hay nada).</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Modelo efectivo (el guardado, o el recomendado del preset si no hay nada).</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Hay una clave utilizable (guardada por pantalla o dejada por el tecnico).</summary>
    public bool HasApiKey { get; set; }

    /// <summary>Uno de <see cref="AiApiKeySources"/>.</summary>
    public string ApiKeySource { get; set; } = AiApiKeySources.None;

    /// <summary>
    /// Los primeros 4 caracteres de la clave guardada ("gsk_"). Es lo UNICO mostrable de la clave.
    /// Null cuando la clave la puso el tecnico por el servidor (de esa no guardamos prefijo).
    /// </summary>
    public string? ApiKeyPrefix { get; set; }

    /// <summary>Uno de <see cref="AiConnectionTestCodes"/>, o null si todavia nadie probo.</summary>
    public string? LastTestCode { get; set; }

    /// <summary>Cuando fue la ultima prueba (UTC). Null si todavia nadie probo.</summary>
    public DateTime? LastTestAt { get; set; }

    /// <summary>Quien toco la configuracion por ultima vez (nombre visible).</summary>
    public string? UpdatedByUserName { get; set; }

    /// <summary>Cuando se toco por ultima vez (UTC). Null si nunca se guardo nada.</summary>
    public DateTime? UpdatedAt { get; set; }
}

/// <summary>
/// Lo que manda la pantalla al guardar. La clave viaja SOLO en esta direccion (entra y no sale).
/// </summary>
public class UpdateAiSettingsRequest
{
    /// <summary>Codigo del proveedor elegido ("groq", "openai", ..., "otra"). Obligatorio.</summary>
    [Required(ErrorMessage = "Elegí con cuál inteligencia artificial querés trabajar.")]
    [MaxLength(40)]
    public string ProviderCode { get; set; } = string.Empty;

    /// <summary>
    /// Direccion. Si viene vacia y el proveedor tiene recomendada, se usa la recomendada.
    /// Obligatoria cuando el proveedor es "Otra".
    /// </summary>
    [MaxLength(300)]
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Modelo. Si viene vacio y el proveedor tiene recomendado, se usa el recomendado.
    /// Obligatorio cuando el proveedor es "Otra".
    /// </summary>
    [MaxLength(150)]
    public string? Model { get; set; }

    /// <summary>
    /// La clave nueva. Si viene vacia se conserva la que ya estaba guardada (asi el dueño puede
    /// cambiar el modelo sin volver a pegar la clave). Si no hay ninguna guardada, es obligatoria.
    /// </summary>
    [MaxLength(500)]
    public string? ApiKey { get; set; }
}

/// <summary>
/// Lo que manda el boton "Probar conexion": lo que hay EN PANTALLA, este guardado o no (§15.4).
/// Si no trae clave nueva, se prueba con la que ya esta guardada.
/// </summary>
public class TestAiConnectionRequest
{
    [MaxLength(40)]
    public string? ProviderCode { get; set; }

    [MaxLength(300)]
    public string? BaseUrl { get; set; }

    [MaxLength(150)]
    public string? Model { get; set; }

    [MaxLength(500)]
    public string? ApiKey { get; set; }
}

/// <summary>
/// El resultado de la prueba: un codigo y cuanto tardo. Nada mas. Ni el texto del proveedor, ni el
/// numero de error, ni el nombre de ninguna pieza interna.
/// </summary>
public class AiConnectionTestResultDto
{
    /// <summary>Uno de <see cref="AiConnectionTestCodes"/>.</summary>
    public string ResultCode { get; set; } = AiConnectionTestCodes.NoResponse;

    /// <summary>Cuanto tardo, en milisegundos, para poder decir "contestó en 0,8 s".</summary>
    public int ElapsedMilliseconds { get; set; }
}

/// <summary>
/// Un proveedor de la lista, tal como lo ve la pantalla (M-32). La lista sale del motor.
/// </summary>
public class AiProviderPresetDto
{
    public string Code { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Tagline { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public bool IsRecommended { get; set; }

    /// <summary>Si es true, la pantalla abre "Ajustes avanzados" y exige direccion y modelo.</summary>
    public bool RequiresManualEndpoint { get; set; }
}

/// <summary>Respuesta del listado de proveedores. Un sobre para poder crecer sin romper el front.</summary>
public class AiProviderPresetsResponse
{
    public List<AiProviderPresetDto> Providers { get; set; } = new();
}
