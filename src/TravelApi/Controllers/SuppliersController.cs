#pragma warning disable CS8601, CS8602, CS8604, CS8618
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class SuppliersController : ControllerBase
{
    private readonly ISupplierService _supplierService;
    private readonly EntityReferenceResolver _entityReferenceResolver;

    public SuppliersController(ISupplierService supplierService, EntityReferenceResolver entityReferenceResolver)
    {
        _supplierService = supplierService;
        _entityReferenceResolver = entityReferenceResolver;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<Supplier>>> GetSuppliers(CancellationToken cancellationToken)
    {
        var suppliers = await _supplierService.GetSuppliersAsync(cancellationToken);
        return Ok(suppliers.Select(ToSupplierResponse));
    }

    [HttpPost("recalculate-all")]
    public async Task<ActionResult> RecalculateAllBalances(CancellationToken cancellationToken)
    {
        await _supplierService.RecalculateAllBalancesAsync(cancellationToken);
        return Ok(new { Message = "Saldos recalculados" });
    }

    [HttpGet("{publicIdOrLegacyId}")]
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
    public async Task<ActionResult<Supplier>> CreateSupplier(Supplier supplier, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _supplierService.CreateSupplierAsync(supplier, cancellationToken);
            return CreatedAtAction(nameof(GetSupplier), new { publicIdOrLegacyId = result.PublicId }, ToSupplierResponse(result));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpPut("{publicIdOrLegacyId}")]
    public async Task<ActionResult<Supplier>> UpdateSupplier(string publicIdOrLegacyId, Supplier supplier, CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Supplier>(publicIdOrLegacyId, cancellationToken);
            var result = await _supplierService.UpdateSupplierAsync(id, supplier, cancellationToken);
            return Ok(ToSupplierResponse(result));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpDelete("{publicIdOrLegacyId}")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult> DeleteSupplier(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Supplier>(publicIdOrLegacyId, cancellationToken);
            await _supplierService.DeleteSupplierAsync(id, cancellationToken);
            return Ok(new { Message = "Proveedor eliminado" });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpDelete("{publicIdOrLegacyId}/force")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult> ForceDeleteSupplier(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Supplier>(publicIdOrLegacyId, cancellationToken);
            await _supplierService.ForceDeleteSupplierAsync(id, cancellationToken);
            return Ok(new { Message = "Proveedor eliminado (forzado)" });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
    }

    [HttpGet("{publicIdOrLegacyId}/account")]
    public async Task<ActionResult> GetSupplierAccount(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Supplier>(publicIdOrLegacyId, cancellationToken);
            var accountDto = await _supplierService.GetSupplierAccountAsync(id, cancellationToken);
            return Ok(accountDto);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
    }

    [HttpPost("{publicIdOrLegacyId}/payments")]
    public async Task<ActionResult> AddSupplierPayment(string publicIdOrLegacyId, [FromBody] SupplierPaymentRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Supplier>(publicIdOrLegacyId, cancellationToken);
            var paymentPublicId = await _supplierService.AddSupplierPaymentAsync(id, request, cancellationToken);
            return Ok(new { Message = "Pago registrado correctamente", PaymentPublicId = paymentPublicId });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpPut("{publicIdOrLegacyId}/payments/{paymentPublicIdOrLegacyId}")]
    public async Task<ActionResult> UpdateSupplierPayment(string publicIdOrLegacyId, string paymentPublicIdOrLegacyId, [FromBody] SupplierPaymentRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Supplier>(publicIdOrLegacyId, cancellationToken);
            var paymentId = await _entityReferenceResolver.ResolveRequiredIdAsync<SupplierPayment>(paymentPublicIdOrLegacyId, cancellationToken);
            await _supplierService.UpdateSupplierPaymentAsync(id, paymentId, request, cancellationToken);
            return Ok(new { Message = "Pago actualizado correctamente" });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpDelete("{publicIdOrLegacyId}/payments/{paymentPublicIdOrLegacyId}")]
    public async Task<ActionResult> DeleteSupplierPayment(string publicIdOrLegacyId, string paymentPublicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Supplier>(publicIdOrLegacyId, cancellationToken);
            var paymentId = await _entityReferenceResolver.ResolveRequiredIdAsync<SupplierPayment>(paymentPublicIdOrLegacyId, cancellationToken);
            await _supplierService.DeleteSupplierPaymentAsync(id, paymentId, cancellationToken);
            return Ok(new { Message = "Pago eliminado y saldo restaurado" });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
    }

    [HttpGet("{publicIdOrLegacyId}/payments")]
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
}
