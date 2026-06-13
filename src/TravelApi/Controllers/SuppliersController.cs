#pragma warning disable CS8601, CS8602, CS8604, CS8618
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Authorization;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Controllers;

// B1.15 Fase 0' (CODE-09): hotfix de seguridad. Antes el controller era
// solo [Authorize] — cualquier autenticado podia crear, modificar o borrar
// proveedores y operar pagos egresos. Ahora cada endpoint exige permiso
// especifico (proveedores.view/edit/edit_fiscal o tesoreria.supplier_payments).
[Authorize]
[ApiController]
[Route("api/[controller]")]
public class SuppliersController : ControllerBase
{
    private readonly ISupplierService _supplierService;
    private readonly IEntityReferenceResolver _entityReferenceResolver;

    public SuppliersController(ISupplierService supplierService, IEntityReferenceResolver entityReferenceResolver)
    {
        _supplierService = supplierService;
        _entityReferenceResolver = entityReferenceResolver;
    }

    [HttpGet]
    [RequirePermission(Permissions.ProveedoresView)]
    public async Task<ActionResult<PagedResponse<SupplierListItemDto>>> GetSuppliers([FromQuery] SupplierListQuery query, CancellationToken cancellationToken)
    {
        return Ok(await _supplierService.GetSuppliersAsync(query, cancellationToken));
    }

    [HttpPost("recalculate-all")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult> RecalculateAllBalances(CancellationToken cancellationToken)
    {
        await _supplierService.RecalculateAllBalancesAsync(cancellationToken);
        return Ok(new { Message = "Saldos recalculados" });
    }

    [HttpGet("{publicIdOrLegacyId}")]
    [RequirePermission(Permissions.ProveedoresView)]
    public async Task<ActionResult<Supplier>> GetSupplier(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Supplier>(publicIdOrLegacyId, cancellationToken);
            var supplier = await _supplierService.GetSupplierAsync(id, cancellationToken);
            return Ok(ToSupplierResponse(supplier));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPost]
    [RequirePermission(Permissions.ProveedoresEdit)]
    public async Task<ActionResult<Supplier>> CreateSupplier(SupplierUpsertRequest supplier, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _supplierService.CreateSupplierAsync(MapSupplier(supplier), cancellationToken);
            return CreatedAtAction(nameof(GetSupplier), new { publicIdOrLegacyId = result.PublicId }, ToSupplierResponse(result));
        }
        catch (ArgumentException)
        {
            return BadRequest(new { message = "No se pudo crear el proveedor." });
        }
    }

