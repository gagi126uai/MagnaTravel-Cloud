using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;

namespace TravelApi.Controllers;

/// <summary>
/// ADR-041 (2026-06-27): un solo set de endpoints, parametrizado por dueño, para las cuentas bancarias de la
/// Agencia, los Clientes y los Proveedores. La AUTORIZACION es por dueño y se resuelve EN RUNTIME (depende del
/// tipo de dueño, que viaja en la query/body), por eso NO se puede usar el atributo estatico [RequirePermission]:
/// se chequea a mano contra <see cref="BankAccountAuthorization"/> (con bypass por rol Admin).
///
/// <para><b>Lectura</b> por el *.view del dueño (proveedores.view / clientes.view / configuracion.view).
/// <b>Escritura</b> por proveedores.edit / clientes.edit; la de la Agencia es Admin-only (configuracion).</para>
/// </summary>
[Authorize]
[ApiController]
[Route("api/bank-accounts")]
public class BankAccountsController : ControllerBase
{
    private readonly IBankAccountService _bankAccountService;
    private readonly IUserPermissionResolver _permissionResolver;

    public BankAccountsController(IBankAccountService bankAccountService, IUserPermissionResolver permissionResolver)
    {
        _bankAccountService = bankAccountService;
        _permissionResolver = permissionResolver;
    }

