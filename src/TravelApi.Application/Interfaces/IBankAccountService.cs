using TravelApi.Application.DTOs;
using TravelApi.Domain.Entities;

namespace TravelApi.Application.Interfaces;

/// <summary>
/// ADR-041 (2026-06-27): alta/baja/edicion y listado de las cuentas bancarias polimorficas (Agencia / Cliente /
/// Proveedor). La AUTORIZACION por dueño la resuelve el controller (depende del rol y del tipo de dueño en
/// runtime); este servicio se ocupa de VALIDAR, ENMASCARAR, PERSISTIR y AUDITAR.
/// </summary>
public interface IBankAccountService
{
    /// <summary>
    /// Lista las cuentas ACTIVAS de un dueño. El CBU viaja ENMASCARADO (solo ultimos 4). Orden estable por
    /// fecha de creacion. <paramref name="ownerId"/> es el Id INTERNO (int) del dueño (Agency=0). Para resolver
    /// el token publico (GUID) que manda el front a este Id interno, usar <see cref="ResolveOwnerInternalIdAsync"/>.
    /// </summary>
    Task<IReadOnlyList<BankAccountListItemDto>> ListAsync(
        BankAccountOwnerType ownerType,
        int ownerId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Traduce el TOKEN PUBLICO del dueño (lo que viaja por la API) al Id INTERNO (int) con el que se persiste y
    /// consulta. Es el unico punto donde se hace esa traduccion (centralizado, no duplicado).
    ///
    /// <para>Reglas por tipo de dueño:
    /// <list type="bullet">
    /// <item>Agencia -> 0 fijo (singleton). El token se ignora (no hay tabla "Agency" que validar).</item>
    /// <item>Cliente/Proveedor -> el token debe ser su <c>PublicId</c> (GUID); se resuelve al Id interno y el dueño
    /// debe existir y estar ACTIVO. Token vacio/no-GUID, o dueño inexistente/inactivo -> <see cref="ArgumentException"/>
    /// (el controller lo mapea a 400).</item>
    /// </list>
    /// Se usa el PublicId (no el Id interno) por coherencia con el resto de la API y para no exponer ids secuenciales.</para>
    /// </summary>
    Task<int> ResolveOwnerInternalIdAsync(
        BankAccountOwnerType ownerType,
        string? ownerToken,
        CancellationToken cancellationToken);

    /// <summary>
    /// Devuelve el detalle COMPLETO (CBU sin enmascarar) de una cuenta por su PublicId, o null si no existe.
    /// Lo usa el form de edicion para pre-llenar. El controller gatea el acceso por el permiso de lectura del
    /// dueño de la cuenta.
    /// </summary>
    Task<BankAccountDetailDto?> GetByPublicIdAsync(Guid publicId, CancellationToken cancellationToken);

    /// <summary>
    /// Registra (y persiste) un evento de auditoria <c>BankAccountDetailViewed</c> por el ACCESO al dato
    /// completo (CBU/alias desenmascarados). Lo llama el endpoint de detalle DESPUES de autorizar, sobre el DTO
    /// ya cargado (no recarga de BD). Es la unica lectura que se audita (la lista va enmascarada, no se audita).
    /// </summary>
    Task AuditDetailViewedAsync(
        BankAccountDetailDto account,
        string actorUserId,
        string? actorUserName,
        CancellationToken cancellationToken);

    /// <summary>
    /// Crea una cuenta. Valida server-side: titular y moneda obligatorios; al menos uno de CBU/alias; CBU = 22
    /// digitos si viene; alias formato AR si viene; y el dueño (Cliente/Proveedor, identificado por su
    /// <c>PublicId</c> en <c>request.OwnerId</c>) debe existir y estar activo (Agencia -> OwnerId 0 fijo, token
    /// ignorado). La traduccion token->Id interno la hace <see cref="ResolveOwnerInternalIdAsync"/>. Audita el
    /// alta en la MISMA transaccion (StageBusinessEvent).
    ///
    /// <para>Devuelve el shape ENMASCARADO (<see cref="BankAccountListItemDto"/>, CBU/alias tapados): ninguna
    /// ESCRITURA expone el dato bancario en claro. Para el CBU completo esta el GET de detalle, que ademas
    /// audita la lectura (BankAccountDetailViewed) — asi no queda un camino para leer el dato sin rastro.</para>
    /// </summary>
    Task<BankAccountListItemDto> CreateAsync(
        BankAccountUpsertRequest request,
        string actorUserId,
        string? actorUserName,
        CancellationToken cancellationToken);

    /// <summary>
    /// Edita una cuenta existente (identificada por PublicId). NO permite cambiar el dueño (OwnerType/OwnerId del
    /// request se ignoran). Mismas validaciones que el alta. Audita en la misma transaccion.
    /// Devuelve el shape ENMASCARADO (ver <see cref="CreateAsync"/>): la escritura no expone el CBU/alias en claro.
    /// </summary>
    Task<BankAccountListItemDto> UpdateAsync(
        Guid publicId,
        BankAccountUpsertRequest request,
        string actorUserId,
        string? actorUserName,
        CancellationToken cancellationToken);

    /// <summary>
    /// Desactiva (soft-delete: IsActive=false) una cuenta. Idempotente: desactivar una ya inactiva no falla.
    /// Audita en la misma transaccion.
    /// </summary>
    Task DeactivateAsync(
        Guid publicId,
        string actorUserId,
        string? actorUserName,
        CancellationToken cancellationToken);

    /// <summary>
    /// Marca una cuenta como PRINCIPAL del dueño para su moneda, desmarcando atomicamente cualquier otra
    /// principal del mismo (OwnerType, OwnerId, Currency). Rechaza una cuenta inactiva (no puede ser principal).
    /// Idempotente si ya era principal. Audita el cambio (<c>BankAccountSetPrimary</c>) en la misma transaccion.
    /// Devuelve el shape ENMASCARADO (ver <see cref="CreateAsync"/>): la escritura no expone el CBU/alias en claro.
    /// </summary>
    Task<BankAccountListItemDto> SetPrimaryAsync(
        Guid publicId,
        string actorUserId,
        string? actorUserName,
        CancellationToken cancellationToken);
}
