using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TravelApi.Data;
using TravelApi.Models;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/customers")]
[Authorize]
public class CustomersController : ControllerBase
{
    private readonly AppDbContext _dbContext;

    public CustomersController(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<object>>> GetCustomers([FromQuery] bool includeInactive = false, CancellationToken cancellationToken = default)
    {
        var query = _dbContext.Customers.AsNoTracking();
        
        if (!includeInactive)
        {
            query = query.Where(c => c.IsActive);
        }

        var customers = await query
            .Include(c => c.TravelFiles)
            .OrderBy(customer => customer.FullName)
            .Select(c => new 
            {
                c.Id,
                c.FullName,
                c.Email,
                c.Phone,
                c.DocumentNumber,
                c.Address,
                c.Notes,
                c.TaxId,
                c.CreditLimit,
                c.IsActive,
                // Live Balance Calculation: Sum of balance of all non-cancelled files
                CurrentBalance = c.TravelFiles
                    .Where(f => f.Status != FileStatus.Cancelled && f.Status != FileStatus.Budget)
                    .Sum(f => f.Balance)
            })
            .ToListAsync(cancellationToken);

        return Ok(customers);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<object>> GetCustomer(int id, CancellationToken cancellationToken)
    {
        var customer = await _dbContext.Customers
            .AsNoTracking()
            .Include(c => c.TravelFiles)
            .FirstOrDefaultAsync(found => found.Id == id, cancellationToken);

        if (customer is null)
        {
            return NotFound();
        }

        return Ok(new 
        {
            customer.Id,
            customer.FullName,
            customer.Email,
            customer.Phone,
            customer.DocumentNumber,
            customer.Address,
            customer.Notes,
            customer.TaxId,
            customer.CreditLimit,
            customer.IsActive,
            CurrentBalance = customer.TravelFiles
                .Where(f => f.Status != FileStatus.Cancelled && f.Status != FileStatus.Budget)
                .Sum(f => f.Balance)
        });
    }

    [HttpPost]
    public async Task<ActionResult<Customer>> CreateCustomer(Customer customer, CancellationToken cancellationToken)
    {
        try
        {
            customer.IsActive = true; // Default to active
            _dbContext.Customers.Add(customer);
            await _dbContext.SaveChangesAsync(cancellationToken);

            return CreatedAtAction(nameof(GetCustomer), new { id = customer.Id }, customer);
        }
        catch (DbUpdateException ex)
        {
            // Simple duplication check placeholder
            return BadRequest($"Error creando cliente (Posible duplicado de Documento/Email): {ex.InnerException?.Message ?? ex.Message}");
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error interno: {ex.Message}");
        }
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<Customer>> UpdateCustomer(int id, Customer customer, CancellationToken cancellationToken)
    {
        try
        {
            if (id != customer.Id) return BadRequest("ID mismatch");

            var existing = await _dbContext.Customers.FindAsync(new object[] { id }, cancellationToken);
            if (existing is null) return NotFound();

            existing.FullName = customer.FullName;
            existing.Email = customer.Email;
            existing.Phone = customer.Phone;
            existing.DocumentNumber = customer.DocumentNumber;
            existing.Address = customer.Address;
            existing.Notes = customer.Notes;
            existing.IsActive = customer.IsActive; // Allow updating status
            
            // Retail Pivot: Update Financials
            existing.TaxId = customer.TaxId;
            existing.CreditLimit = customer.CreditLimit;
            // Balance is read-only calculated field now, ignoring input

            await _dbContext.SaveChangesAsync(cancellationToken);

            return Ok(existing);
        }
        catch (DbUpdateException ex)
        {
            return BadRequest($"Error actualizando cliente: {ex.InnerException?.Message ?? ex.Message}");
        }
        catch (Exception ex)
        {
             return StatusCode(500, $"Error interno: {ex.Message}");
        }
    }

    /// <summary>
    /// Cuenta corriente del cliente: expedientes, pagos y saldo
    /// </summary>
    [HttpGet("{id:int}/account")]
    public async Task<ActionResult> GetCustomerAccount(int id, CancellationToken cancellationToken)
    {
        try
        {
            var customer = await _dbContext.Customers
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

            if (customer is null) return NotFound("Cliente no encontrado");

            // Get all travel files for this customer
            var files = await _dbContext.TravelFiles
                .AsNoTracking()
                .Where(f => f.PayerId == id)
                .OrderByDescending(f => f.CreatedAt)
                .Select(f => new
                {
                    f.Id,
                    f.FileNumber,
                    f.Name,
                    f.Status,
                    f.TotalSale,
                    f.Balance,
                    f.CreatedAt,
                    f.StartDate,
                    Paid = f.TotalSale - f.Balance
                })
                .ToListAsync(cancellationToken);

            // Get all payments for this customer's files
            var fileIds = files.Select(f => f.Id).ToList();
            var payments = await _dbContext.Payments
                .AsNoTracking()
                .Where(p => p.TravelFileId != null && fileIds.Contains(p.TravelFileId.Value))
                .OrderByDescending(p => p.PaidAt)
                .Select(p => new
                {
                    p.Id,
                    p.Amount,
                    p.Method,
                    PaymentDate = p.PaidAt,
                    p.Notes,
                    TravelFileId = p.TravelFileId,
                    FileNumber = p.TravelFile != null ? p.TravelFile.FileNumber : null,
                    FileName = p.TravelFile != null ? p.TravelFile.Name : null
                })
                .ToListAsync(cancellationToken);

            // Calculate totals
            var totalSales = files.Sum(f => f.TotalSale);
            var totalPaid = files.Sum(f => f.Paid);
            var totalBalance = files.Sum(f => f.Balance);

            return Ok(new
            {
                Customer = new
                {
                    customer.Id,
                    customer.FullName,
                    customer.Email,
                    customer.Phone,
                    customer.TaxId,
                    customer.CreditLimit,
                    customer.CurrentBalance
                },
                Files = files,
                Payments = payments,
                Summary = new
                {
                    TotalSales = totalSales,
                    TotalPaid = totalPaid,
                    TotalBalance = totalBalance,
                    FileCount = files.Count(),
                    PaymentCount = payments.Count()
                }
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error obteniendo cuenta corriente: {ex.Message}");
        }
    }
}