    /// <summary>
    /// Lista las cuentas activas de un dueño. <paramref name="ownerId"/> es el TOKEN PUBLICO del dueño: el
    /// <c>PublicId</c> (GUID) para Cliente/Proveedor (coherente con el resto de la API), o un 0 para la Agencia
    /// (singleton, se ignora). El token se resuelve al Id interno DESPUES de autorizar (la autorizacion depende
    /// solo del tipo de dueño, no del dueño concreto: resolver antes filtraria existencia a quien no tiene permiso).
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<BankAccountListItemDto>>> List(
        [FromQuery] string? ownerType,
        [FromQuery] string? ownerId,
        CancellationToken cancellationToken)
    {
        // ownerType llega como TEXTO crudo (no como enum) a proposito: si lo bindeara [ApiController] a
        // BankAccountOwnerType, un valor malformado (?ownerType=abc) devolveria un 400 automatico cuyo mensaje
        // es el string interno del framework ("The value 'abc' is not valid for ownerType.") — fuga de detalle
        // interno. Lo parseamos a mano y devolvemos un mensaje amable, sin nunca repetir el valor recibido.
        if (!TryParseOwnerType(ownerType, out var ownerTypeValue))
            return BadRequest(new { message = "Tipo de dueño inválido." });

        // Autorizamos ANTES de tocar la BD para resolver el dueño: si resolvieramos primero, un usuario sin permiso
        // podria distinguir un dueño existente (200) de uno inexistente (400) — una fuga de existencia.
        if (!await CanReadAsync(ownerTypeValue, cancellationToken))
            return Forbid();

        try
        {
            // El front manda el PublicId (GUID) del Cliente/Proveedor (o 0 para la Agencia). Lo traducimos al Id
            // interno con el que se consultan las cuentas. Token vacio/invalido o dueño inexistente -> ArgumentException.
            var internalOwnerId = await _bankAccountService.ResolveOwnerInternalIdAsync(ownerTypeValue, ownerId, cancellationToken);
            var accounts = await _bankAccountService.ListAsync(ownerTypeValue, internalOwnerId, cancellationToken);
            return Ok(accounts);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("{publicId:guid}")]
    public async Task<ActionResult<BankAccountDetailDto>> GetByPublicId(Guid publicId, CancellationToken cancellationToken)
    {
        var account = await _bankAccountService.GetByPublicIdAsync(publicId, cancellationToken);
        if (account is null)
            return NotFound();

        // El detalle expone el CBU COMPLETO: gateamos por el permiso de lectura del dueño de ESTA cuenta.
        if (!await CanReadAsync(account.OwnerType, cancellationToken))
            return Forbid();

        // SOLO despues de autorizar: auditamos el acceso al dato desenmascarado (destino de transferencia).
        await _bankAccountService.AuditDetailViewedAsync(account, CurrentUserId(), CurrentUserName(), cancellationToken);

        return Ok(account);
    }

    [HttpPost]
    public async Task<ActionResult<BankAccountListItemDto>> Create(
        [FromBody] BankAccountUpsertRequest request,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(typeof(BankAccountOwnerType), request.OwnerType))
            return BadRequest(new { message = "Tipo de dueño inválido." });

        if (!await CanWriteAsync(request.OwnerType, cancellationToken))
            return Forbid();

        try
        {
            var created = await _bankAccountService.CreateAsync(request, CurrentUserId(), CurrentUserName(), cancellationToken);
            return CreatedAtAction(nameof(GetByPublicId), new { publicId = created.PublicId }, created);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPut("{publicId:guid}")]
    public async Task<ActionResult<BankAccountListItemDto>> Update(
        Guid publicId,
        [FromBody] BankAccountUpsertRequest request,
        CancellationToken cancellationToken)
    {
        // El service IGNORA el OwnerType del body (la edicion no cambia de dueño), pero igual rechazamos un valor
        // de enum fuera de rango: es input malformado y la respuesta correcta es 400, no procesarlo en silencio.
        if (!Enum.IsDefined(typeof(BankAccountOwnerType), request.OwnerType))
            return BadRequest(new { message = "Tipo de dueño inválido." });

        // Autorizamos contra el dueño PERSISTIDO (no el del body): la edicion no cambia de dueño, y asi un
        // usuario sin permiso sobre el dueño real no puede editar mandando otro ownerType en el body.
        var existing = await _bankAccountService.GetByPublicIdAsync(publicId, cancellationToken);
        if (existing is null)
            return NotFound();

        if (!await CanWriteAsync(existing.OwnerType, cancellationToken))
            return Forbid();

        try
        {
            var updated = await _bankAccountService.UpdateAsync(publicId, request, CurrentUserId(), CurrentUserName(), cancellationToken);
            return Ok(updated);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Marca la cuenta como PRINCIPAL del dueño para su moneda (desmarca la anterior principal de ese dueño+moneda).
    /// Usa el MISMO permiso de escritura por dueño que el PUT (autoriza contra el dueño PERSISTIDO, no el del body).
    /// </summary>
    [HttpPut("{publicId:guid}/set-primary")]
    public async Task<ActionResult<BankAccountListItemDto>> SetPrimary(Guid publicId, CancellationToken cancellationToken)
    {
        var existing = await _bankAccountService.GetByPublicIdAsync(publicId, cancellationToken);
        if (existing is null)
            return NotFound();

        if (!await CanWriteAsync(existing.OwnerType, cancellationToken))
            return Forbid();

        try
        {
            var updated = await _bankAccountService.SetPrimaryAsync(publicId, CurrentUserId(), CurrentUserName(), cancellationToken);
            return Ok(updated);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpDelete("{publicId:guid}")]
    public async Task<ActionResult> Delete(Guid publicId, CancellationToken cancellationToken)
    {
        var existing = await _bankAccountService.GetByPublicIdAsync(publicId, cancellationToken);
        if (existing is null)
            return NotFound();

        if (!await CanWriteAsync(existing.OwnerType, cancellationToken))
            return Forbid();

        try
        {
            await _bankAccountService.DeactivateAsync(publicId, CurrentUserId(), CurrentUserName(), cancellationToken);
            return Ok(new { Message = "Cuenta bancaria desactivada." });
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    /// <summary>
    /// Parsea el <c>ownerType</c> que llega como texto crudo en la query del listado. Acepta DOS formas, que son
    /// las que puede mandar el front: el NOMBRE del enum sin distinguir mayusculas ("Agency"/"customer"/...) y el
    /// VALOR numerico como texto ("0"/"1"/"2", que es lo que envia <c>OWNER_TYPE[...]</c> del front). Cualquier
    /// otra cosa (vacio, "abc", "99") devuelve false -> el controller responde 400 con mensaje amable.
    ///
    /// <para>Se preserva la semantica del viejo guard <c>Enum.IsDefined</c>: <c>Enum.TryParse</c> aceptaria un
    /// numerico fuera de rango ("99") como un enum sin definir, asi que despues exigimos <c>IsDefined</c> para
    /// descartarlo. Nunca se repite el valor recibido en la respuesta (anti fuga de input).</para>
    /// </summary>
    private static bool TryParseOwnerType(string? raw, out BankAccountOwnerType ownerType)
    {
        ownerType = default;

        if (string.IsNullOrWhiteSpace(raw))
            return false;

        // TryParse(ignoreCase) cubre tanto el nombre ("Customer") como el numerico en texto ("1").
        if (!Enum.TryParse(raw.Trim(), ignoreCase: true, out BankAccountOwnerType parsed))
            return false;

        // TryParse acepta numericos fuera de rango (ej. "99"): los rechazamos para conservar el guard original.
        if (!Enum.IsDefined(typeof(BankAccountOwnerType), parsed))
            return false;

        ownerType = parsed;
        return true;
    }

    // ============================================================
    // Autorizacion por dueño (runtime). Admin bypassea siempre.
    // ============================================================

    private async Task<bool> CanReadAsync(BankAccountOwnerType ownerType, CancellationToken cancellationToken)
    {
        if (User.IsInRole("Admin"))
            return true;

        var permission = BankAccountAuthorization.RequiredReadPermission(ownerType);
        return await HasPermissionAsync(permission, cancellationToken);
    }

    private async Task<bool> CanWriteAsync(BankAccountOwnerType ownerType, CancellationToken cancellationToken)
    {
        if (User.IsInRole("Admin"))
            return true;

        // null = no hay permiso que habilite la escritura (caso Agency): solo Admin (ya descartado arriba).
        var permission = BankAccountAuthorization.RequiredWritePermission(ownerType);
        if (permission is null)
            return false;

        return await HasPermissionAsync(permission, cancellationToken);
    }

    private async Task<bool> HasPermissionAsync(string permission, CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return false;

        var permissions = await _permissionResolver.GetPermissionsAsync(userId, cancellationToken);
        return permissions.Contains(permission);
    }

    private string CurrentUserId() =>
        User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "System";

    private string? CurrentUserName() =>
        User.FindFirst("FullName")?.Value ?? User.FindFirstValue(ClaimTypes.Name);
}
