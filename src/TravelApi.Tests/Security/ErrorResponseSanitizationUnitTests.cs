using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Logging.Abstractions;
using TravelApi.Application.DTOs;
using TravelApi.Domain.Exceptions;
using TravelApi.Errors;
using TravelApi.Middleware;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Security;

/// <summary>
/// Hardening sistemico (2026-06-28): garantiza que NINGUNA respuesta de error filtre informacion
/// tecnica al usuario (nombres de tipo .NET, namespaces, strings del framework en ingles, stack traces,
/// path interno de campos). Cubre las DOS vias residuales app-wide:
///  A) el 400 automatico de [ApiController] por error de model binding/conversion;
///  B) el 500 generico del GlobalExceptionHandler ante una excepcion no controlada.
///
/// El namespace NO contiene "Integration" (entra en la suite no-Docker) y los nombres contienen "Unit"
/// para que el filtro de CI (FullyQualifiedName~Unit) tambien los corra.
/// </summary>
public class ErrorResponseSanitizationUnitTests
{
    // Tokens que JAMAS deben aparecer en el body de una respuesta de error de cara al usuario.
    // Incluye fragmentos de las oraciones en INGLES de los DataAnnotations por defecto: si el framework
    // agrega un default nuevo (o se nos escapa un atributo sin ErrorMessage propio), la suite lo rompe.
    private static readonly string[] ForbiddenLeakTokens =
    {
        // tipos / path / conversion
        "TravelApi", "System.", "The JSON value", "$.",
        // estructura de los defaults en ingles de DataAnnotations
        "The field ", "field is required", "must be", "must be between", "must be a string",
        "minimum length", "maximum length", "is not a valid", "is not valid", "must match",
    };

    private static void AssertNoTechnicalLeak(string body)
    {
        foreach (var token in ForbiddenLeakTokens)
        {
            Assert.DoesNotContain(token, body, StringComparison.Ordinal);
        }
    }

    // =====================================================================
    // A) Saneo del 400 de model binding/validacion (factory pura, sin HTTP).
    // =====================================================================

    [Fact]
    public void BuildSanitizedErrors_JsonConversionError_StringMessageWithTypeName_IsReplaced_KeyDropped()
    {
        var modelState = new ModelStateDictionary();
        // Forma REAL en .NET 8 (verificada): el error de conversion JSON llega como ErrorMessage de TEXTO
        // (Exception == null) bajo la clave del path interno "$.ownerType", y el texto incluye el nombre
        // del tipo .NET. Este es el caso que el discriminador "solo Exception" NO atrapaba.
        modelState.AddModelError(
            "$.ownerType",
            "The JSON value could not be converted to TravelApi.Application.DTOs.BankAccountUpsertRequest. Path: $.ownerType | LineNumber: 0 | BytePositionInLine: 18.");

        var sanitized = ApiValidationErrorResponseFactory.BuildSanitizedErrors(modelState);

        // La clave interna "$.ownerType" NO debe sobrevivir.
        Assert.DoesNotContain("$.ownerType", sanitized.Keys);
        var allMessages = sanitized.Values.SelectMany(values => values).ToArray();
        Assert.Contains(ApiValidationErrorResponseFactory.BindingErrorMessage, allMessages);

        // Ni las claves ni los valores filtran nada tecnico.
        AssertNoTechnicalLeak(JsonSerializer.Serialize(sanitized));
    }

    [Fact]
    public void BuildSanitizedErrors_FrameworkException_IsReplaced()
    {
        var modelState = new ModelStateDictionary();
        // Variante con Exception seteada (otros binders la usan): tambien debe colapsar a generico.
        modelState.TryAddModelException(
            "$.adults",
            new JsonException("The JSON value could not be converted to System.Int32. Path: $.adults"));

        var sanitized = ApiValidationErrorResponseFactory.BuildSanitizedErrors(modelState);

        Assert.DoesNotContain("$.adults", sanitized.Keys);
        Assert.Contains(ApiValidationErrorResponseFactory.BindingErrorMessage,
            sanitized.Values.SelectMany(values => values));
        AssertNoTechnicalLeak(JsonSerializer.Serialize(sanitized));
    }

