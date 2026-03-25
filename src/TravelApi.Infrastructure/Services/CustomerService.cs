using Microsoft.EntityFrameworkCore;
using TravelApi.Application.DTOs;
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

    public async Task<PagedResponse<CustomerListItemDto>> GetCustomersAsync(CustomerListQuery query, CancellationToken cancellationToken)
    {
        var customersQuery = ApplyCustomerSearch(_dbContext.Customers.AsNoTracking(), query.Search);

        if (!query.IncludeInactive)
        {
            customersQuery = customersQuery.Where(customer => customer.IsActive);
        }

        customersQuery = ApplyCustomerOrdering(customersQuery, query);

        var projectedQuery = customersQuery.Select(customer => new CustomerListItemDto
        {
            PublicId = customer.PublicId,
            FullName = customer.FullName,
            Email = customer.Email,
            Phone = customer.Phone,
            DocumentNumber = customer.DocumentNumber,
            Address = customer.Address,
            Notes = customer.Notes,
            TaxId = customer.TaxId,
            CreditLimit = customer.CreditLimit,
            IsActive = customer.IsActive,
            TaxConditionId = customer.TaxConditionId,
            CurrentBalance = customer.Reservas
                .Where(reserva => reserva.Status != EstadoReserva.Cancelled && reserva.Status != EstadoReserva.Budget && reserva.Status != "Archived")
                .Sum(reserva => (decimal?)reserva.Balance) ?? 0m
        });

        return await projectedQuery.ToPagedResponseAsync(query, cancellationToken);
    }

    public async Task<CustomerListItemDto> GetCustomerAsync(int id, CancellationToken cancellationToken)
    {
        var customer = await _dbContext.Customers
            .AsNoTracking()
            .Where(found => found.Id == id)
            .Select(found => new CustomerListItemDto
            {
                PublicId = found.PublicId,
                FullName = found.FullName,
                Email = found.Email,
                Phone = found.Phone,
                DocumentNumber = found.DocumentNumber,
                Address = found.Address,
                Notes = found.Notes,
                TaxId = found.TaxId,
                CreditLimit = found.CreditLimit,
                IsActive = found.IsActive,
                TaxConditionId = found.TaxConditionId,
                CurrentBalance = found.Reservas
                    .Where(reserva => reserva.Status != EstadoReserva.Cancelled && reserva.Status != EstadoReserva.Budget && reserva.Status != "Archived")
                    .Sum(reserva => (decimal?)reserva.Balance) ?? 0m
            })
            .FirstOrDefaultAsync(cancellationToken);

        return customer ?? throw new KeyNotFoundException("Cliente no encontrado");
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
        existing.TaxConditionId = customer.TaxConditionId;
        existing.TaxCondition = customer.TaxCondition;

        await _dbContext.SaveChangesAsync(cancellationToken);
        return existing;
    }

    public async Task<CustomerAccountOverviewDto> GetCustomerAccountOverviewAsync(int id, CancellationToken cancellationToken)
    {
        var customer = await _dbContext.Customers
            .AsNoTracking()
            .Where(entity => entity.Id == id)
            .Select(entity => new CustomerAccountCustomerDto
            {
                PublicId = entity.PublicId,
                FullName = entity.FullName,
                Email = entity.Email,
                Phone = entity.Phone,
                TaxId = entity.TaxId,
                CreditLimit = entity.CreditLimit,
                CurrentBalance = entity.Reservas
                    .Where(reserva => reserva.Status != EstadoReserva.Cancelled && reserva.Status != EstadoReserva.Budget && reserva.Status != "Archived")
                    .Sum(reserva => (decimal?)reserva.Balance) ?? 0m
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (customer == null)
        {
            throw new KeyNotFoundException("Cliente no encontrado");
        }

        var reservasQuery = _dbContext.Reservas
            .AsNoTracking()
            .Where(reserva => reserva.PayerId == id);

        var summary = new CustomerAccountSummaryDto
        {
            TotalSales = await reservasQuery.SumAsync(reserva => (decimal?)reserva.TotalSale, cancellationToken) ?? 0m,
            TotalPaid = await reservasQuery.SumAsync(reserva => (decimal?)reserva.TotalPaid, cancellationToken) ?? 0m,
            TotalBalance = await reservasQuery.SumAsync(reserva => (decimal?)reserva.Balance, cancellationToken) ?? 0m,
            ReservaCount = await reservasQuery.CountAsync(cancellationToken),
            PaymentCount = await _dbContext.Payments
                .AsNoTracking()
                .CountAsync(payment => payment.Reserva != null && payment.Reserva.PayerId == id, cancellationToken),
            InvoiceCount = await _dbContext.Invoices
                .AsNoTracking()
                .CountAsync(invoice => invoice.Reserva != null && invoice.Reserva.PayerId == id, cancellationToken)
        };

        return new CustomerAccountOverviewDto
        {
            Customer = customer,
            Summary = summary
        };
    }

    public async Task<PagedResponse<CustomerAccountReservaListItemDto>> GetCustomerAccountReservasAsync(int id, PagedQuery query, CancellationToken cancellationToken)
    {
        var reservasQuery = _dbContext.Reservas
            .AsNoTracking()
            .Where(reserva => reserva.PayerId == id);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var normalized = query.Search.Trim().ToLowerInvariant();
            reservasQuery = reservasQuery.Where(reserva =>
                reserva.NumeroReserva.ToLower().Contains(normalized) ||
                reserva.Name.ToLower().Contains(normalized) ||
                reserva.Status.ToLower().Contains(normalized));
        }

        reservasQuery = !string.Equals(query.SortDir, "asc", StringComparison.OrdinalIgnoreCase)
            ? reservasQuery.OrderByDescending(reserva => reserva.CreatedAt).ThenByDescending(reserva => reserva.Id)
            : reservasQuery.OrderBy(reserva => reserva.CreatedAt).ThenBy(reserva => reserva.Id);

        return await reservasQuery
            .Select(reserva => new CustomerAccountReservaListItemDto
            {
                PublicId = reserva.PublicId,
                NumeroReserva = reserva.NumeroReserva,
                Name = reserva.Name,
                Status = reserva.Status,
                TotalSale = reserva.TotalSale,
                Balance = reserva.Balance,
                CreatedAt = reserva.CreatedAt,
                StartDate = reserva.StartDate,
                Paid = reserva.TotalPaid
            })
            .ToPagedResponseAsync(query, cancellationToken);
    }

    public async Task<PagedResponse<CustomerAccountPaymentListItemDto>> GetCustomerAccountPaymentsAsync(int id, PagedQuery query, CancellationToken cancellationToken)
    {
        var paymentsQuery = _dbContext.Payments
            .AsNoTracking()
            .Where(payment => payment.Reserva != null && payment.Reserva.PayerId == id);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var normalized = query.Search.Trim().ToLowerInvariant();
            paymentsQuery = paymentsQuery.Where(payment =>
                payment.Method.ToLower().Contains(normalized) ||
                payment.Reference != null && payment.Reference.ToLower().Contains(normalized) ||
                payment.Notes != null && payment.Notes.ToLower().Contains(normalized) ||
                payment.Reserva != null && payment.Reserva.NumeroReserva.ToLower().Contains(normalized));
        }

        paymentsQuery = !string.Equals(query.SortDir, "asc", StringComparison.OrdinalIgnoreCase)
            ? paymentsQuery.OrderByDescending(payment => payment.PaidAt).ThenByDescending(payment => payment.Id)
            : paymentsQuery.OrderBy(payment => payment.PaidAt).ThenBy(payment => payment.Id);

        return await paymentsQuery
            .Select(payment => new CustomerAccountPaymentListItemDto
            {
                PublicId = payment.PublicId,
                Amount = payment.Amount,
                Method = payment.Method,
                PaidAt = payment.PaidAt,
                Notes = payment.Notes,
                ReservaPublicId = payment.Reserva != null ? (Guid?)payment.Reserva.PublicId : null,
                NumeroReserva = payment.Reserva != null ? payment.Reserva.NumeroReserva : null,
                FileName = payment.Reserva != null ? payment.Reserva.Name : null,
                ReceiptPublicId = payment.Receipt != null ? (Guid?)payment.Receipt.PublicId : null,
                ReceiptNumber = payment.Receipt != null ? payment.Receipt.ReceiptNumber : null,
                ReceiptStatus = payment.Receipt != null ? payment.Receipt.Status : null
            })
            .ToPagedResponseAsync(query, cancellationToken);
    }

    public async Task<PagedResponse<InvoiceListDto>> GetCustomerAccountInvoicesAsync(int id, PagedQuery query, CancellationToken cancellationToken)
    {
        var invoicesQuery = _dbContext.Invoices
            .AsNoTracking()
            .Where(invoice => invoice.Reserva != null && invoice.Reserva.PayerId == id);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var normalized = query.Search.Trim().ToLowerInvariant();
            invoicesQuery = invoicesQuery.Where(invoice =>
                invoice.NumeroComprobante.ToString().Contains(normalized) ||
                invoice.ForceReason != null && invoice.ForceReason.ToLower().Contains(normalized) ||
                invoice.Reserva != null && invoice.Reserva.NumeroReserva.ToLower().Contains(normalized));
        }

        invoicesQuery = !string.Equals(query.SortDir, "asc", StringComparison.OrdinalIgnoreCase)
            ? invoicesQuery.OrderByDescending(invoice => invoice.CreatedAt).ThenByDescending(invoice => invoice.Id)
            : invoicesQuery.OrderBy(invoice => invoice.CreatedAt).ThenBy(invoice => invoice.Id);

        return await invoicesQuery
            .Select(invoice => new InvoiceListDto
            {
                PublicId = invoice.PublicId,
                ReservaPublicId = invoice.Reserva != null ? (Guid?)invoice.Reserva.PublicId : null,
                NumeroReserva = invoice.Reserva != null ? invoice.Reserva.NumeroReserva : null,
                CustomerName = invoice.Reserva != null && invoice.Reserva.Payer != null ? invoice.Reserva.Payer.FullName : null,
                TipoComprobante = invoice.TipoComprobante,
                PuntoDeVenta = invoice.PuntoDeVenta,
                NumeroComprobante = invoice.NumeroComprobante,
                ImporteTotal = invoice.ImporteTotal,
                CreatedAt = invoice.CreatedAt,
                CAE = invoice.CAE,
                Resultado = invoice.Resultado,
                Observaciones = invoice.Observaciones,
                WasForced = invoice.WasForced,
                ForceReason = invoice.ForceReason,
                ForcedByUserId = invoice.ForcedByUserId,
                ForcedByUserName = invoice.ForcedByUserName,
                ForcedAt = invoice.ForcedAt,
                OutstandingBalanceAtIssuance = invoice.OutstandingBalanceAtIssuance,
                InvoiceType =
                    invoice.TipoComprobante == 1 || invoice.TipoComprobante == 2 || invoice.TipoComprobante == 3 ? "A" :
                    invoice.TipoComprobante == 6 || invoice.TipoComprobante == 7 || invoice.TipoComprobante == 8 ? "B" :
                    invoice.TipoComprobante == 11 || invoice.TipoComprobante == 12 || invoice.TipoComprobante == 13 ? "C" :
                    invoice.TipoComprobante == 51 ? "M" :
                    "UNK"
            })
            .ToPagedResponseAsync(query, cancellationToken);
    }

    private static IQueryable<Customer> ApplyCustomerSearch(IQueryable<Customer> query, string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return query;
        }

        var normalized = search.Trim().ToLowerInvariant();
        return query.Where(customer =>
            customer.FullName.ToLower().Contains(normalized) ||
            customer.DocumentNumber != null && customer.DocumentNumber.ToLower().Contains(normalized) ||
            customer.TaxId != null && customer.TaxId.ToLower().Contains(normalized) ||
            customer.Email != null && customer.Email.ToLower().Contains(normalized));
    }

    private static IQueryable<Customer> ApplyCustomerOrdering(IQueryable<Customer> query, CustomerListQuery request)
    {
        var sortBy = (request.SortBy ?? "fullName").Trim().ToLowerInvariant();
        var desc = string.Equals(request.SortDir, "desc", StringComparison.OrdinalIgnoreCase);

        return sortBy switch
        {
            "currentbalance" => desc
                ? query.OrderByDescending(customer => customer.CurrentBalance).ThenBy(customer => customer.FullName)
                : query.OrderBy(customer => customer.CurrentBalance).ThenBy(customer => customer.FullName),
            "createdat" => desc
                ? query.OrderByDescending(customer => customer.CreatedAt).ThenByDescending(customer => customer.Id)
                : query.OrderBy(customer => customer.CreatedAt).ThenBy(customer => customer.Id),
            _ => desc
                ? query.OrderByDescending(customer => customer.FullName).ThenByDescending(customer => customer.Id)
                : query.OrderBy(customer => customer.FullName).ThenBy(customer => customer.Id)
        };
    }
}
