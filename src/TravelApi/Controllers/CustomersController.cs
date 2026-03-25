using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Errors;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/customers")]
[Authorize]
public class CustomersController : ControllerBase
{
    private readonly ICustomerService _customerService;
    private readonly EntityReferenceResolver _entityReferenceResolver;

    public CustomersController(ICustomerService customerService, EntityReferenceResolver entityReferenceResolver)
    {
        _customerService = customerService;
        _entityReferenceResolver = entityReferenceResolver;
    }

    [HttpGet]
    public async Task<ActionResult<PagedResponse<CustomerListItemDto>>> GetCustomers([FromQuery] CustomerListQuery query, CancellationToken cancellationToken = default)
    {
        var customers = await _customerService.GetCustomersAsync(query, cancellationToken);
        return Ok(customers);
    }

    [HttpGet("{publicIdOrLegacyId}")]
    public async Task<ActionResult<CustomerListItemDto>> GetCustomer(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Customer>(publicIdOrLegacyId, cancellationToken);
            var customer = await _customerService.GetCustomerAsync(id, cancellationToken);
            return Ok(customer);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPost]
    public async Task<ActionResult<Customer>> CreateCustomer(CustomerUpsertRequest customer, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _customerService.CreateCustomerAsync(MapCustomer(customer), cancellationToken);
            var response = await _customerService.GetCustomerAsync(result.Id, cancellationToken);
            return CreatedAtAction(nameof(GetCustomer), new { publicIdOrLegacyId = result.PublicId }, response);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException)
        {
            return BadRequest(new { message = "No se pudo crear el cliente. Verifica que el documento y el email no esten duplicados." });
        }
        catch (Exception ex)
        {
            if (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
            }
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo crear el cliente.");
        }
    }

    [HttpPut("{publicIdOrLegacyId}")]
    public async Task<ActionResult<Customer>> UpdateCustomer(string publicIdOrLegacyId, CustomerUpsertRequest customer, CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Customer>(publicIdOrLegacyId, cancellationToken);
            var result = await _customerService.UpdateCustomerAsync(id, MapCustomer(customer), cancellationToken);
            var response = await _customerService.GetCustomerAsync(result.Id, cancellationToken);
            return Ok(response);
        }
        catch (ArgumentException)
        {
            return BadRequest(new { message = "No se pudo actualizar el cliente." });
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException)
        {
            return BadRequest(new { message = "No se pudo actualizar el cliente. Verifica que el documento y el email no esten duplicados." });
        }
        catch (Exception ex)
        {
            if (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
            }
             return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo actualizar el cliente.");
        }
    }

    /// <summary>
    /// Cuenta corriente del cliente: reservas, pagos y saldo
    /// </summary>
    [HttpGet("{publicIdOrLegacyId}/account")]
    public async Task<ActionResult<CustomerAccountOverviewDto>> GetCustomerAccount(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Customer>(publicIdOrLegacyId, cancellationToken);
            var accountDto = await _customerService.GetCustomerAccountOverviewAsync(id, cancellationToken);
            return Ok(accountDto);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            if (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
            }
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo obtener la cuenta corriente del cliente.");
        }
    }

    [HttpGet("{publicIdOrLegacyId}/account/reservas")]
    public async Task<ActionResult<PagedResponse<CustomerAccountReservaListItemDto>>> GetCustomerAccountReservas(
        string publicIdOrLegacyId,
        [FromQuery] PagedQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Customer>(publicIdOrLegacyId, cancellationToken);
            return Ok(await _customerService.GetCustomerAccountReservasAsync(id, query, cancellationToken));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpGet("{publicIdOrLegacyId}/account/payments")]
    public async Task<ActionResult<PagedResponse<CustomerAccountPaymentListItemDto>>> GetCustomerAccountPayments(
        string publicIdOrLegacyId,
        [FromQuery] PagedQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Customer>(publicIdOrLegacyId, cancellationToken);
            return Ok(await _customerService.GetCustomerAccountPaymentsAsync(id, query, cancellationToken));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpGet("{publicIdOrLegacyId}/account/invoices")]
    public async Task<ActionResult<PagedResponse<InvoiceListDto>>> GetCustomerAccountInvoices(
        string publicIdOrLegacyId,
        [FromQuery] PagedQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Customer>(publicIdOrLegacyId, cancellationToken);
            return Ok(await _customerService.GetCustomerAccountInvoicesAsync(id, query, cancellationToken));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    private static Customer MapCustomer(CustomerUpsertRequest request)
    {
        return new Customer
        {
            FullName = request.FullName,
            Email = request.Email,
            Phone = request.Phone,
            DocumentNumber = request.DocumentNumber,
            Address = request.Address,
            Notes = request.Notes,
            TaxId = request.TaxId,
            TaxCondition = request.TaxCondition ?? "Consumidor Final",
            TaxConditionId = request.TaxConditionId,
            CreditLimit = request.CreditLimit,
            IsActive = request.IsActive
        };
    }
}

public record CustomerUpsertRequest(
    string FullName,
    string? Email,
    string? Phone,
    string? DocumentNumber,
    string? Address,
    string? Notes,
    string? TaxId,
    string? TaxCondition,
    int? TaxConditionId,
    decimal CreditLimit,
    bool IsActive = true);
