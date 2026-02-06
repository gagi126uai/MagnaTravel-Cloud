using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TravelApi.Data;
using TravelApi.Models;

namespace TravelApi.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class SuppliersController : ControllerBase
{
    private readonly AppDbContext _dbContext;

    public SuppliersController(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<Supplier>>> GetSuppliers(CancellationToken cancellationToken)
    {
        return await _dbContext.Suppliers
            .AsNoTracking()
            .OrderBy(s => s.Name)
            .ToListAsync(cancellationToken);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<Supplier>> GetSupplier(int id, CancellationToken cancellationToken)
    {
        var supplier = await _dbContext.Suppliers.FindAsync(new object[] { id }, cancellationToken);

        if (supplier is null)
        {
            return NotFound();
        }

        return supplier;
    }

    [HttpPost]
    public async Task<ActionResult<Supplier>> CreateSupplier(Supplier supplier, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(supplier.Name))
        {
            return BadRequest("El nombre del proveedor es requerido.");
        }

        _dbContext.Suppliers.Add(supplier);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return CreatedAtAction(nameof(GetSupplier), new { id = supplier.Id }, supplier);
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<Supplier>> UpdateSupplier(int id, Supplier supplier, CancellationToken cancellationToken)
    {
        if (id != supplier.Id)
        {
            return BadRequest("ID mismatch");
        }

        var existing = await _dbContext.Suppliers.FindAsync(new object[] { id }, cancellationToken);
        if (existing is null)
        {
            return NotFound();
        }

        existing.Name = supplier.Name;
        existing.ContactName = supplier.ContactName;
        existing.Email = supplier.Email;
        existing.Phone = supplier.Phone;
        existing.IsActive = supplier.IsActive;
        // CurrentBalance is usually updated via payments/bills workflow, but allowing edit for now
        existing.CurrentBalance = supplier.CurrentBalance;

        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(existing);
    }

    /// <summary>
    /// Cuenta corriente del proveedor: servicios comprados, pagos realizados, saldo
    /// </summary>
    [HttpGet("{id:int}/account")]
    public async Task<ActionResult> GetSupplierAccount(int id, CancellationToken cancellationToken)
    {
        var supplier = await _dbContext.Suppliers
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

        if (supplier is null)
        {
            return NotFound("Proveedor no encontrado");
        }

        // Servicios comprados a este proveedor
        var services = await _dbContext.Reservations
            .AsNoTracking()
            .Where(r => r.SupplierId == id)
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => new
            {
                r.Id,
                r.ServiceType,
                r.Description,
                r.ConfirmationNumber,
                r.NetCost,
                r.SalePrice,
                r.DepartureDate,
                r.Status,
                FileNumber = r.TravelFile != null ? r.TravelFile.FileNumber : null,
                FileName = r.TravelFile != null ? r.TravelFile.Name : null
            })
            .ToListAsync(cancellationToken);

        // Pagos realizados a este proveedor
        var payments = await _dbContext.SupplierPayments
            .AsNoTracking()
            .Where(p => p.SupplierId == id)
            .OrderByDescending(p => p.PaidAt)
            .Select(p => new
            {
                p.Id,
                p.Amount,
                p.Method,
                p.PaidAt,
                p.Reference,
                p.Notes,
                FileNumber = p.TravelFile != null ? p.TravelFile.FileNumber : null
            })
            .ToListAsync(cancellationToken);

        // Totales
        var totalPurchases = services.Sum(s => s.NetCost);
        var totalPaid = payments.Sum(p => p.Amount);
        var balance = totalPurchases - totalPaid; // Positivo = le debemos

        return Ok(new
        {
            Supplier = new
            {
                supplier.Id,
                supplier.Name,
                supplier.ContactName,
                supplier.Email,
                supplier.Phone,
                supplier.TaxId
            },
            Services = services,
            Payments = payments,
            Summary = new
            {
                TotalPurchases = totalPurchases,
                TotalPaid = totalPaid,
                Balance = balance, // Positivo = le debemos
                ServiceCount = services.Count,
                PaymentCount = payments.Count
            }
        });
    }

    /// <summary>
    /// Registrar un pago al proveedor (egreso)
    /// </summary>
    [HttpPost("{id:int}/payments")]
    public async Task<ActionResult> AddSupplierPayment(int id, [FromBody] SupplierPaymentRequest request, CancellationToken cancellationToken)
    {
        var supplier = await _dbContext.Suppliers.FindAsync(new object[] { id }, cancellationToken);
        if (supplier is null)
        {
            return NotFound("Proveedor no encontrado");
        }

        if (request.Amount <= 0)
        {
            return BadRequest("El monto debe ser mayor a 0");
        }

        var payment = new SupplierPayment
        {
            SupplierId = id,
            Amount = request.Amount,
            Method = request.Method ?? "Transfer",
            Reference = request.Reference,
            Notes = request.Notes,
            TravelFileId = request.TravelFileId,
            ReservationId = request.ReservationId,
            PaidAt = DateTime.UtcNow
        };

        _dbContext.SupplierPayments.Add(payment);
        
        // Actualizar saldo del proveedor (reducir deuda)
        supplier.CurrentBalance -= request.Amount;
        
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new { Message = "Pago registrado correctamente", PaymentId = payment.Id });
    }

    /// <summary>
    /// Historial de pagos al proveedor
    /// </summary>
    [HttpGet("{id:int}/payments")]
    public async Task<ActionResult> GetSupplierPayments(int id, CancellationToken cancellationToken)
    {
        var payments = await _dbContext.SupplierPayments
            .AsNoTracking()
            .Where(p => p.SupplierId == id)
            .OrderByDescending(p => p.PaidAt)
            .Select(p => new
            {
                p.Id,
                p.Amount,
                p.Method,
                p.PaidAt,
                p.Reference,
                p.Notes,
                FileNumber = p.TravelFile != null ? p.TravelFile.FileNumber : null,
                FileName = p.TravelFile != null ? p.TravelFile.Name : null
            })
            .ToListAsync(cancellationToken);

        return Ok(payments);
    }
}

public record SupplierPaymentRequest(
    decimal Amount, 
    string? Method, 
    string? Reference, 
    string? Notes,
    int? TravelFileId,
    int? ReservationId
);
