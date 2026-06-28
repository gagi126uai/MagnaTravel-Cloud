using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace TravelApi.Errors;

/// <summary>
/// Saneador GLOBAL de los 400 automaticos de [ApiController] (model binding / validacion).
/// Existe para cerrar una fuga de informacion interna: cuando el framework no puede convertir
/// un dato del body/query (por ejemplo un enum mal escrito), arma un ValidationProblemDetails
/// cuyo mensaje incluye el NOMBRE DEL TIPO .NET ("...could not be converted to
/// TravelApi.Domain.Entities.BankAccountOwnerType. Path: $.ownerType") y el path interno del campo.
/// Ese 400 lo produce el pipeline ANTES de que corra cualquier exception handler, asi que el
/// GlobalExceptionHandler no alcanza a taparlo: hay que interceptarlo aca, en la fabrica de respuestas.
///
/// <para>El objetivo es que el usuario (no tecnico, en espanol) NUNCA vea nombres de clases, namespaces,
/// strings del framework en ingles ni el path interno del campo. Mantenemos la MISMA forma de respuesta
/// que el front ya sabe leer (ValidationProblemDetails con <c>errors{}</c> + <c>title</c>), solo cambiamos
/// el CONTENIDO de los mensajes que filtran detalle interno.</para>
/// </summary>
public static class ApiValidationErrorResponseFactory
{
    /// <summary>
    /// Mensaje generico que reemplaza a CUALQUIER error generado por el framework (binding, conversion,
    /// deserializacion o validacion por defecto en ingles). Nunca repite el valor recibido ni nombra el
    /// campo interno: es deliberadamente vago para no filtrar nada. "Valor invalido" (no "formato") porque
    /// cubre tanto un dato malformado como un campo faltante.
    /// </summary>
    public const string BindingErrorMessage =
        "Hay un dato con un valor inválido. Revisá lo ingresado e intentá de nuevo.";

    /// <summary>Titulo en espanol del 400 (reemplaza el "One or more validation errors occurred." del framework).</summary>
    public const string ValidationProblemTitle = "Revisá los datos ingresados.";

    /// <summary>
    /// Clave NEUTRAL bajo la que se agrupan los errores de binding. Usamos una clave generica a proposito:
    /// la clave real del framework para un error de deserializacion suele ser el path interno ("$.ownerType"),
    /// que tambien es una fuga (delata el nombre del campo y el patron "$."). El front aplana
    /// <c>Object.values(errors)</c>, asi que la clave no se le muestra al usuario; solo importa el valor.
    /// </summary>
    private const string GenericBindingErrorKey = "solicitud";

    /// <summary>
    /// Punto de enganche para <c>ApiBehaviorOptions.InvalidModelStateResponseFactory</c>.
    /// Devuelve un 400 con ProblemDetails saneado.
    /// </summary>
    public static IActionResult Create(ActionContext context)
    {
        var sanitizedErrors = BuildSanitizedErrors(context.ModelState);

        var problem = new ValidationProblemDetails(sanitizedErrors)
        {
            Status = StatusCodes.Status400BadRequest,
            Title = ValidationProblemTitle,
        };
        problem.Extensions["code"] = "validation_failed";

        return new BadRequestObjectResult(problem)
        {
            ContentTypes = { "application/problem+json" },
        };
    }

