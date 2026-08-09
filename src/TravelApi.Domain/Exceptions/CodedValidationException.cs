using System.ComponentModel.DataAnnotations;

namespace TravelApi.Domain.Exceptions;

/// <summary>
/// Un rechazo de validacion que ademas del texto en criollo lleva un CODIGO estable.
///
/// <para><b>Por que existe</b> (T-13: las decisiones viajan por codigo, no por texto): hasta ahora la
/// pantalla tenia que adivinar de que se quejaba el servidor mirando como empezaba la frase
/// (<c>mensaje.startsWith("Pegá la clave")</c>). Eso se rompe solo: alcanza con mejorar la
/// redaccion para que la pantalla deje de reaccionar. Con el codigo, el texto se puede reescribir
/// cuando se quiera y el comportamiento de la pantalla no se mueve.</para>
///
/// <para><b>Como llega al front</b>: <c>GlobalExceptionHandler</c> la trata como cualquier otra
/// <see cref="ValidationException"/> (HTTP 400, mismo <c>code = "validation_failed"</c> de siempre,
/// para no cambiarle el contrato a nadie) y le suma el campo <c>validationCode</c>.</para>
///
/// <para><b>El codigo NO es texto para mostrar</b>: es una palabra interna que la pantalla usa para
/// decidir. Lo que se muestra es siempre el <see cref="System.Exception.Message"/> (P-17).</para>
/// </summary>
public sealed class CodedValidationException : ValidationException
{
    /// <summary>Codigo estable de la causa. Ejemplo: <c>"aiClaveFaltante"</c>.</summary>
    public string Code { get; }

    public CodedValidationException(string code, string message) : base(message)
    {
        Code = code;
    }
}

/// <summary>
/// Los codigos de validacion que hoy la pantalla necesita distinguir. Vive aca (y no suelto en un
/// string cualquiera) para que backend y frontend hablen del mismo valor y se pueda buscar quien lo usa.
/// </summary>
public static class ValidationCodes
{
    /// <summary>
    /// Se cambio de proveedor de inteligencia artificial (o nunca hubo clave) y el pedido no trajo
    /// una clave nueva. La pantalla usa este codigo para dejar el foco en el casillero de la clave.
    /// </summary>
    public const string AiApiKeyMissing = "aiClaveFaltante";
}
