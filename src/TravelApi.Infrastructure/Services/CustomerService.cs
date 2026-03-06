using Microsoft.EntityFrameworkCore;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

public class CustomerService : ICustomerService
{
    private readonly AppDbContext _dbContext;

    public CustomerService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IEnumerable<object>> GetCustomersAsync(bool includeInactive, CancellationToken cancellationToken)
    {
        var query = _dbContext.Customers.AsNoTracking();
        
        if (!includeInactive)
        {
            query = query.Where(c => c.IsActive);
        }

        return await query
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
                CurrentBalance = c.TravelFiles
                    .Where(f => f.Status != FileStatus.Cancelled && f.Status != FileStatus.Budget)
                    .Sum(f => f.Balance)
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<object> GetCustomerAsync(int id, CancellationToken cancellationToken)
    {
        var customer = await _dbContext.Customers
            .AsNoTracking()
            .Include(c => c.TravelFiles)
            .FirstOrDefaultAsync(found => found.Id == id, cancellationToken);

        if (customer == null) throw new KeyNotFoundException("Cliente no encontrado");

        return new 
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
        };
    }

    public async Task<Customer> CreateCustomerAsync(Customer customer, CancellationToken cancellationToken)
    {
        customer.IsActive = true; 
        _dbContext.Customers.Add(customer);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return customer;
    }

    public async Task<Customer> UpdateCustomerAsync(int id, Customer customer, CancellationToken cancellationToken)
    {
        if (id != customer.Id) throw new ArgumentException("ID mismatch");

        var existing = await _dbContext.Customers.FindAsync(new object[] { id }, cancellationToken);
        if (existing == null) throw new KeyNotFoundException("Cliente no encontrado");

        existing.FullName = customer.FullName;
        existing.Email = customer.Email;
        existing.Phone = customer.Phone;
        existing.DocumentNumber = customer.DocumentNumber;
        existing.Address = customer.Address;
        existing.Notes = customer.Notes;
        existing.IsActive = customer.IsActive; 
        
        existing.TaxId = customer.TaxId;
        existing.CreditLimit = customer.CreditLimit;

        await _dbContext.SaveChangesAsync(cancellationToken);
        return existing;
    }

    public async Task<CustomerAccountDto> GetCustomerAccountAsync(int id, CancellationToken cancellationToken)
    {
        var customer = await _dbContext.Customers
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

        if (customer == null) throw new KeyNotFoundException("Cliente no encontrado");

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

        var fileIds = files.Select(f => f.Id).ToList();
        var payments = await _dbContext.Payments
            .AsNoTracking()
            .Where(p => p.TravelFileId != null && fileIds.Contains(p.TravelFileId.Value) && !p.IsDeleted)
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

        var totalSales = files.Sum(f => f.TotalSale);
        var totalPaid = files.Sum(f => f.Paid);
        var totalBalance = files.Sum(f => f.Balance);

        return new CustomerAccountDto
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
            Files = files.Cast<object>(),
            Payments = payments.Cast<object>(),
            Summary = new
            {
                TotalSales = totalSales,
                TotalPaid = totalPaid,
                TotalBalance = totalBalance,
                FileCount = files.Count,
                PaymentCount = payments.Count
            }
        };
    }
}
