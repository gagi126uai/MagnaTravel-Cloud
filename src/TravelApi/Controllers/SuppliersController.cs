#pragma warning disable CS8601, CS8602, CS8604, CS8618
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;

namespace TravelApi.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class SuppliersController : ControllerBase
{
    private readonly ISupplierService _supplierService;

    public SuppliersController(ISupplierService supplierService)
    {
        _supplierService = supplierService;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<Supplier>>> GetSuppliers(CancellationToken cancellationToken)
    {
        var suppliers = await _supplierService.GetSuppliersAsync(cancellationToken);
        return Ok(suppliers);
    }

    [HttpPost("recalculate-all")]
    public async Task<ActionResult> RecalculateAllBalances(CancellationToken cancellationToken)
    {
        await _supplierService.RecalculateAllBalancesAsync(cancellationToken);
        return Ok(new { Message = "Saldos recalculados" });
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<Supplier>> GetSupplier(int id, CancellationToken cancellationToken)
    {
        try
        {
            var supplier = await _supplierService.GetSupplierAsync(id, cancellationToken);
            return Ok(supplier);
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
            return CreatedAtAction(nameof(GetSupplier), new { id = result.Id }, result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<Supplier>> UpdateSupplier(int id, Supplier supplier, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _supplierService.UpdateSupplierAsync(id, supplier, cancellationToken);
            return Ok(result);
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

    [HttpDelete("{id:int}")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult> DeleteSupplier(int id, CancellationToken cancellationToken)
    {
        try
        {
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

    [HttpDelete("{id:int}/force")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult> ForceDeleteSupplier(int id, CancellationToken cancellationToken)
    {
        try
        {
            await _supplierService.ForceDeleteSupplierAsync(id, cancellationToken);
            return Ok(new { Message = "Proveedor eliminado (forzado)" });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
    }

    [HttpGet("{id:int}/account")]
    public async Task<ActionResult> GetSupplierAccount(int id, CancellationToken cancellationToken)
    {
        try
        {
            var accountDto = await _supplierService.GetSupplierAccountAsync(id, cancellationToken);
            return Ok(accountDto);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
    }

    [HttpPost("{id:int}/payments")]
    public async Task<ActionResult> AddSupplierPayment(int id, [FromBody] SupplierPaymentRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var paymentId = await _supplierService.AddSupplierPaymentAsync(id, request, cancellationToken);
            return Ok(new { Message = "Pago registrado correctamente", PaymentId = paymentId });
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

    [HttpPut("{id:int}/payments/{paymentId:int}")]
    public async Task<ActionResult> UpdateSupplierPayment(int id, int paymentId, [FromBody] SupplierPaymentRequest request, CancellationToken cancellationToken)
    {
        try
        {
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

    [HttpDelete("{id:int}/payments/{paymentId:int}")]
    public async Task<ActionResult> DeleteSupplierPayment(int id, int paymentId, CancellationToken cancellationToken)
    {
        try
        {
            await _supplierService.DeleteSupplierPaymentAsync(id, paymentId, cancellationToken);
            return Ok(new { Message = "Pago eliminado y saldo restaurado" });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
    }

    [HttpGet("{id:int}/payments")]
    public async Task<ActionResult> GetSupplierPayments(int id, CancellationToken cancellationToken)
    {
        var payments = await _supplierService.GetSupplierPaymentsHistoryAsync(id, cancellationToken);
        return Ok(payments);
    }
}
