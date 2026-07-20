#pragma warning disable CS8601, CS8602, CS8604, CS8618
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Authorization;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Exceptions;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Controllers;

// B1.15 Fase 0' (CODE-09): hotfix de seguridad. Antes el controller era
// solo [Authorize] — cualquier autenticado podia crear, modificar o borrar
// proveedores y operar pagos egresos. Ahora cada endpoint exige permiso
// especifico (proveedores.view/edit/edit_fiscal o tesoreria.supplier_payments).
[Authorize]
[ApiController]
[Route("api/[controller]")]
public class SuppliersController : ControllerBase
{
    private readonly ISupplierService _supplierService;
    private readonly IEntityReferenceResolver _entityReferenceResolver;
    private readonly ISupplierCreditService _supplierCreditService;
    private readonly IOperatorRefundReadModelService _operatorRefundReadModel;

    public SuppliersController(
        ISupplierService supplierService,
        IEntityReferenceResolver entityReferenceResolver,
        ISupplierCreditService supplierCreditService,
        IOperatorRefundReadModelService operatorRefundReadModel)
    {
        _supplierService = supplierService;
        _entityReferenceResolver = entityReferenceResolver;
        _supplierCreditService = supplierCreditService;
        _operatorRefundReadModel = operatorRefundReadModel;
    }

    [HttpGet]
    [RequirePermission(Permissions.ProveedoresView)]
    public async Task<ActionResult<PagedResponse<SupplierListItemDto>>> GetSuppliers([FromQuery] SupplierListQuery query, CancellationToken cancellationToken)
    {
        return Ok(await _supplierService.GetSuppliersAsync(query, cancellationToken));
    }

