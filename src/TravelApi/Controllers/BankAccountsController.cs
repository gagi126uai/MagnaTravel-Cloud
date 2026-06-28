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

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<BankAccountListItemDto>>> List(
        [FromQuery] BankAccountOwnerType ownerType,
        [FromQuery] int ownerId,
        CancellationToken cancellationToken)
    {
        // El binding de un enum desde la query acepta CUALQUIER int (ej. 99) sin error. Validamos que sea un
        // valor real ANTES de mapearlo a permiso, si no caeria en un 500 mas abajo. 400 es la respuesta correcta.
        if (!Enum.IsDefined(typeof(BankAccountOwnerType), ownerType))
            return BadRequest(new { message = "Tipo de dueño inválido." });

        if (!await CanReadAsync(ownerType, cancellationToken))
            return Forbid();

        var accounts = await _bankAccountService.ListAsync(ownerType, ownerId, cancellationToken);
        return Ok(accounts);
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