    [HttpPut("{publicIdOrLegacyId}")]
    [RequirePermission(Permissions.ProveedoresEdit)]
    public async Task<ActionResult<Supplier>> UpdateSupplier(string publicIdOrLegacyId, SupplierUpsertRequest supplier, CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Supplier>(publicIdOrLegacyId, cancellationToken);
            var result = await _supplierService.UpdateSupplierAsync(id, MapSupplier(supplier), cancellationToken);
            return Ok(ToSupplierResponse(result));
        }
        catch (ArgumentException)
        {
            return BadRequest(new { message = "No se pudo actualizar el proveedor." });
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            // C29: guard de desactivacion (IsActive: true -> false con reservas activas).
            // B1.15 Fase 0' (CODE-13): guard fiscal (TaxId/TaxCondition con CAE viva).
            // Ambos casos reflejan "estado actual incompatible con la operacion" → 409.
            return Conflict(new { message = ex.Message });
        }
    }

    [HttpDelete("{publicIdOrLegacyId}")]
    [RequirePermission(Permissions.ProveedoresEdit)]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult> DeleteSupplier(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Supplier>(publicIdOrLegacyId, cancellationToken);
            await _supplierService.DeleteSupplierAsync(id, cancellationToken);
            return Ok(new { Message = "Proveedor eliminado" });
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    // C24: el endpoint DELETE /suppliers/{id}/force fue eliminado por completo.
    // Reemplazado por el flujo unico DeleteSupplier que valida referencias antes
    // de borrar. Si el cliente recibia 200 con "(forzado)", ahora recibe 404 al
    // pegarle al path; eso es intencional — antes podia dejar la BD inconsistente.

    [HttpGet("{publicIdOrLegacyId}/account")]
    [RequirePermission(Permissions.ProveedoresView)]
    public async Task<ActionResult<SupplierAccountOverviewDto>> GetSupplierAccount(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Supplier>(publicIdOrLegacyId, cancellationToken);
            return Ok(await _supplierService.GetSupplierAccountOverviewAsync(id, cancellationToken));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpGet("{publicIdOrLegacyId}/account/services")]
    [RequirePermission(Permissions.ProveedoresView)]
    public async Task<ActionResult<PagedResponse<SupplierAccountServiceListItemDto>>> GetSupplierAccountServices(
        string publicIdOrLegacyId,
        [FromQuery] SupplierAccountServicesQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Supplier>(publicIdOrLegacyId, cancellationToken);
            return Ok(await _supplierService.GetSupplierAccountServicesAsync(id, query, cancellationToken));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    // Auditoria ERP hallazgo #4 (2026-06-12): deuda con el proveedor DESGLOSADA POR EXPEDIENTE (reserva).
    // Mismo permiso que el resto de la cuenta del proveedor (proveedores.view): la estructura (que reservas,
    // que monedas) es visible con ese permiso; los MONTOS los enmascara el servicio si falta cobranzas.see_cost.
    [HttpGet("{publicIdOrLegacyId}/account/debt-by-reserva")]
    [RequirePermission(Permissions.ProveedoresView)]
    public async Task<ActionResult<SupplierDebtByReservaDto>> GetSupplierDebtByReserva(
        string publicIdOrLegacyId,
        CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Supplier>(publicIdOrLegacyId, cancellationToken);
            return Ok(await _supplierService.GetSupplierDebtByReservaAsync(id, cancellationToken));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpGet("{publicIdOrLegacyId}/account/payments")]
    [RequirePermission(Permissions.TesoreriaSupplierPayments)]
    public async Task<ActionResult<PagedResponse<SupplierPaymentDto>>> GetSupplierAccountPayments(
        string publicIdOrLegacyId,
        [FromQuery] SupplierAccountPaymentsQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Supplier>(publicIdOrLegacyId, cancellationToken);
            return Ok(await _supplierService.GetSupplierAccountPaymentsAsync(id, query, cancellationToken));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPost("{publicIdOrLegacyId}/payments")]
    [RequirePermission(Permissions.TesoreriaSupplierPayments)]
    public async Task<ActionResult> AddSupplierPayment(string publicIdOrLegacyId, [FromBody] SupplierPaymentRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Supplier>(publicIdOrLegacyId, cancellationToken);
            var paymentPublicId = await _supplierService.AddSupplierPaymentAsync(id, request, cancellationToken);
            return Ok(new { Message = "Pago registrado correctamente", PaymentPublicId = paymentPublicId });
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ArgumentException)
        {
            return BadRequest(new { message = "No se pudo registrar el pago al proveedor." });
        }
        catch (InvalidOperationException)
        {
            return BadRequest(new { message = "No se pudo registrar el pago al proveedor." });
        }
    }

    [HttpPut("{publicIdOrLegacyId}/payments/{paymentPublicIdOrLegacyId}")]
    [RequirePermission(Permissions.TesoreriaSupplierPayments)]
    public async Task<ActionResult> UpdateSupplierPayment(string publicIdOrLegacyId, string paymentPublicIdOrLegacyId, [FromBody] SupplierPaymentRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Supplier>(publicIdOrLegacyId, cancellationToken);
            var paymentId = await _entityReferenceResolver.ResolveRequiredIdAsync<SupplierPayment>(paymentPublicIdOrLegacyId, cancellationToken);
            await _supplierService.UpdateSupplierPaymentAsync(id, paymentId, request, cancellationToken);
            return Ok(new { Message = "Pago actualizado correctamente" });
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ArgumentException)
        {
            return BadRequest(new { message = "No se pudo actualizar el pago al proveedor." });
        }
        catch (InvalidOperationException)
        {
            return BadRequest(new { message = "No se pudo actualizar el pago al proveedor." });
        }
    }

    [HttpDelete("{publicIdOrLegacyId}/payments/{paymentPublicIdOrLegacyId}")]
    [RequirePermission(Permissions.TesoreriaSupplierPayments)]
    public async Task<ActionResult> DeleteSupplierPayment(string publicIdOrLegacyId, string paymentPublicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Supplier>(publicIdOrLegacyId, cancellationToken);
            var paymentId = await _entityReferenceResolver.ResolveRequiredIdAsync<SupplierPayment>(paymentPublicIdOrLegacyId, cancellationToken);
            await _supplierService.DeleteSupplierPaymentAsync(id, paymentId, cancellationToken);
            return Ok(new { Message = "Pago eliminado y saldo restaurado" });
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    [HttpGet("{publicIdOrLegacyId}/payments")]
    [RequirePermission(Permissions.TesoreriaSupplierPayments)]
    public async Task<ActionResult> GetSupplierPayments(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Supplier>(publicIdOrLegacyId, cancellationToken);
        var payments = await _supplierService.GetSupplierPaymentsHistoryAsync(id, cancellationToken);
        return Ok(payments);
    }

    private static object ToSupplierResponse(Supplier supplier) => new
    {
        supplier.PublicId,
        supplier.Name,
        supplier.ContactName,
        supplier.Email,
        supplier.Phone,
        supplier.TaxId,
        supplier.TaxCondition,
        supplier.Address,
        supplier.IsActive,
        supplier.CurrentBalance,
        supplier.CreatedAt
    };

    private static Supplier MapSupplier(SupplierUpsertRequest request) => new()
    {
        Name = request.Name,
        ContactName = request.ContactName,
        Email = request.Email,
        Phone = request.Phone,
        TaxId = request.TaxId,
        TaxCondition = request.TaxCondition,
        Address = request.Address,
        IsActive = request.IsActive
    };
}

public record SupplierUpsertRequest(
    string Name,
    string? ContactName,
    string? Email,
    string? Phone,
    string? TaxId,
    string? TaxCondition,
    string? Address,
    bool IsActive = true);
