using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Authorization;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Exceptions;
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
    private readonly IClientCreditService _clientCreditService;

    public CustomersController(
        ICustomerService customerService,
        IEntityReferenceResolver entityReferenceResolver,
        IClientCreditService clientCreditService)
    {
        _customerService = customerService;
        _entityReferenceResolver = entityReferenceResolver;
        _clientCreditService = clientCreditService;
    }

    // ADR-023 T3.2: todo CustomersController estaba [Authorize] sin permiso fino ->
    // cualquier autenticado leia la lista de clientes (con documento, CUIT y saldo) y la
    // cuenta completa de cualquiera. Lecturas exigen clientes.view; escrituras clientes.edit;
    // las pantallas que muestran montos del cliente (cuenta y pagos) ademas cobranzas.view
    // (AND apilando atributos). Admin/Vendedor/Colaborador tienen clientes.view+clientes.edit
    // en sus defaults, asi que el gate no rompe las pantallas que ya operan clientes.
    [HttpGet]
    [RequirePermission(Permissions.ClientesView)]
    [RequirePermission(Permissions.CobranzasView)]
    public async Task<ActionResult<PagedResponse<CustomerListItemDto>>> GetCustomers([FromQuery] CustomerListQuery query, CancellationToken cancellationToken = default)
    {
        var customers = await _customerService.GetCustomersAsync(query, cancellationToken);
        return Ok(customers);
    }

    [HttpGet("{publicIdOrLegacyId}")]
    [RequirePermission(Permissions.ClientesView)]
    [RequirePermission(Permissions.CobranzasView)]
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
            return CreatedAtAction(nameof(GetCustomer), new { publicIdOrLegacyId = result.PublicId }, MapMutationResponse(result));
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
            return Ok(MapMutationResponse(result));
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

    private static object MapMutationResponse(Customer customer) => new
    {
        customer.PublicId,
        customer.FullName,
        customer.Email,
        customer.Phone,
        customer.DocumentType,
        customer.DocumentNumber,
        customer.Address,
        customer.Notes,
        customer.TaxId,
        customer.TaxCondition,
        customer.TaxConditionId,
        customer.CreditLimit,
        customer.BillingMode,
        customer.PaymentTermsDays,
        customer.IsActive
    };

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

    /// <summary>
    /// EXTRACTO (libro mayor) de la cuenta por cobrar del cliente: una linea por cada venta confirmada (cargo)
    /// y cada cobro (abono), con saldo corriente POR MONEDA, calculado EN EL SERVIDOR. El saldo de cierre de
    /// cada moneda reconcilia con el "Debe" por moneda del header (el receivable) por construccion. Reemplaza el
    /// armado en el navegador (que mezclaba pagos+facturas con techo de 500 y no cerraba con el resumen).
    /// </summary>
    // Mismo gate que /account: expone montos de la cuenta del cliente -> clientes.view Y cobranzas.view (AND
    // apilando atributos). El lado VENTA no enmascara montos (no hay costo ni margen en el extracto).
    [HttpGet("{publicIdOrLegacyId}/account/statement")]
    [RequirePermission(Permissions.ClientesView)]
    [RequirePermission(Permissions.CobranzasView)]
    public async Task<ActionResult<CustomerAccountStatementDto>> GetCustomerAccountStatement(
        string publicIdOrLegacyId,
        CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Customer>(publicIdOrLegacyId, cancellationToken);
            return Ok(await _customerService.GetCustomerAccountStatementAsync(id, cancellationToken));
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
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo obtener el extracto de la cuenta del cliente.");
        }
    }

    [HttpGet("{publicIdOrLegacyId}/account/reservas")]
    [RequirePermission(Permissions.ClientesView)]
    [RequirePermission(Permissions.CobranzasView)]
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
    [RequirePermission(Permissions.CobranzasView)]
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
    /// Deuda del cliente DESGLOSADA POR RESERVA y por moneda (espejo del lado proveedor
    /// GET /api/suppliers/{id}/account/debt-by-reserva). Solo reservas con saldo pendiente. Alimenta el
    /// buscador del flujo "usar saldo a favor -> aplicar a otra reserva": el front ofrece como destino solo
    /// reservas con deuda en la MISMA moneda del saldo a favor (el saldo en USD no cancela deuda en ARS).
    /// </summary>
    // Mismo gate que /account y /credit: expone montos de deuda del cliente -> clientes.view Y cobranzas.view
    // (AND apilando atributos). El lado VENTA no enmascara montos (a diferencia de la cuenta del proveedor).
    [HttpGet("{publicIdOrLegacyId}/account/debt-by-reserva")]
    [RequirePermission(Permissions.ClientesView)]
    [RequirePermission(Permissions.CobranzasView)]
    public async Task<ActionResult<CustomerDebtByReservaDto>> GetCustomerDebtByReserva(
        string publicIdOrLegacyId,
        CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Customer>(publicIdOrLegacyId, cancellationToken);
            return Ok(await _customerService.GetCustomerDebtByReservaAsync(id, cancellationToken));
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

    // ===================================================================================================
    // FC4 — saldo a favor del cliente CONSUMIBLE: ver / APLICAR / REVERTIR a otra reserva del mismo cliente.
    // Espejo del lado operador (SuppliersController .../credit). Lectura con clientes.view + cobranzas.view
    // (mismo gate que /account y /available-credit, que tampoco enmascaran montos del cliente). Aplicar y
    // revertir mueven la deuda de una reserva -> cobranzas.edit. La ownership de la reserva destino la valida
    // el service (devuelve 403 si esta a cargo de otro vendedor y el usuario no ve todas las cobranzas).
    // ===================================================================================================

    /// <summary>Saldo a favor disponible del cliente, agrupado por moneda (bolsillos con saldo &gt; 0).</summary>
    [HttpGet("{publicIdOrLegacyId}/credit")]
    [RequirePermission(Permissions.ClientesView)]
    [RequirePermission(Permissions.CobranzasView)]
    public async Task<ActionResult<ClientCreditOverviewDto>> GetCustomerCredit(
        string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Customer>(publicIdOrLegacyId, cancellationToken);
            return Ok(await _clientCreditService.GetCustomerCreditAsync(id, cancellationToken));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    /// <summary>Aplica saldo a favor del cliente a OTRA reserva del mismo cliente y misma moneda (FIFO).</summary>
    [HttpPost("{publicIdOrLegacyId}/credit/apply")]
    [RequirePermission(Permissions.CobranzasEdit)]
    public async Task<ActionResult<ClientCreditApplicationResultDto>> ApplyCustomerCredit(
        string publicIdOrLegacyId, [FromBody] ApplyClientCreditRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Customer>(publicIdOrLegacyId, cancellationToken);
            var (userId, userName) = ResolveActor();
            var result = await _clientCreditService.ApplyCustomerCreditAsync(id, request, userId, userName, cancellationToken);
            return Ok(result);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (UnauthorizedAccessException)
        {
            // La reserva destino no esta a cargo del usuario actual (y este no ve todas las cobranzas). 403,
            // mismo contrato que el alta de pago / el flujo por-bolsillo.
            return new ObjectResult(PermissionDeniedProblemFactory.OwnershipRequired(OwnedEntity.Reserva.ToString()))
            {
                StatusCode = StatusCodes.Status403Forbidden
            };
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (BusinessInvariantViolationException ex)
        {
            // Tope superado / moneda cruzada / cliente equivocado / estado no firme: conflicto de negocio (409).
            return Conflict(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            // Modulo deshabilitado (feature flag off).
            return Conflict(new { message = ex.Message });
        }
    }

    /// <summary>Revierte una aplicacion de saldo a favor del cliente (repone el bolsillo, deshace el puente).</summary>
    [HttpPost("{publicIdOrLegacyId}/credit/applications/{applicationPublicId:guid}/reverse")]
    [RequirePermission(Permissions.CobranzasEdit)]
    public async Task<ActionResult<ClientCreditApplicationResultDto>> ReverseCustomerCreditApplication(
        string publicIdOrLegacyId,
        Guid applicationPublicId,
        [FromBody] ReverseClientCreditApplicationRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Customer>(publicIdOrLegacyId, cancellationToken);
            var (userId, userName) = ResolveActor();
            var result = await _clientCreditService.ReverseCustomerCreditApplicationAsync(
                id, applicationPublicId, request, userId, userName, cancellationToken);
            return Ok(result);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (UnauthorizedAccessException)
        {
            // La reserva destino (cuya deuda muta la reversa) no esta a cargo del usuario actual (y este no ve
            // todas las cobranzas). 403, mismo contrato que el alta de pago / el apply / el flujo por-bolsillo.
            return new ObjectResult(PermissionDeniedProblemFactory.OwnershipRequired(OwnedEntity.Reserva.ToString()))
            {
                StatusCode = StatusCodes.Status403Forbidden
            };
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (BusinessInvariantViolationException ex)
        {
            // No es una aplicacion / ya revertida / tope superado (409).
            return Conflict(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            // Flag off o estado inconsistente (puente sin reserva destino).
            return Conflict(new { message = ex.Message });
        }
    }

    // ===================================================================================================
    // Tanda D1 (2026-07-16) — saldo a favor del cliente APLICADO CONTRA UNA MULTA + neteo automatico al
    // devolver. Mismos gates que el bloque FC4 de arriba: lectura clientes.view+cobranzas.view, escritura
    // cobranzas.edit. La ownership de la reserva de la multa la valida el service (403 si esta a cargo de
    // otro vendedor y el usuario no ve todas las cobranzas).
    // ===================================================================================================

    /// <summary>Vista previa (solo lectura) de cuanto se le devolveria al cliente si pide su saldo a favor ahora, neteado contra sus multas abiertas.</summary>
    [HttpGet("{publicIdOrLegacyId}/credit/refund-netting-preview")]
    [RequirePermission(Permissions.ClientesView)]
    [RequirePermission(Permissions.CobranzasView)]
    public async Task<ActionResult<RefundNettingPreviewDto>> GetCustomerRefundNettingPreview(
        string publicIdOrLegacyId, [FromQuery] string currency, CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Customer>(publicIdOrLegacyId, cancellationToken);
            return Ok(await _clientCreditService.GetCustomerRefundNettingPreviewAsync(id, currency, cancellationToken));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>Aplica saldo a favor del cliente contra UNA multa (Nota de Debito de una reserva anulada del mismo cliente, misma moneda).</summary>
    [HttpPost("{publicIdOrLegacyId}/credit/apply-to-penalty")]
    [RequirePermission(Permissions.CobranzasEdit)]
    public async Task<ActionResult<ClientCreditApplicationResultDto>> ApplyCustomerCreditToPenalty(
        string publicIdOrLegacyId, [FromBody] ApplyCreditToPenaltyRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Customer>(publicIdOrLegacyId, cancellationToken);
            var (userId, userName) = ResolveActor();
            var result = await _clientCreditService.ApplyCustomerCreditToPenaltyAsync(id, request, userId, userName, cancellationToken);
            return Ok(result);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (UnauthorizedAccessException)
        {
            // La reserva de la multa no esta a cargo del usuario actual (y este no ve todas las cobranzas).
            return new ObjectResult(PermissionDeniedProblemFactory.OwnershipRequired(OwnedEntity.Reserva.ToString()))
            {
                StatusCode = StatusCodes.Status403Forbidden
            };
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (BusinessInvariantViolationException ex)
        {
            // Gate de comprobante sin CAE / moneda cruzada / cliente equivocado / tope superado / sin pool (409).
            return Conflict(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            // Modulo deshabilitado (feature flag off), o la ND no corresponde a una multa documentada.
            return Conflict(new { message = ex.Message });
        }
    }

    /// <summary>Devuelve el saldo a favor del cliente en una moneda, neteando automaticamente contra sus multas abiertas de esa moneda antes de calcular el egreso.</summary>
    [HttpPost("{publicIdOrLegacyId}/credit/refund-with-netting")]
    [RequirePermission(Permissions.CobranzasEdit)]
    public async Task<ActionResult<RefundWithNettingResultDto>> RefundCustomerCreditWithNetting(
        string publicIdOrLegacyId, [FromBody] RefundWithNettingRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Customer>(publicIdOrLegacyId, cancellationToken);
            var (userId, userName) = ResolveActor();
            var result = await _clientCreditService.RefundCustomerCreditWithNettingAsync(id, request, userId, userName, cancellationToken);
            return Ok(result);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (BusinessInvariantViolationException ex)
        {
            // Sin saldo a favor disponible / Ley 25.345 sobre el neto en efectivo (409).
            return Conflict(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            // Modulo deshabilitado (feature flag off).
            return Conflict(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Revierte una aplicacion de saldo a favor CONTRA UNA MULTA (mismo mecanismo de reversa que
    /// <see cref="ReverseCustomerCreditApplication"/>, ruta dedicada para que el front la distinga en el
    /// extracto). El puente de multa se revierte con el MISMO metodo de servicio: el bolsillo se re-incrementa
    /// y la ND vuelve a su saldo pendiente anterior.
    /// </summary>
    [HttpPost("{publicIdOrLegacyId}/credit/penalty-applications/{applicationPublicId:guid}/reverse")]
    [RequirePermission(Permissions.CobranzasEdit)]
    public async Task<ActionResult<ClientCreditApplicationResultDto>> ReverseCustomerCreditPenaltyApplication(
        string publicIdOrLegacyId,
        Guid applicationPublicId,
        [FromBody] ReverseClientCreditApplicationRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Customer>(publicIdOrLegacyId, cancellationToken);
            var (userId, userName) = ResolveActor();
            var result = await _clientCreditService.ReverseCustomerCreditApplicationAsync(
                id, applicationPublicId, request, userId, userName, cancellationToken);
            return Ok(result);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (UnauthorizedAccessException)
        {
            return new ObjectResult(PermissionDeniedProblemFactory.OwnershipRequired(OwnedEntity.Reserva.ToString()))
            {
                StatusCode = StatusCodes.Status403Forbidden
            };
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (BusinessInvariantViolationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    /// <summary>Actor actual (userId, userName) para auditoria de las operaciones de saldo a favor del cliente.</summary>
    private (string UserId, string? UserName) ResolveActor()
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "System";
        var userName = User.FindFirst("FullName")?.Value ?? User.FindFirst(ClaimTypes.Name)?.Value;
        return (userId, userName);
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
