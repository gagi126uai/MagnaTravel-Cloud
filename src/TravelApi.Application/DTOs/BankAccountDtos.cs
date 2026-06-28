using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using TravelApi.Domain.Entities;

namespace TravelApi.Application.DTOs;

/// <summary>
/// ADR-041 (2026-06-27): item de la LISTA de cuentas bancarias de un dueño. El CBU y el ALIAS viajan
/// ENMASCARADOS (<see cref="CbuMasked"/> = ultimos 4, <see cref="AliasMasked"/> = primeros/ultimos 2) — la lista
/// NUNCA expone el dato completo. Ambos son destino de transferencia. Para el dato completo (pre-llenar el form
/// de edicion) esta el endpoint de detalle, que devuelve <see cref="BankAccountDetailDto"/> y queda auditado.
/// </summary>
public record BankAccountListItemDto(
    Guid PublicId,
    BankAccountOwnerType OwnerType,
    // OwnerType (enum) SI viaja: el front/autorizacion lo usan. El OwnerId interno (int secuencial de
    // Customer/Supplier) NO se expone (hardening 2026-06-28): es un identificador interno que el front no lee.
    // El dueño concreto ya se direcciona por PublicId (GUID) en el resto de la API.
    string? CbuMasked,
    string? AliasMasked,
    string HolderName,
    string Currency,
    string? Bank,
    BankAccountType? AccountType,
    string? HolderTaxId,
    string? Notes,
    bool IsActive,
    DateTime CreatedAt,
    // IsPrimary: cuenta principal del dueño PARA ESTA MONEDA (a donde transferir por defecto). En la lista las
    // principales vienen primero (por moneda). Default false para no romper construcciones existentes por posicion.
    bool IsPrimary = false);

/// <summary>
/// ADR-041: detalle COMPLETO de una cuenta (incluye el CBU sin enmascarar). Se usa para el detalle/edicion.
/// El acceso esta gateado por el permiso de lectura del dueño (ver BankAccountsController).
/// </summary>
public record BankAccountDetailDto(
    Guid PublicId,
    BankAccountOwnerType OwnerType,
    // Igual que en la lista: el OwnerId interno NO se expone en la respuesta (hardening 2026-06-28). El OwnerType
    // (enum) si, porque la autorizacion del detalle se decide por el tipo de dueño de la cuenta.
    string? Cbu,
    string? Alias,
    string HolderName,
    string Currency,
    string? Bank,
    BankAccountType? AccountType,
    string? HolderTaxId,
    string? Notes,
    bool IsActive,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    // IsPrimary: cuenta principal del dueño para esta moneda. Default false al final para no romper las
    // construcciones posicionales existentes (tests, mapeos por nombre).
    bool IsPrimary = false);

/// <summary>
/// ADR-041: request de alta/edicion de una cuenta bancaria. En el ALTA, <see cref="OwnerType"/> y
/// <see cref="OwnerId"/> definen el dueño. En la EDICION estos dos campos se IGNORAN (no se puede mover una
/// cuenta de un dueño a otro — eso seria una reasignacion sensible); el dueño persistido se conserva.
///
/// <para><b>OwnerId es un TOKEN PUBLICO, no el Id interno</b> (bugfix 2026-06-28): para Cliente/Proveedor es el
/// <c>PublicId</c> (GUID) — el mismo identificador publico que usan TODOS los demas endpoints de cuentas
/// (detalle/editar/borrar van por <c>{publicId:guid}</c>). Para la Agencia el front manda un 0 numerico que el
/// servidor IGNORA (la Agencia es un singleton, su OwnerId interno es 0 fijo). Como el front manda un numero (0)
/// para la Agencia y un texto (GUID) para los otros, el campo es <c>string?</c> y lo deserializa
/// <see cref="OwnerReferenceJsonConverter"/>, que acepta ambas formas (numero o texto). El servicio traduce este
/// token al Id interno (int) con <c>ResolveOwnerInternalIdAsync</c>.</para>
/// </summary>
public record BankAccountUpsertRequest(
    BankAccountOwnerType OwnerType,
    [property: JsonConverter(typeof(OwnerReferenceJsonConverter))]
    string? OwnerId,
    string? Cbu,
    string? Alias,
    string HolderName,
    string Currency,
    string? Bank,
    BankAccountType? AccountType,
    string? HolderTaxId,
    string? Notes,
    // IsPrimary: si viene true, esta cuenta queda como principal del dueño para su moneda (desmarcando la
    // anterior principal de ese mismo dueño+moneda). Default false: el alta/edicion no toca el principal salvo
    // que el front lo pida. Nota: la PRIMERA cuenta activa de un dueño+moneda queda principal automaticamente.
    bool IsPrimary = false);

/// <summary>
/// Lee el campo <c>ownerId</c> del body de alta/edicion de cuenta bancaria, que llega POLIMORFICO: un NUMERO
/// (0) cuando el dueño es la Agencia, o un TEXTO (el PublicId GUID) cuando es Cliente/Proveedor. System.Text.Json
/// por defecto NO convierte un numero JSON a una propiedad <c>string</c> (tira excepcion), por eso necesitamos
/// este converter: unifica ambas formas en un <c>string</c> que despues el servicio resuelve al Id interno.
///
/// <para>Por que un converter y no cambiar el front: TODOS los demas endpoints de cuentas (detalle/editar/borrar)
/// ya direccionan por PublicId GUID, y el front ya manda ese GUID aca. El converter hace que el backend acepte lo
/// que el front YA envia, sin tocar el front.</para>
/// </summary>
public sealed class OwnerReferenceJsonConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;

            case JsonTokenType.String:
                return reader.GetString();

            // La Agencia viaja como numero (0). Lo pasamos a texto para tratar todo como un unico tipo de token.
            // El valor de la Agencia se ignora despues (su Id interno es 0 fijo), asi que el formato exacto da igual.
            case JsonTokenType.Number:
                if (reader.TryGetInt64(out var asLong))
                    return asLong.ToString(CultureInfo.InvariantCulture);
                return reader.GetDouble().ToString(CultureInfo.InvariantCulture);

            default:
                throw new JsonException("El identificador del dueño debe ser un texto (GUID) o un número.");
        }
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        // El request es de ENTRADA; igual implementamos Write por completitud (si el DTO se serializara, ej. en logs).
        if (value is null)
            writer.WriteNullValue();
        else
            writer.WriteStringValue(value);
    }
}