    [HttpPost("recalculate-all")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult> RecalculateAllBalances(CancellationToken cancellationToken)
    {
        await _supplierService.RecalculateAllBalancesAsync(cancellationToken);
        return Ok(new { Message = "Saldos recalculados" });
    }

    [HttpGet("{publicIdOrLegacyId}")]
    [RequirePermission(Permissions.ProveedoresView)]
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
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost]
    [RequirePermission(Permissions.ProveedoresEdit)]
    public async Task<ActionResult<Supplier>> CreateSupplier(SupplierUpsertRequest supplier, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _supplierService.CreateSupplierAsync(MapSupplier(supplier), cancellationToken);
            return CreatedAtAction(nameof(GetSupplier), new { publicIdOrLegacyId = result.PublicId }, ToSupplierResponse(result));
        }
        catch (ArgumentException ex)
        {
            // Las validaciones del servicio (nombre requerido, plazo de pago negativo, moneda por defecto no
            // soportada) lanzan ArgumentException con un mensaje en espanol de negocio (sin nombres internos ni
            // strings de framework/enum). Se devuelve ese mensaje para que el alta muestre el motivo concreto.
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPut("{publicIdOrLegacyId}")]
    [RequirePermission(Permissions.ProveedoresEdit)]
    public async Task<ActionResult<Supplier>> UpdateSupplier(string publicIdOrLegacyId, SupplierUpsertRequest supplier, CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Supplier>(publicIdOrLegacyId, cancellationToken);
            var mapped = MapSupplier(supplier);
            // Clientes previos a este contrato no mandan InvoicingMode. Preservar el valor real evita convertir
            // silenciosamente un intermediario en compra/reventa al editar telefono, estado u otro dato.
            if (!supplier.InvoicingMode.HasValue)
            {
                var current = await _supplierService.GetSupplierAsync(id, cancellationToken);
                mapped.InvoicingMode = current.InvoicingMode;
            }
            var result = await _supplierService.UpdateSupplierAsync(id, mapped, cancellationToken);
            return Ok(ToSupplierResponse(result));
        }
        catch (ArgumentException ex)
        {
            // Igual que en el alta: el servicio valida con mensajes de negocio en espanol (sin internals).
            return BadRequest(new { message = ex.Message });
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            // C29: guard de desactivacion (IsActive: true -> false con reservas activas).
            // B1.15 Fase 0' (CODE-13): guard fiscal (TaxId/TaxCondition con CAE viva).
            // Ambos casos reflejan "estado actual incompatible con la operacion" → 409.
            return Conflict(new { message = ex.Message });
        }
    }

    [HttpDelete("{publicIdOrLegacyId}")]
    [RequirePermission(Permissions.ProveedoresEdit)]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult> DeleteSupplier(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Supplier>(publicIdOrLegacyId, cancellationToken);
            await _supplierService.DeleteSupplierAsync(id, cancellationToken);
            return Ok(new { Message = "Proveedor eliminado" });
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    // C24: el endpoint DELETE /suppliers/{id}/force fue eliminado por completo.
    // Reemplazado por el flujo unico DeleteSupplier que valida referencias antes
    // de borrar. Si el cliente recibia 200 con "(forzado)", ahora recibe 404 al
    // pegarle al path; eso es intencional — antes podia dejar la BD inconsistente.

    [HttpGet("{publicIdOrLegacyId}/account")]
    [RequirePermission(Permissions.ProveedoresView)]
    public async Task<ActionResult<SupplierAccountOverviewDto>> GetSupplierAccount(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Supplier>(publicIdOrLegacyId, cancellationToken);
            return Ok(await _supplierService.GetSupplierAccountOverviewAsync(id, cancellationToken));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpGet("{publicIdOrLegacyId}/invoices")]
    [RequirePermission(Permissions.ProveedoresView)]
    public async Task<ActionResult<IReadOnlyList<SupplierInvoiceDto>>> GetSupplierInvoices(
        string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Supplier>(publicIdOrLegacyId, cancellationToken);
            return Ok(await _supplierService.GetSupplierInvoicesAsync(id, cancellationToken));
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    [HttpPost("{publicIdOrLegacyId}/invoices")]
    [RequirePermission(Permissions.TesoreriaSupplierPayments)]
    [RequirePermission(Permissions.CobranzasSeeCost)]
    public async Task<ActionResult<SupplierInvoiceDto>> CreateSupplierInvoice(
        string publicIdOrLegacyId, SupplierInvoiceCreateRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Supplier>(publicIdOrLegacyId, cancellationToken);
            var created = await _supplierService.CreateSupplierInvoiceAsync(id, request, cancellationToken);
            return CreatedAtAction(nameof(GetSupplierInvoices), new { publicIdOrLegacyId }, created);
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException ex) { return BadRequest(new { message = ex.Message }); }
        catch (InvalidOperationException ex) { return Conflict(new { message = ex.Message }); }
    }

    [HttpPost("{publicIdOrLegacyId}/invoices/{invoicePublicId:guid}/applications")]
    [RequirePermission(Permissions.TesoreriaSupplierPayments)]
    [RequirePermission(Permissions.CobranzasSeeCost)]
    public async Task<ActionResult<SupplierInvoiceDto>> ApplySupplierPaymentToInvoice(
        string publicIdOrLegacyId, Guid invoicePublicId,
        SupplierInvoicePaymentApplicationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Supplier>(publicIdOrLegacyId, cancellationToken);
            return Ok(await _supplierService.ApplySupplierPaymentToInvoiceAsync(id, invoicePublicId, request, cancellationToken));
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException ex) { return BadRequest(new { message = ex.Message }); }
        catch (InvalidOperationException ex) { return Conflict(new { message = ex.Message }); }
    }

    [HttpPost("{publicIdOrLegacyId}/invoices/{invoicePublicId:guid}/applications/{applicationPublicId:guid}/reverse")]
    [RequirePermission(Permissions.TesoreriaSupplierPayments)]
    [RequirePermission(Permissions.CobranzasSeeCost)]
    public async Task<ActionResult<SupplierInvoiceDto>> ReverseSupplierInvoicePaymentApplication(
        string publicIdOrLegacyId, Guid invoicePublicId, Guid applicationPublicId,
        SupplierInvoicePaymentApplicationReversalRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Supplier>(publicIdOrLegacyId, cancellationToken);
            return Ok(await _supplierService.ReverseSupplierInvoicePaymentApplicationAsync(
                id, invoicePublicId, applicationPublicId, request.Reason, cancellationToken));
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException ex) { return BadRequest(new { message = ex.Message }); }
        catch (InvalidOperationException ex) { return Conflict(new { message = ex.Message }); }
    }

    [HttpPost("{publicIdOrLegacyId}/invoices/{invoicePublicId:guid}/void")]
    [RequirePermission(Permissions.TesoreriaSupplierPayments)]
    [RequirePermission(Permissions.CobranzasSeeCost)]
    public async Task<ActionResult<SupplierInvoiceDto>> VoidSupplierInvoice(
        string publicIdOrLegacyId, Guid invoicePublicId,
        SupplierInvoiceVoidRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Supplier>(publicIdOrLegacyId, cancellationToken);
            return Ok(await _supplierService.VoidSupplierInvoiceAsync(id, invoicePublicId, request.Reason, cancellationToken));
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException ex) { return BadRequest(new { message = ex.Message }); }
        catch (InvalidOperationException ex) { return Conflict(new { message = ex.Message }); }
    }

    [HttpGet("{publicIdOrLegacyId}/account/services")]
    [RequirePermission(Permissions.ProveedoresView)]
    public async Task<ActionResult<PagedResponse<SupplierAccountServiceListItemDto>>> GetSupplierAccountServices(
        string publicIdOrLegacyId,
        [FromQuery] SupplierAccountServicesQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Supplier>(publicIdOrLegacyId, cancellationToken);
            return Ok(await _supplierService.GetSupplierAccountServicesAsync(id, query, cancellationToken));
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

    // Auditoria ERP hallazgo #4 (2026-06-12): deuda con el proveedor DESGLOSADA POR EXPEDIENTE (reserva).
    // Mismo permiso que el resto de la cuenta del proveedor (proveedores.view): la estructura (que reservas,
    // que monedas) es visible con ese permiso; los MONTOS los enmascara el servicio si falta cobranzas.see_cost.
    [HttpGet("{publicIdOrLegacyId}/account/debt-by-reserva")]
    [RequirePermission(Permissions.ProveedoresView)]
    public async Task<ActionResult<SupplierDebtByReservaDto>> GetSupplierDebtByReserva(
        string publicIdOrLegacyId,
        CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Supplier>(publicIdOrLegacyId, cancellationToken);
            return Ok(await _supplierService.GetSupplierDebtByReservaAsync(id, cancellationToken));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    // TANDA 1 (cuenta corriente del proveedor): EXTRACTO de la Cuenta por Pagar como libro mayor por moneda
    // (cargos = compras confirmadas, abonos = pagos al operador, saldo corriente). Mismo permiso que el resto
    // de la cuenta del proveedor (proveedores.view): la estructura es visible con ese permiso; los MONTOS los
    // enmascara el servicio si falta cobranzas.see_cost (read-only, sin migracion).
    [HttpGet("{publicIdOrLegacyId}/account/statement")]
    [RequirePermission(Permissions.ProveedoresView)]
    public async Task<ActionResult<SupplierAccountStatementDto>> GetSupplierAccountStatement(
        string publicIdOrLegacyId,
        CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Supplier>(publicIdOrLegacyId, cancellationToken);
            return Ok(await _supplierService.GetSupplierAccountStatementAsync(id, cancellationToken));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    // ADR-041 TANDA 4 (2026-06-28): "reembolsos a cobrar" de ESTE operador = sus cancelaciones esperando (o ya
    // dadas por perdidas esperando) el reintegro, con semaforo y estimado por moneda.
    //
    // SEGURIDAD (B1, review backend+seguridad 2026-06-28): se gatea con tesoreria.supplier_payments, NO con
    // proveedores.view. Motivo: el read-model puebla el NOMBRE del cliente que origino cada anulacion, y
    // proveedores.view lo tiene el rol Vendedor -> con ese permiso un vendedor veria clientes de otros vendedores
    // (fuga horizontal). tesoreria.supplier_payments es el permiso de la pata de tesoreria del proveedor
    // (account/payments, y la bandeja global), coherente con que esto es informacion de cobranza al operador. Los
    // MONTOS ademas se enmascaran si falta cobranzas.see_cost. SOLO LECTURA, sin migracion.
    [HttpGet("{publicIdOrLegacyId}/operator-refunds/pending")]
    [RequirePermission(Permissions.TesoreriaSupplierPayments)]
    public async Task<ActionResult<IReadOnlyList<OperatorRefundPendingItemDto>>> GetSupplierPendingOperatorRefunds(
        string publicIdOrLegacyId,
        CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Supplier>(publicIdOrLegacyId, cancellationToken);
            return Ok(await _operatorRefundReadModel.GetSupplierPendingRefundsAsync(id, cancellationToken));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpGet("{publicIdOrLegacyId}/account/payments")]
    [RequirePermission(Permissions.TesoreriaSupplierPayments)]
    public async Task<ActionResult<PagedResponse<SupplierPaymentDto>>> GetSupplierAccountPayments(
        string publicIdOrLegacyId,
        [FromQuery] SupplierAccountPaymentsQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Supplier>(publicIdOrLegacyId, cancellationToken);
            return Ok(await _supplierService.GetSupplierAccountPaymentsAsync(id, query, cancellationToken));
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

    [HttpPost("{publicIdOrLegacyId}/payments")]
    [RequirePermission(Permissions.TesoreriaSupplierPayments)]
    public async Task<ActionResult> AddSupplierPayment(string publicIdOrLegacyId, [FromBody] SupplierPaymentRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Supplier>(publicIdOrLegacyId, cancellationToken);
            var paymentPublicId = await _supplierService.AddSupplierPaymentAsync(id, request, cancellationToken);
            return Ok(new { Message = "Pago registrado correctamente", PaymentPublicId = paymentPublicId });
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        // Antes estos dos catch eran ArgumentException/InvalidOperationException "a secas": propagaban el
        // mensaje real de negocio (moneda equivocada, cargo ya liquidado, servicio de otro operador, etc),
        // PERO de paso tambien atrapaban sin querer cualquier excepcion de esos mismos tipos que viniera de
        // un bug de framework, con mensaje en ingles o con internals. Ahora SupplierService lanza una
        // excepcion PROPIA del circuito (SupplierPaymentValidationException) para sus validaciones de
        // negocio, y este catch atrapa SOLO esa: cualquier otro Argument/InvalidOperationException real de
        // framework ya NO cae aca, sigue de largo y el GlobalExceptionHandler lo convierte en el generico
        // amigable (2026-07-18, Tanda 1 del plan de remediacion pantalla-motor — fix del mecanismo tras
        // review backend + data-exposure).
        catch (SupplierPaymentValidationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (BusinessInvariantViolationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    [HttpPut("{publicIdOrLegacyId}/payments/{paymentPublicIdOrLegacyId}")]
    [RequirePermission(Permissions.TesoreriaSupplierPayments)]
    public async Task<ActionResult> UpdateSupplierPayment(string publicIdOrLegacyId, string paymentPublicIdOrLegacyId, [FromBody] SupplierPaymentRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Supplier>(publicIdOrLegacyId, cancellationToken);
            var paymentId = await _entityReferenceResolver.ResolveRequiredIdAsync<SupplierPayment>(paymentPublicIdOrLegacyId, cancellationToken);
            await _supplierService.UpdateSupplierPaymentAsync(id, paymentId, request, cancellationToken);
            return Ok(new { Message = "Pago actualizado correctamente" });
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        // Mismo criterio que AddSupplierPayment de arriba: catch especifico de la excepcion de negocio del
        // circuito, no un Argument/InvalidOperationException ancho (Tanda 1 del plan de remediacion
        // pantalla-motor, 2026-07-18).
        catch (SupplierPaymentValidationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        // N1 (review backend, 2026-07-18): UpdateSupplierPaymentAsync tambien puede rechazar por el
        // invariante "un pago que liquida un cargo del operador es inmutable en lo economico" (ver
        // INV-ADR044-SUPPLIER-SETTLEMENT-IMMUTABLE). AddSupplierPayment ya mapea BusinessInvariantViolationException
        // a 409; faltaba la misma simetria aca.
        catch (BusinessInvariantViolationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    [HttpDelete("{publicIdOrLegacyId}/payments/{paymentPublicIdOrLegacyId}")]
    [RequirePermission(Permissions.TesoreriaSupplierPayments)]
    public async Task<ActionResult> DeleteSupplierPayment(string publicIdOrLegacyId, string paymentPublicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Supplier>(publicIdOrLegacyId, cancellationToken);
            var paymentId = await _entityReferenceResolver.ResolveRequiredIdAsync<SupplierPayment>(paymentPublicIdOrLegacyId, cancellationToken);
            await _supplierService.DeleteSupplierPaymentAsync(id, paymentId, cancellationToken);
            return Ok(new { Message = "Pago eliminado y saldo restaurado" });
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        // Mismo mecanismo que Add/Update (2026-07-18, Tanda 1): catch especifico de la excepcion de negocio
        // del circuito, no un InvalidOperationException ancho. Se mantiene el status 409 que ya tenia este
        // endpoint (a diferencia de Add/Update que usan 400): borrar un pago que esta aplicado a una factura
        // es un conflicto con el ESTADO actual del pago, no un dato invalido en el request — no se toco esa
        // semantica, solo el tipo de excepcion que se atrapa.
        catch (SupplierPaymentValidationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    [HttpGet("{publicIdOrLegacyId}/payments")]
    [RequirePermission(Permissions.TesoreriaSupplierPayments)]
    public async Task<ActionResult> GetSupplierPayments(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Supplier>(publicIdOrLegacyId, cancellationToken);
        var payments = await _supplierService.GetSupplierPaymentsHistoryAsync(id, cancellationToken);
        return Ok(payments);
    }

    // ===================================================================================================
    // ADR-041 TANDA 3 (lado proveedor): saldo a favor CONSUMIBLE con un operador. Mismo permiso de
    // tesoreria que los pagos (es plata del lado costo/proveedor). Los montos respetan el masking see_cost
    // dentro del service. Aplicar/revertir son operaciones que mueven la imputacion del saldo a favor.
    // ===================================================================================================

    /// <summary>Saldo a favor disponible con el operador, agrupado por moneda (bolsillos con saldo &gt; 0).</summary>
    [HttpGet("{publicIdOrLegacyId}/credit")]
    [RequirePermission(Permissions.TesoreriaSupplierPayments)]
    public async Task<ActionResult<SupplierCreditOverviewDto>> GetSupplierCredit(
        string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Supplier>(publicIdOrLegacyId, cancellationToken);
            return Ok(await _supplierCreditService.GetSupplierCreditAsync(id, cancellationToken));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    /// <summary>Aplica saldo a favor del operador a OTRA reserva del mismo operador y misma moneda.</summary>
    [HttpPost("{publicIdOrLegacyId}/credit/apply")]
    [RequirePermission(Permissions.TesoreriaSupplierPayments)]
    public async Task<ActionResult<SupplierCreditApplicationResultDto>> ApplySupplierCredit(
        string publicIdOrLegacyId, [FromBody] ApplySupplierCreditRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Supplier>(publicIdOrLegacyId, cancellationToken);
            var (userId, userName) = ResolveActor();
            var result = await _supplierCreditService.ApplyCreditAsync(id, request, userId, userName, cancellationToken);
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
            // Tope superado / moneda cruzada / reserva de otro operador: conflicto de negocio (409).
            return Conflict(new { message = ex.Message });
        }
    }

    /// <summary>Revierte una aplicacion de saldo a favor del operador (repone el pool y deshace la imputacion).</summary>
    [HttpPost("{publicIdOrLegacyId}/credit/applications/{applicationPublicId:guid}/reverse")]
    [RequirePermission(Permissions.TesoreriaSupplierPayments)]
    public async Task<ActionResult<SupplierCreditApplicationResultDto>> ReverseSupplierCreditApplication(
        string publicIdOrLegacyId,
        Guid applicationPublicId,
        [FromBody] ReverseSupplierCreditApplicationRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Supplier>(publicIdOrLegacyId, cancellationToken);
            var (userId, userName) = ResolveActor();
            var result = await _supplierCreditService.ReverseApplicationAsync(
                id, applicationPublicId, request, userId, userName, cancellationToken);
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
            return Conflict(new { message = ex.Message });
        }
    }

    /// <summary>Actor actual (userId, userName) para auditoria de las operaciones de saldo a favor del operador.</summary>
    private (string UserId, string? UserName) ResolveActor()
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "System";
        var userName = User.FindFirst("FullName")?.Value ?? User.FindFirst(ClaimTypes.Name)?.Value;
        return (userId, userName);
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
        // ADR-041 TANDA 5: plazo de pago por defecto (dias) acordado con el operador. null = sin plazo.
        supplier.DefaultPaymentTermDays,
        // Rediseño alta de operador (2026-06-28): moneda por defecto (ISO ARS/USD) para que el form de edicion
        // muestre el valor actual.
        supplier.DefaultCurrency,
        supplier.CurrentBalance,
        supplier.CreatedAt,
        // ADR-044 T3b Decision 3 (2026-07-10): excepcion opcional de "quién asume el ajuste por el dólar" en las
        // multas de ESTE operador. null = hereda el default de la agencia (Configuración operativa). Viaja como
        // el INT del enum (Client=0, Agency=1) — este proyecto no tiene JsonStringEnumConverter configurado,
        // mismo criterio que TreasuryFxAssumedByDefault en OperationalFinanceSettingsDto.
        supplier.TreasuryFxAssumedByOverride,
        // Configuracion de multas de cancelacion (2026-07-14): que tan seguido cobra multa este operador
        // (Unknown=0/RarelyCharges=1/UsuallyCharges=2). Default Unknown = sin pista en el paso de la multa.
        // Viaja como el INT del enum, mismo criterio que TreasuryFxAssumedByOverride de arriba.
        supplier.PenaltyBehavior,
        supplier.InvoicingMode
    };

    private static Supplier MapSupplier(SupplierUpsertRequest request) => new()
    {
        Name = request.Name,
        ContactName = request.ContactName,
        Email = request.Email,
        Phone = request.Phone,
        TaxId = request.TaxId,
        TaxCondition = request.TaxCondition,
        Address = request.Address,
        IsActive = request.IsActive,
        // ADR-041 TANDA 5: plazo de pago por defecto. La validacion (>= 0) la hace el servicio.
        DefaultPaymentTermDays = request.DefaultPaymentTermDays,
        // Rediseño alta de operador (2026-06-28): moneda por defecto. La validacion (moneda soportada) y la
        // normalizacion a ARS si viene vacia las hace el servicio.
        DefaultCurrency = request.DefaultCurrency,
        // ADR-044 T3b Decision 3 (2026-07-10): excepcion opcional del operador. La validacion (que sea un valor
        // definido del enum) la hace el servicio; null siempre es valido (hereda el default de la agencia).
        TreasuryFxAssumedByOverride = request.TreasuryFxAssumedByOverride,
        // Configuracion de multas de cancelacion (2026-07-14): que tan seguido cobra multa este operador. La
        // validacion (que sea un valor definido del enum) la hace el servicio; si el request no lo manda, el
        // binder JSON lo deja en Unknown (default del enum), que es el valor correcto para "sin pista".
        PenaltyBehavior = request.PenaltyBehavior,
        InvoicingMode = request.InvoicingMode ?? SupplierInvoicingMode.TotalToCustomer
    };
}

public record SupplierUpsertRequest(
    string Name,
    string? ContactName,
    string? Email,
    string? Phone,
    string? TaxId,
    string? TaxCondition,
    string? Address,
    bool IsActive = true,
    // ADR-041 TANDA 5: plazo de pago por defecto en dias (opcional). null = sin plazo = comportamiento actual.
    int? DefaultPaymentTermDays = null,
    // Rediseño alta de operador (2026-06-28): moneda por defecto del operador (ISO "ARS"/"USD"). null/vacio
    // = el front no la mando -> el servicio la resuelve a ARS. Si viene un valor no soportado, el servicio
    // rechaza con un mensaje en espanol (validacion server-side, no se confia en el front).
    string? DefaultCurrency = null,
    // ADR-044 T3b Decision 3 (2026-07-10): excepcion opcional de "quién asume el ajuste por el dólar" en las
    // multas de ESTE operador (Client=0 / Agency=1). null = hereda el default de la agencia (Configuración
    // operativa) — es el valor NORMAL, no "campo sin completar": el servicio lo asigna SIEMPRE (a diferencia
    // de DefaultCurrency arriba), asi que mandar null explicito SI limpia una excepcion previa.
    TreasuryFxAssumedBy? TreasuryFxAssumedByOverride = null,
    // Configuracion de multas de cancelacion (2026-07-14): que tan seguido cobra multa este operador
    // (Unknown=0/RarelyCharges=1/UsuallyCharges=2). Default Unknown ("no se sabe") — es el valor NORMAL,
    // el servicio lo asigna SIEMPRE (mismo criterio que TreasuryFxAssumedByOverride de arriba).
    SupplierPenaltyBehavior PenaltyBehavior = SupplierPenaltyBehavior.Unknown,
    // Null = cliente legacy: en update se preserva el valor vigente; en alta se usa compra/reventa.
    SupplierInvoicingMode? InvoicingMode = null);