    [Fact]
    public void BuildSanitizedErrors_DefaultEnglishRequiredMessage_IsReplaced()
    {
        var modelState = new ModelStateDictionary();
        // [Required] SIN ErrorMessage propio -> texto por defecto del framework, en ingles. No es nuestro,
        // asi que se reemplaza (el usuario nunca debe ver copy en ingles).
        modelState.AddModelError("NewCatalogProduct.Name", "The Name field is required.");

        var sanitized = ApiValidationErrorResponseFactory.BuildSanitizedErrors(modelState);

        Assert.Contains(ApiValidationErrorResponseFactory.BindingErrorMessage,
            sanitized.Values.SelectMany(values => values));
        Assert.DoesNotContain("field is required", JsonSerializer.Serialize(sanitized));
    }

    /// <summary>
    /// Cobertura del SET COMPLETO de DataAnnotations built-in usados SIN ErrorMessage propio: sus mensajes
    /// por defecto (en ingles, con el nombre de la propiedad) NO deben llegar al usuario. Cada InlineData es
    /// el texto EXACTO que produce el framework en .NET 8. Este es el agujero que la revision encontro: el
    /// discriminador "solo deny-list de frases conocidas" se les escapaba.
    /// </summary>
    [Theory]
    // MinLength
    [InlineData("Reason", "The field Reason must be a string or array type with a minimum length of '10'.")]
    // MaxLength
    [InlineData("City", "The field City must be a string or array type with a maximum length of '100'.")]
    // StringLength
    [InlineData("Note", "The field Note must be a string with a maximum length of '50'.")]
    // Range
    [InlineData("Days", "The field Days must be between 1 and 100.")]
    // EmailAddress
    [InlineData("Email", "The Email field is not a valid e-mail address.")]
    // RegularExpression
    [InlineData("Code", "The field Code must match the regular expression '^[A-Z]+$'.")]
    // Required (mensaje por defecto)
    [InlineData("Name", "The Name field is required.")]
    // Conversion de binder no-JSON ("The value 'X' is not valid for {1}.")
    [InlineData("Adults", "The value 'abc' is not valid for Adults.")]
    public void BuildSanitizedErrors_DefaultEnglishDataAnnotation_IsReplaced_NeverLeaks(
        string propertyKey, string frameworkDefaultMessage)
    {
        var modelState = new ModelStateDictionary();
        modelState.AddModelError(propertyKey, frameworkDefaultMessage);

        var sanitized = ApiValidationErrorResponseFactory.BuildSanitizedErrors(modelState);

        // Se reemplaza por el generico amable.
        Assert.Contains(ApiValidationErrorResponseFactory.BindingErrorMessage,
            sanitized.Values.SelectMany(values => values));

        // Y el body resultante NO contiene ni el nombre de la propiedad ni copy en ingles.
        var serialized = JsonSerializer.Serialize(sanitized);
        Assert.DoesNotContain(propertyKey, serialized, StringComparison.Ordinal);
        AssertNoTechnicalLeak(serialized);
    }

    [Fact]
    public void BuildSanitizedErrors_AuthoredDataAnnotationMessage_IsPreservedAsIs()
    {
        var modelState = new ModelStateDictionary();
        // Mensaje escrito por nosotros (DataAnnotation en espanol): Exception == null.
        modelState.AddModelError("RazonSocial", "La razón social es obligatoria.");

        var sanitized = ApiValidationErrorResponseFactory.BuildSanitizedErrors(modelState);

        Assert.True(sanitized.ContainsKey("RazonSocial"));
        Assert.Contains("La razón social es obligatoria.", sanitized["RazonSocial"]);
        // Y NO se agrega el mensaje generico de binding cuando no hubo error de binding.
        Assert.DoesNotContain(ApiValidationErrorResponseFactory.BindingErrorMessage,
            sanitized.Values.SelectMany(values => values));
    }

    [Fact]
    public void BuildSanitizedErrors_MixedErrors_KeepsAuthored_AndCollapsesBindingIntoGeneric()
    {
        var modelState = new ModelStateDictionary();
        modelState.AddModelError("RazonSocial", "La razón social es obligatoria.");
        modelState.TryAddModelException(
            "$.ownerType",
            new JsonException("The JSON value could not be converted to TravelApi.Domain.Entities.BankAccountOwnerType. Path: $.ownerType"));

        var sanitized = ApiValidationErrorResponseFactory.BuildSanitizedErrors(modelState);

        // El autorado se conserva bajo su campo.
        Assert.Contains("La razón social es obligatoria.", sanitized["RazonSocial"]);
        // El de binding se colapsa en una entrada generica con el mensaje amable.
        Assert.Contains(ApiValidationErrorResponseFactory.BindingErrorMessage,
            sanitized.Values.SelectMany(values => values));
        AssertNoTechnicalLeak(JsonSerializer.Serialize(sanitized));
    }

