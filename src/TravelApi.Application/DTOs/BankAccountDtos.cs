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
    int OwnerId,
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
    int OwnerId,
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
/// </summary>
public record BankAccountUpsertRequest(
    BankAccountOwnerType OwnerType,
    int OwnerId,
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
