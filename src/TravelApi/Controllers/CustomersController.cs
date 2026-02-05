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
    public async Task<ActionResult<IEnumerable<Customer>>> GetCustomers(CancellationToken cancellationToken)
    {
        var customers = await _dbContext.Customers
            .AsNoTracking()
            .OrderBy(customer => customer.FullName)
            .ToListAsync(cancellationToken);

        return Ok(customers);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<Customer>> GetCustomer(int id, CancellationToken cancellationToken)
    {
        var customer = await _dbContext.Customers
            .AsNoTracking()
            .FirstOrDefaultAsync(found => found.Id == id, cancellationToken);

        if (customer is null)
        {
            return NotFound();
        }

        return Ok(customer);
    }

    [HttpPost]
    public async Task<ActionResult<Customer>> CreateCustomer(Customer customer, CancellationToken cancellationToken)
    {
        _dbContext.Customers.Add(customer);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return CreatedAtAction(nameof(GetCustomer), new { id = customer.Id }, customer);
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<Customer>> UpdateCustomer(int id, Customer customer, CancellationToken cancellationToken)
    {
        if (id != customer.Id)
        {
            return BadRequest("ID mismatch");
        }

        var existing = await _dbContext.Customers.FindAsync(new object[] { id }, cancellationToken);
        if (existing is null)
        {
            return NotFound();
        }

        existing.FullName = customer.FullName;
        existing.Email = customer.Email;
        existing.Phone = customer.Phone;
        existing.DocumentNumber = customer.DocumentNumber;
        existing.Address = customer.Address;
        existing.Notes = customer.Notes;
        
        // Retail Pivot: Update Financials
        existing.TaxId = customer.TaxId;
        existing.CreditLimit = customer.CreditLimit;
        existing.CurrentBalance = customer.CurrentBalance;

        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(existing);
    }

    /// <summary>
    /// Cuenta corriente del cliente: expedientes, pagos y saldo
    /// </summary>
    [HttpGet("{id:int}/account")]
    public async Task<ActionResult> GetCustomerAccount(int id, CancellationToken cancellationToken)
    {
        var customer = await _dbContext.Customers
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

        if (customer is null)
        {
            return NotFound("Cliente no encontrado");
        }

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
            .OrderByDescending(p => p.PaymentDate)
            .Select(p => new
            {
                p.Id,
                p.Amount,
                p.Method,
                p.PaymentDate,
                p.Notes,
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
                FileCount = files.Count,
                PaymentCount = payments.Count
            }
        });
    }
}