    /// <summary>
    /// Marcadores que delatan un mensaje/clave generado por el FRAMEWORK: errores de
    /// binding/conversion/deserializacion Y los mensajes POR DEFECTO (en ingles) de los DataAnnotations
    /// usados SIN ErrorMessage propio. Si aparece cualquiera, el texto NO es nuestro y se reemplaza.
    ///
    /// <para>PRINCIPIO (recomendacion de la revision de fuga de datos): NO confiamos en una lista de
    /// "frases conocidas que filtran". Como NUESTROS mensajes estan SIEMPRE en espanol (p.ej.
    /// "La razón social es obligatoria."), keyeamos sobre la ESTRUCTURA de las oraciones en INGLES que
    /// el framework genera + los marcadores de tipo/path. Asi cualquier default en ingles cae (aunque
    /// agreguen atributos nuevos), y un mensaje espanol autorado nunca da falso positivo.</para>
    ///
    /// <para>Cobertura del set built-in (verificado .NET 8): Required "The {0} field is required.";
    /// MinLength "The field {0} must be a string or array type with a minimum length of '{1}'.";
    /// MaxLength "...maximum length of '{1}'."; StringLength "The field {0} must be a string with a
    /// maximum length of {1}."; Range "The field {0} must be between {1} and {2}."; EmailAddress/Phone/Url/
    /// CreditCard "The {0} field is not a valid ..."; RegularExpression "The field {0} must match the
    /// regular expression '{1}'."; Compare "'{0}' and '{1}' do not match.". Mas los de conversion JSON
    /// ("The JSON value ... could not be converted to TravelApi...") y de binder ("The value 'X' is not
    /// valid.").</para>
    ///
    /// <para>OJO (verificado .NET 8): el error de conversion JSON NO llega con
    /// <see cref="ModelError.Exception"/> seteada — llega como ErrorMessage de TEXTO con
    /// <c>Exception == null</c> y la clave igual al path interno ("$.ownerType"). Por eso NO alcanza con
    /// mirar <c>Exception</c>: hay que reconocer estos marcadores y el prefijo "$" de la clave.</para>
    /// </summary>
    private static readonly string[] FrameworkLeakMarkers =
    {
        // --- Estructura de oraciones en ingles del set built-in de DataAnnotations ---
        "the field ",                     // "The field {0} must be..." (MinLength/MaxLength/StringLength/Range/RegEx)
        "field is required",              // "The {0} field is required." (Required)
        "is not a valid",                 // "The {0} field is not a valid ..." (Email/Phone/Url/CreditCard)
        "must be a string",               // MinLength/MaxLength/StringLength
        "must be between ",               // Range
        "must be a number",               // conversion numerica / Range numerico
        "minimum length of",              // MinLength/StringLength
        "maximum length of",              // MaxLength/StringLength
        "must match the regular expression", // RegularExpression
        "do not match",                   // Compare
        // --- Binding / conversion / deserializacion ---
        "could not be converted",         // conversion JSON
        "the json value",                 // deserializacion JSON
        "could not convert",              // conversion de tipo
        "is not valid",                   // "The value 'X' is not valid."
        "is in an invalid format",        // formato invalido del binder
        "the input was not valid",        // mensaje generico del input formatter
        "non-empty request body",         // body vacio/ausente
        "the value ",                     // "The value 'X' is not valid for ..."
        // --- Tipos / namespaces / path interno ---
        "system.",                        // namespace del runtime
        "travelapi",                      // namespace propio (nombres de tipos internos)
        "$.",                             // path interno de deserializacion
    };

    /// <summary>
    /// Reconstruye el diccionario de errores separando DOS familias:
    ///  - Errores del FRAMEWORK (binding/conversion/deserializacion o validacion por defecto en ingles):
    ///    se reemplazan por <see cref="BindingErrorMessage"/> y se DESCARTA su clave/path interno.
    ///  - Validaciones escritas por nosotros (DataAnnotations con ErrorMessage en espanol): se PRESERVAN
    ///    tal cual, bajo su campo.
    /// </summary>
    public static Dictionary<string, string[]> BuildSanitizedErrors(ModelStateDictionary modelState)
    {
        var authoredErrorsByField = new Dictionary<string, List<string>>();
        var hasFrameworkError = false;

        foreach (var entry in modelState)
        {
            foreach (var error in entry.Value.Errors)
            {
                if (IsFrameworkError(entry.Key, error))
                {
                    hasFrameworkError = true;
                    continue;
                }

                // Mensaje autorado por nosotros: se conserva intacto bajo su campo (las claves de
                // DataAnnotations son nombres de propiedad C#, nunca el path "$." de deserializacion).
                if (!authoredErrorsByField.TryGetValue(entry.Key, out var messages))
                {
                    messages = new List<string>();
                    authoredErrorsByField[entry.Key] = messages;
                }

                if (!messages.Contains(error.ErrorMessage))
                    messages.Add(error.ErrorMessage);
            }
        }

        var result = new Dictionary<string, string[]>();
        foreach (var field in authoredErrorsByField)
            result[field.Key] = field.Value.ToArray();

        // Si hubo aunque sea un error del framework, agregamos UNA entrada generica (no por campo,
        // para no insinuar cual fallo) con el mensaje amable.
        if (hasFrameworkError)
            result[GenericBindingErrorKey] = new[] { BindingErrorMessage };

        return result;
    }

    /// <summary>
    /// Devuelve true si el error proviene del framework (y por lo tanto NO debe mostrarse al usuario):
    /// trae Exception, no tiene mensaje mostrable, la clave es un path interno ("$..."), o el texto
    /// contiene alguno de los <see cref="FrameworkLeakMarkers"/>.
    /// </summary>
    private static bool IsFrameworkError(string key, ModelError error)
    {
        if (error.Exception is not null)
            return true;

        if (string.IsNullOrEmpty(error.ErrorMessage))
            return true;

        // Las claves de deserializacion JSON empiezan con "$" (p.ej. "$.ownerType"): son path interno.
        if (key.StartsWith("$", StringComparison.Ordinal))
            return true;

        foreach (var marker in FrameworkLeakMarkers)
        {
            if (error.ErrorMessage.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