    // =====================================================================
    // A-bis) Guard: ningun ErrorMessage AUTORADO filtra nombres internos ni jerga.
    // =====================================================================

    // Fragmentos que un mensaje de cara al usuario (no tecnico, espanol) NO debe contener:
    // nombres de propiedad C# internos, identificadores en ingles y jerga tecnica.
    private static readonly string[] AuthoredMessageForbiddenFragments =
    {
        "Override", "PublicId", "GrossAmount", "ISO 4217", ">=",
        "Amount", "Currency", "Comment", "Reason", "Percent", "Days", "Threshold", "Tolerance",
    };

    /// <summary>
    /// Recorre por REFLEXION TODOS los ErrorMessage autorados en los DTOs de la capa Application
    /// (propiedades + parametros de constructor de records) y exige que ninguno filtre un nombre interno
    /// de propiedad o jerga. El sanitizador PRESERVA los mensajes autorados, asi que deben estar limpios
    /// en origen. Si alguien agrega un DataAnnotation con ErrorMessage que nombra el campo interno, este
    /// test lo rompe.
    /// </summary>
    [Fact]
    public void AuthoredErrorMessages_DoNotLeakInternalNamesOrJargon()
    {
        var assembly = typeof(OperationalFinanceSettingsDto).Assembly;
        var offenders = new List<string>();

        foreach (var message in CollectAuthoredErrorMessages(assembly))
        {
            foreach (var fragment in AuthoredMessageForbiddenFragments)
            {
                if (message.Contains(fragment, StringComparison.Ordinal))
                    offenders.Add($"'{message}' contiene '{fragment}'");
            }
        }

        Assert.True(offenders.Count == 0,
            "Hay ErrorMessage autorados que filtran nombres internos o jerga:\n" + string.Join("\n", offenders));
    }

    private static IEnumerable<string> CollectAuthoredErrorMessages(Assembly assembly)
    {
        foreach (var type in assembly.GetTypes())
        {
            // Propiedades (DTOs tipo clase, p.ej. OperationalFinanceSettingsDto).
            foreach (var property in type.GetProperties())
            {
                foreach (var attribute in property.GetCustomAttributes<ValidationAttribute>(inherit: true))
                {
                    if (!string.IsNullOrEmpty(attribute.ErrorMessage))
                        yield return attribute.ErrorMessage;
                }
            }

            // Parametros de constructor (records posicionales, p.ej. EditLiquidationRequest).
            foreach (var constructor in type.GetConstructors())
            {
                foreach (var parameter in constructor.GetParameters())
                {
                    foreach (var attribute in parameter.GetCustomAttributes<ValidationAttribute>(inherit: true))
                    {
                        if (!string.IsNullOrEmpty(attribute.ErrorMessage))
                            yield return attribute.ErrorMessage;
                    }
                }
            }
        }
    }

    // =====================================================================
    // B) GlobalExceptionHandler: body SIEMPRE amable, sin detalle tecnico.
    // =====================================================================

