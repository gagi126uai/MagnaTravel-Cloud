using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Authorization;
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
    private readonly IEntityReferenceResolver _entityReferenceResolver;

    public CustomersController(ICustomerService customerService, IEntityReferenceResolver entityReferenceResolver)
    {
        _customerService = customerService;
        _entityReferenceResolver = entityReferenceResolver;
    }

    // ADR-023 T3.2: todo CustomersController estaba [Authorize] sin permiso fino ->
    // cualquier autenticado leia la lista de clientes (con documento, CUIT y saldo) y la
    // cuenta completa de cualquiera. Lecturas exigen clientes.view; escrituras clientes.edit;
    // las pantallas que muestran montos del cliente (cuenta y pagos) ademas cobranzas.view
    // (AND apilando atributos). Admin/Vendedor/Colaborador tienen clientes.view+clientes.edit
    // en sus defaults, asi que el gate no rompe las pantallas que ya operan clientes.
    [HttpGet]
    [RequirePermission(Permissions.ClientesView)]
    public async Task<ActionResult<PagedResponse<CustomerListItemDto>>> GetCustomers([FromQuery] CustomerListQuery query, CancellationToken cancellationToken = default)
    {
        var customers = await _customerService.GetCustomersAsync(query, cancellationToken);
        return Ok(customers);
    }

    [HttpGet("{publicIdOrLegacyId}")]
    [RequirePermission(Permissions.ClientesView)]
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
    [RequirePermission(Permissions.ClientesEdit)]
    public async Task<ActionResult<Customer>> CreateCustomer(CustomerUpsertRequest customer, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _customerService.CreateCustomerAsync(MapCustomer(customer), cancellationToken);
            var response = await _customerService.GetCustomerAsync(result.Id, cancellationToken);
            return CreatedAtAction(nameof(GetCustomer), new { publicIdOrLegacyId = result.PublicId }, response);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException)
        {
            return Conflict(new { message = "No se pudo crear el cliente. Verifica que el documento y el email no esten duplicados." });
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

    [HttpGet("search-similar")]
    [RequirePermission(Permissions.ClientesView)]
    public async Task<ActionResult<IReadOnlyList<CustomerSimilarMatchDto>>> SearchSimilar(
        [FromQuery] string? fullName,
        [FromQuery] string? documentType,
        [FromQuery] string? documentNumber,
        [FromQuery] string? phone,
        [FromQuery] int take,
        CancellationToken cancellationToken)
    {
        var matches = await _customerService.SearchSimilarAsync(fullName, documentType, documentNumber, phone, take, cancellationToken);
        return Ok(matches);
    }

    [HttpPut("{publicIdOrLegacyId}")]
    [RequirePermission(Permissions.ClientesEdit)]
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
        catch (InvalidOperationException ex)
        {
            // B1.15 Fase 0' (CODE-06): MutationGuards rechaza cambios fiscales
            // (TaxId/TaxCondition) cuando hay factura AFIP viva. 409 Conflict.
            return Conflict(new { message = ex.Message });
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

    [HttpDelete("{publicIdOrLegacyId}")]
    [RequirePermission(Permissions.ClientesEdit)]
    public async Task<IActionResult> DeleteOrArchive(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Customer>(publicIdOrLegacyId, cancellationToken);
            var result = await _customerService.DeleteOrArchiveCustomerAsync(id, cancellationToken);
            return Ok(new { outcome = result.Outcome.ToString(), message = result.Message });
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
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo eliminar el cliente.");
        }
    }

    [HttpPatch("{publicIdOrLegacyId}/reactivate")]
    [RequirePermission(Permissions.ClientesEdit)]
    public async Task<IActionResult> Reactivate(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Customer>(publicIdOrLegacyId, cancellationToken);
            await _customerService.ReactivateCustomerAsync(id, cancellationToken);
            var customer = await _customerService.GetCustomerAsync(id, cancellationToken);
            return Ok(customer);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    /// <summary>
    /// Cuenta corriente del cliente: reservas, pagos y saldo
    /// </summary>
    // ADR-023 T3.2: la cuenta muestra saldo y plata -> exige clientes.view Y cobranzas.view
    // (AND: dos atributos apilados). Asi un usuario que ve clientes pero no cobranzas no
    // accede a los montos de la cuenta.
    [HttpGet("{publicIdOrLegacyId}/account")]
    [RequirePermission(Permissions.ClientesView)]
    [RequirePermission(Permissions.CobranzasView)]
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
    [RequirePermission(Permissions.ClientesView)]
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

    // ADR-023 T3.2: los pagos muestran montos -> clientes.view Y cobranzas.view (AND).
    [HttpGet("{publicIdOrLegacyId}/account/payments")]
    [RequirePermission(Permissions.ClientesView)]
    [RequirePermission(Permissions.CobranzasView)]
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
    [RequirePermission(Permissions.ClientesView)]
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

    /// <summary>
    /// Lista los saldos a favor DISPONIBLES del cliente (entries con RemainingBalance &gt; 0), del más
    /// viejo al más nuevo. El front lo usa para el botón "usar saldo a favor": el usuario elige de qué
    /// entry retirar y luego llama al withdraw (POST /api/client-credit-entries/{entryPublicId}/withdrawals).
    /// </summary>
    // Mismo gate que /account: la lista expone montos a favor del cliente -> clientes.view Y cobranzas.view
    // (AND apilando atributos). Coherente con el resto de la cuenta del cliente.
    [HttpGet("{publicIdOrLegacyId}/available-credit")]
    [RequirePermission(Permissions.ClientesView)]
    [RequirePermission(Permissions.CobranzasView)]
    public async Task<ActionResult<IReadOnlyList<CustomerAvailableCreditEntryDto>>> GetCustomerAvailableCredit(
        string publicIdOrLegacyId,
        CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Customer>(publicIdOrLegacyId, cancellationToken);
            return Ok(await _customerService.GetCustomerAvailableCreditAsync(id, cancellationToken));
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
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo obtener el saldo a favor del cliente.");
        }
    }

    private static Customer MapCustomer(CustomerUpsertRequest request)
    {
        return new Customer
        {
            FullName = request.FullName,
            Email = request.Email,
            Phone = request.Phone,
            DocumentType = string.IsNullOrWhiteSpace(request.DocumentType) ? null : request.DocumentType.Trim(),
            DocumentNumber = string.IsNullOrWhiteSpace(request.DocumentNumber) ? null : request.DocumentNumber.Trim(),
            Address = request.Address,
            Notes = request.Notes,
            TaxId = request.TaxId,
            TaxCondition = request.TaxCondition ?? "Consumidor Final",
            TaxConditionId = request.TaxConditionId,
            // ADR-023 T1.5: CreditLimit ya NO viaja en el request ni se mapea. La columna en DB queda intacta
            // (no se borran datos); simplemente deja de poder setearse por API. El campo sigue en el DTO de
            // salida hasta que el front lo retire (en su tanda con UX gate).
            IsActive = request.IsActive
        };
    }
}

// ADR-023 T1.5: CreditLimit quitado del request (no se puede setear por API). El nuevo Customer creado/editado
// usa el default de la entidad (0) para CreditLimit; en edicion no se toca el valor guardado.
public record CustomerUpsertRequest(
    string FullName,
    string? Email,
    string? Phone,
    string? DocumentType,
    string? DocumentNumber,
    string? Address,
    string? Notes,
    string? TaxId,
    string? TaxCondition,
    int? TaxConditionId,
    bool IsActive = true);