    private static async Task<(int status, string body)> RunHandlerAsync(Exception exception)
    {
        // El handler ya NO depende de IHostEnvironment: se comporta igual en Development y Production.
        var handler = new GlobalExceptionHandler(NullLogger<GlobalExceptionHandler>.Instance);

        var context = new DefaultHttpContext();
        context.TraceIdentifier = "trace-ref-123";
        context.Response.Body = new MemoryStream();

        var handled = await handler.TryHandleAsync(context, exception, CancellationToken.None);
        Assert.True(handled);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body);
        var body = await reader.ReadToEndAsync();
        return (context.Response.StatusCode, body);
    }

    [Fact]
    public async Task UnhandledException_ReturnsFriendlySpanish_AndNoTechnicalDetail()
    {
        var (status, body) = await RunHandlerAsync(
            new InvalidOperationException("SUPER_SECRET internal detail at TravelApi.Internal.Foo.Bar()"));

        Assert.Equal(StatusCodes.Status500InternalServerError, status);
        Assert.Contains("Ocurrió un error inesperado", body);

        // El body NO contiene el mensaje crudo, el tipo, ni rastros de stack/excepcion.
        Assert.DoesNotContain("SUPER_SECRET", body, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidOperationException", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", body, StringComparison.Ordinal);
        Assert.DoesNotContain("stack", body, StringComparison.OrdinalIgnoreCase);
        AssertNoTechnicalLeak(body);

        // El codigo de referencia OPACO si esta (para cruzar con el log del servidor).
        Assert.Contains("trace-ref-123", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DatabaseUnavailable_Returns503Friendly_WithoutLeakingDriverMessage()
    {
        // TimeoutException es clasificada como "base no disponible" por DatabaseExceptionClassifier.
        var (status, body) = await RunHandlerAsync(
            new TimeoutException("Npgsql connection to host db-secret-host:5432 timed out"));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, status);
        Assert.Contains("Base de datos no disponible", body);
        Assert.DoesNotContain("db-secret-host", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Timeout", body, StringComparison.OrdinalIgnoreCase);
        AssertNoTechnicalLeak(body);
    }

    // =====================================================================
    // C) Tanda 3 "contrato pantalla-motor" (2026-07-20): el 409 de anular/confirmar una cancelacion
    //    (draft/confirm de BookingCancellation, INV-152/081/100/093) YA viaja con su codigo de negocio en
    //    `invariantCode` porque nace como BusinessInvariantViolationException — el GlobalExceptionHandler lo
    //    agrega hace tiempo (ver arriba en el propio middleware). Este test cierra la verificacion pedida por
    //    la Tanda 3: cada uno de los 4 codigos que el frontend va a mapear (spec UX 2026-07-20) efectivamente
    //    llega en el body 409, Y el mensaje de negocio en español sigue intacto (Decision C, envelope aditivo:
    //    esta tanda NO cambia ni un caracter de esos mensajes, solo confirma que el codigo ya viajaba).
    // =====================================================================
    [Theory]
    [InlineData("INV-152", "Esta reserva tiene servicios de más de un operador; no se puede anular desde acá.")]
    [InlineData("INV-081", "Esta reserva ya tiene una anulación activa en curso.")]
    [InlineData("INV-100", "La factura de esta reserva ya fue anulada con una nota de crédito.")]
    [InlineData("INV-093", "Esta anulación cambió de estado mientras la tenías abierta.")]
    public async Task BusinessInvariantViolation_AnnulReservaCodes_CarryInvariantCode_AndKeepMessageIntact(
        string invariantCode, string businessMessage)
    {
        var (status, body) = await RunHandlerAsync(
            new BusinessInvariantViolationException(businessMessage, invariantCode: invariantCode));

        Assert.Equal(StatusCodes.Status409Conflict, status);

        // El codigo estable llega SIN transformar (mismo texto que usa BookingCancellationService al lanzar).
        Assert.Contains($"\"invariantCode\":\"{invariantCode}\"", body, StringComparison.Ordinal);

        // El mensaje de negocio en español (lo que el vendedor lee) llega intacto, sin recortar ni reemplazar
        // por el generico — a diferencia del InvalidOperationException "tecnico" que SI se sanea.
        Assert.Contains(businessMessage, body, StringComparison.Ordinal);
        AssertNoTechnicalLeak(body);
    }
}

/// <summary>
/// Variante HTTP del hardening A: recorre el pipeline real ([ApiController] + InvalidModelStateResponseFactory)
/// para confirmar que un body con un enum malformado devuelve 400 con SOLO espanol amable. Usa el host completo
/// con InMemory (sin Docker). El nombre contiene "Unit" para entrar en el filtro de CI.
/// </summary>
public class ErrorResponseSanitizationHttpUnitTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public ErrorResponseSanitizationHttpUnitTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task PutSettings_AuthoredSpanishDataAnnotation_PassesThroughUnchanged()
    {
        var client = _factory.CreateClient();

        // MaxDiscountPercentWithoutOverride tiene un [Range] con ErrorMessage AUTORADO en espanol.
        // Ese mensaje (nuestro) debe sobrevivir intacto al saneo global: NO se reemplaza por el generico.
        var dto = new TravelApi.Application.DTOs.OperationalFinanceSettingsDto
        {
            RequireFullPaymentForOperativeStatus = true,
            RequireFullPaymentForVoucher = true,
            AfipInvoiceControlMode = "AllowAgentOverrideWithReason",
            EnableUpcomingUnpaidReservationNotifications = true,
            UpcomingUnpaidReservationAlertDays = 7,
            MaxDiscountPercentWithoutOverride = 150m,
        };

        var response = await client.PutAsJsonAsync("/api/settings/operational-finance", dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        // El MENSAJE autorado (lo unico que el front renderiza: aplana Object.values(errors)) ahora es de
        // negocio y en espanol, sin el nombre interno de la propiedad. La CLAVE del error sigue siendo el
        // nombre del campo (comportamiento estandar de ValidationProblemDetails); no se le muestra al usuario.
        Assert.Contains("El porcentaje máximo de descuento debe estar entre 0 y 100.", body);
    }

    [Fact]
    public async Task PostBankAccount_MalformedEnumBody_Returns400_FriendlySpanish_NoTechnicalLeak()
    {
        var client = _factory.CreateClient();

        // ownerType = "abc" no convierte al enum BankAccountOwnerType -> error de deserializacion del
        // framework que (sin el saneo) filtraria el nombre del tipo .NET y el path "$.ownerType".
        var malformedBody = new StringContent(
            "{\"ownerType\":\"abc\"}", Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/api/bank-accounts", malformedBody);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();

        // Debe contener el mensaje amable y NADA tecnico.
        Assert.Contains(ApiValidationErrorResponseFactory.BindingErrorMessage, body);
        AssertHttpBodyHasNoLeak(body);
    }

    [Fact]
    public async Task PostCancellation_ReasonTooShort_DefaultMinLength_Returns400_FriendlySpanish_NoLeak()
    {
        var client = _factory.CreateClient();

        // DraftCancellationRequest.Reason tiene [MinLength(10)] SIN ErrorMessage propio. Con Reason corto,
        // el framework genera "The field Reason must be a string or array type with a minimum length of '10'."
        // (ingles + nombre de propiedad). Pasa por el pipeline real (Admin bypassea el permiso). Debe salir
        // saneado: generico espanol y sin filtrar el mensaje en ingles ni el nombre del campo.
        var body = new StringContent(
            "{\"reservaPublicId\":\"" + Guid.NewGuid() + "\",\"reason\":\"corto\"}",
            Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/api/cancellations", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.Contains(ApiValidationErrorResponseFactory.BindingErrorMessage, responseBody);
        Assert.DoesNotContain("Reason", responseBody, StringComparison.Ordinal);
        AssertHttpBodyHasNoLeak(responseBody);
    }

    [Fact]
    public async Task PostCancellation_ReasonTooLong_DefaultMaxLength_Returns400_FriendlySpanish_NoLeak()
    {
        var client = _factory.CreateClient();

        // [MaxLength(1000)] sin ErrorMessage: Reason de 1001 chars -> "The field Reason must be a string or
        // array type with a maximum length of '1000'." Mismo saneo esperado.
        var tooLongReason = new string('a', 1001);
        var body = new StringContent(
            "{\"reservaPublicId\":\"" + Guid.NewGuid() + "\",\"reason\":\"" + tooLongReason + "\"}",
            Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/api/cancellations", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.Contains(ApiValidationErrorResponseFactory.BindingErrorMessage, responseBody);
        Assert.DoesNotContain("Reason", responseBody, StringComparison.Ordinal);
        AssertHttpBodyHasNoLeak(responseBody);
    }

    // Mismos tokens prohibidos que la clase de unit, pero local a la clase HTTP (no comparte el helper privado).
    private static void AssertHttpBodyHasNoLeak(string body)
    {
        foreach (var token in new[]
        {
            "TravelApi", "System.", "The JSON value", "$.",
            "The field ", "field is required", "must be", "must be between", "must be a string",
            "minimum length", "maximum length", "is not a valid", "is not valid", "must match",
        })
        {
            Assert.DoesNotContain(token, body, StringComparison.Ordinal);
        }
    }
}
