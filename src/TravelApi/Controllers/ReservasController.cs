using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using TravelApi.Application.Contracts.Files;
using TravelApi.Application.Contracts.Reservations;
using TravelApi.Application.Contracts.Shared;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Authorization;
using TravelApi.Domain.Entities;
using TravelApi.Errors;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/reservas")]
[Authorize]
public class ReservasController : ControllerBase
{
    private readonly IReservaService _reservaService;
    private readonly IVoucherService _voucherService;
    private readonly ITimelineService _timelineService;
    private readonly ISupplierService _supplierService;
    private readonly IEntityReferenceResolver _entityReferenceResolver;
    private readonly IBookingService _bookingService;
    private readonly ILogger<ReservasController> _logger;

    public ReservasController(
        IReservaService reservaService,
        IVoucherService voucherService,
        ITimelineService timelineService,
        ISupplierService supplierService,
        IEntityReferenceResolver entityReferenceResolver,
        IBookingService bookingService,
        ILogger<ReservasController> logger)
    {
        _reservaService = reservaService;
        _voucherService = voucherService;
        _timelineService = timelineService;
        _supplierService = supplierService;
        _entityReferenceResolver = entityReferenceResolver;
        _bookingService = bookingService;
        _logger = logger;
    }

    [HttpGet]
    [RequirePermission(Permissions.ReservasView)]
    public async Task<IActionResult> GetReservas([FromQuery] ReservaListQuery query, CancellationToken cancellationToken)
    {
        try
        {
            // B1.15 Fase 2a: el service decide si filtra por owner segun permiso
            // del usuario actual (reservas.view_all). Devuelve el scope efectivo
            // para que el frontend pueda mostrar un banner "viendo solo tus reservas".
            var (page, scope) = await _reservaService.GetReservasWithScopeAsync(query, cancellationToken);
            Response.Headers["X-Permission-Scope"] = scope;
            return Ok(page);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error getting reservas");
            if (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
            }

            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudieron obtener las reservas.");
        }
    }

    [HttpGet("{publicIdOrLegacyId}")]
    [RequirePermission(Permissions.ReservasView)]
    [RequireOwnership(OwnedEntity.Reserva, bypassPermission: Permissions.ReservasViewAll)]
    public async Task<IActionResult> GetReserva(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            var dto = await _reservaService.GetReservaByIdAsync(publicIdOrLegacyId, cancellationToken);
            return Ok(dto);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error getting reserva {ReservaId}", publicIdOrLegacyId);
            if (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
            }

            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo obtener la reserva.");
        }
    }

    /// <summary>
    /// "Estado de Cuenta" de la reserva como LIBRO MAYOR (extracto estilo banco): cada factura/ND SUMA, cada
    /// cobro/NC RESTA, con saldo corriente, SEPARADO por moneda. Mismos permisos/ownership que el GET de
    /// detalle (es la misma informacion de venta/cobranza, vista como extracto). Solo venta/cobranza: no
    /// expone costo ni margen.
    /// </summary>
    [HttpGet("{publicIdOrLegacyId}/account-statement")]
    [RequirePermission(Permissions.ReservasView)]
    [RequireOwnership(OwnedEntity.Reserva, bypassPermission: Permissions.ReservasViewAll)]
    public async Task<IActionResult> GetReservaAccountStatement(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            var dto = await _reservaService.GetAccountStatementAsync(publicIdOrLegacyId, cancellationToken);
            return Ok(dto);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error getting account statement for reserva {ReservaId}", publicIdOrLegacyId);
            if (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
            }

            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo obtener el estado de cuenta.");
        }
    }

    /// <summary>
    /// ADR-036 4c: estado "pagado al operador" de cada servicio de la reserva. Por servicio devuelve el
    /// estado (pagado/parcial/impago, visible para todos) y los montos (costo/pagado/saldo, solo con
    /// cobranzas.see_cost). Mismos permisos/ownership que el detalle de la reserva: es informacion de la
    /// reserva, no de la cuenta global del proveedor. Es solo lectura (registrar el pago al operador va por
    /// SuppliersController, gateado por tesoreria.supplier_payments).
    /// </summary>
    [HttpGet("{publicIdOrLegacyId}/supplier-payment-status")]
    [RequirePermission(Permissions.ReservasView)]
    [RequireOwnership(OwnedEntity.Reserva, bypassPermission: Permissions.ReservasViewAll)]
    public async Task<IActionResult> GetReservaSupplierPaymentStatus(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            var reservaId = await _entityReferenceResolver.ResolveRequiredIdAsync<Reserva>(publicIdOrLegacyId, cancellationToken);
            var dto = await _supplierService.GetReservaSupplierPaymentStatusAsync(reservaId, cancellationToken);
            return Ok(dto);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error getting supplier payment status for reserva {ReservaId}", publicIdOrLegacyId);
            if (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
            }

            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo obtener el estado de pago al operador.");
        }
    }

    [HttpGet("{publicIdOrLegacyId}/timeline")]
    [RequirePermission(Permissions.ReservasView)]
    [RequireOwnership(OwnedEntity.Reserva, bypassPermission: Permissions.ReservasViewAll)]
    public async Task<IActionResult> GetReservaTimeline(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            var timeline = await _timelineService.GetTimelineAsync(publicIdOrLegacyId, cancellationToken);
            return Ok(timeline);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error getting timeline for reserva {ReservaId}", publicIdOrLegacyId);
            if (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
            }

            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo obtener el historial.");
        }
    }

    [HttpPost]
    [RequirePermission(Permissions.ReservasEdit)]
    public async Task<IActionResult> CreateReserva(CreateReservaRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var createdByUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var reserva = await _reservaService.CreateReservaAsync(request, createdByUserId, cancellationToken);
            return CreatedAtAction(nameof(GetReserva), new { publicIdOrLegacyId = reserva.PublicId }, reserva);
        }
        catch (ArgumentException ex)
        {
            // Pedido invalido del cliente (ej. SourceLeadPublicId que no resuelve a ningun lead).
            return BadRequest(new { message = ex.Message });
        }
        catch (KeyNotFoundException)
        {
            // Referencia que no existe (ej. PayerId de un cliente inexistente).
            return NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error creating reserva");
            if (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
            }

            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo crear la reserva.");
        }
    }

    [HttpPost("{publicIdOrLegacyId}/services")]
    [RequirePermission(Permissions.ReservasEdit)]
    [RequireOwnership(OwnedEntity.Reserva, bypassPermission: Permissions.ReservasViewAll)]
    public async Task<IActionResult> AddService(string publicIdOrLegacyId, AddServiceRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _reservaService.AddServiceAsync(publicIdOrLegacyId, request, cancellationToken);
            if (!string.IsNullOrWhiteSpace(result.Warning))
            {
                return Ok(new { servicio = result.Servicio, result.Warning });
            }

            return Ok(result.Servicio);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ArgumentException)
        {
            return BadRequest(new { message = "No se pudo agregar el servicio." });
        }
        catch (InvalidOperationException ex)
        {
            // ADR-020 F4: reserva confirmada con candado (o agregar servicio dispara regresion sin
            // autorizacion). 409 Conflict, mismo patron que el resto de los write-paths.
            return Conflict(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error adding service to reserva {ReservaId}", publicIdOrLegacyId);
            if (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
            }

            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo agregar el servicio.");
        }
    }

    // B1.15 Fase 2a (FIX 9): ownership por Servicio. El resolver hace el join
    // Servicio -> Reserva.ResponsibleUserId. bypassPermission permite a Admin/
    // Colaborador con reservas.view_all editar cualquier servicio.
    [HttpPut("services/{servicePublicIdOrLegacyId}")]
    [RequirePermission(Permissions.ReservasEdit)]
    [RequireOwnership(OwnedEntity.Servicio, "servicePublicIdOrLegacyId", bypassPermission: Permissions.ReservasViewAll)]
    public async Task<IActionResult> UpdateService(string servicePublicIdOrLegacyId, AddServiceRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var service = await _reservaService.UpdateServiceAsync(servicePublicIdOrLegacyId, request, cancellationToken);
            return Ok(service);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ArgumentException)
        {
            return BadRequest(new { message = "No se pudo actualizar el servicio." });
        }
        catch (InvalidOperationException ex)
        {
            // B1.15 Fase 0' (CODE-05): MutationGuards rechaza con factura AFIP
            // viva o voucher Issued. 409 Conflict (mismo patron que DeleteGuards).
            return Conflict(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error updating service {ServiceId}", servicePublicIdOrLegacyId);
            if (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
            }

            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo actualizar el servicio.");
        }
    }

    [HttpPut("{publicIdOrLegacyId}/services/{servicePublicIdOrLegacyId}")]
    [RequirePermission(Permissions.ReservasEdit)]
    [RequireOwnership(OwnedEntity.Reserva, bypassPermission: Permissions.ReservasViewAll)]
    public Task<IActionResult> UpdateNestedService(
        string publicIdOrLegacyId,
        string servicePublicIdOrLegacyId,
        AddServiceRequest request,
        CancellationToken cancellationToken)
    {
        _ = publicIdOrLegacyId;
        return UpdateService(servicePublicIdOrLegacyId, request, cancellationToken);
    }

    // TODO B1.15 Fase 2a: ownership en service layer (solo serviceId en ruta).
    // Mantenemos Admin-only como defense-in-depth hasta resolver via service.
    [HttpDelete("services/{servicePublicIdOrLegacyId}")]
    [RequirePermission(Permissions.ReservasDelete)]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> RemoveService(string servicePublicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            await _reservaService.RemoveServiceAsync(servicePublicIdOrLegacyId, cancellationToken);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error removing service {ServiceId}", servicePublicIdOrLegacyId);
            if (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
            }

            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo eliminar el servicio.");
        }
    }

    [HttpDelete("{publicIdOrLegacyId}/services/{servicePublicIdOrLegacyId}")]
    [RequirePermission(Permissions.ReservasDelete)]
    [RequireOwnership(OwnedEntity.Reserva, bypassPermission: Permissions.ReservasViewAll)]
    [Authorize(Roles = "Admin")]
    public Task<IActionResult> RemoveNestedService(
        string publicIdOrLegacyId,
        string servicePublicIdOrLegacyId,
        CancellationToken cancellationToken)
    {
        _ = publicIdOrLegacyId;
        return RemoveService(servicePublicIdOrLegacyId, cancellationToken);
    }

    [HttpGet("{publicIdOrLegacyId}/passengers")]
    [RequirePermission(Permissions.ReservasView)]
    [RequireOwnership(OwnedEntity.Reserva, bypassPermission: Permissions.ReservasViewAll)]
    public async Task<ActionResult> GetPassengers(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        var passengers = await _reservaService.GetPassengersAsync(publicIdOrLegacyId, cancellationToken);
        return Ok(passengers);
    }

    [HttpPost("{publicIdOrLegacyId}/passengers")]
    [RequirePermission(Permissions.ReservasEdit)]
    [RequireOwnership(OwnedEntity.Reserva, bypassPermission: Permissions.ReservasViewAll)]
    public async Task<ActionResult> AddPassenger(string publicIdOrLegacyId, PassengerUpsertRequest passenger, CancellationToken cancellationToken)
    {
        try
        {
            var dto = await _reservaService.AddPassengerAsync(publicIdOrLegacyId, passenger, cancellationToken);
            return CreatedAtAction(nameof(GetReserva), new { publicIdOrLegacyId }, dto);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (DbUpdateException ex) when (!DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
        {
            // BUG 4 (2026-06-08): un fallo al persistir que NO es de conectividad (violacion de constraint,
            // dato fuera de rango, FK invalida) es un error de DATOS del request, no "base no disponible".
            // Antes el catch-all lo clasificaba como 503 porque PostgresException hereda de NpgsqlException.
            // Ahora devolvemos 422 con un mensaje claro y logueamos el detalle REAL (SqlState + inner)
            // para diagnosticar — sin volcar PII del pasajero (nombre/documento).
            LogPassengerPersistenceFailure(ex, "adding", publicIdOrLegacyId);
            return UnprocessableEntity(new { message = "No se pudo guardar el pasajero: los datos no cumplen una restriccion de la base. Revisá los campos e intentá de nuevo." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error adding passenger to reserva {ReservaId}", publicIdOrLegacyId);
            if (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
            }

            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo agregar el pasajero.");
        }
    }

    /// <summary>
    /// BUG 4 (2026-06-08): loguea el detalle REAL de un fallo al persistir un pasajero (SqlState +
    /// mensaje del servidor Postgres + inner exception), SIN exponer PII (nombre/documento). Sirve para
    /// distinguir en los logs un constraint/dato invalido de una caida de base. No loguea el payload.
    /// </summary>
    private void LogPassengerPersistenceFailure(DbUpdateException ex, string operation, string reservaOrPassengerRef)
    {
        var sqlState = (ex.InnerException as Npgsql.PostgresException)?.SqlState;
        var innerMessage = ex.InnerException?.Message;
        _logger.LogError(ex,
            "Passenger persistence failed while {Operation}. Ref={Ref} SqlState={SqlState} Inner={Inner}",
            operation, reservaOrPassengerRef, sqlState, innerMessage);
    }

    // ============= Phase 2.1 — Pasajero <-> Servicio =============

    [HttpGet("{publicIdOrLegacyId}/assignments")]
    [RequirePermission(Permissions.ReservasView)]
    [RequireOwnership(OwnedEntity.Reserva, bypassPermission: Permissions.ReservasViewAll)]
    public async Task<ActionResult<IReadOnlyList<PassengerServiceAssignmentDto>>> GetAssignments(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            var list = await _reservaService.GetAssignmentsAsync(publicIdOrLegacyId, cancellationToken);
            return Ok(list);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPost("{publicIdOrLegacyId}/assignments")]
    [RequirePermission(Permissions.ReservasEdit)]
    [RequireOwnership(OwnedEntity.Reserva, bypassPermission: Permissions.ReservasViewAll)]
    public async Task<ActionResult<PassengerServiceAssignmentDto>> CreateAssignment(string publicIdOrLegacyId, [FromBody] CreatePassengerAssignmentRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var dto = await _reservaService.CreateAssignmentAsync(publicIdOrLegacyId, request, cancellationToken);
            return Created($"/api/reservas/assignments/{dto.PublicId}", dto);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    // B1.15 Fase 2a (FIX 9): ownership por Assignment. Resolver hace el doble
    // join Assignment -> Passenger -> Reserva.ResponsibleUserId.
    [HttpDelete("assignments/{assignmentPublicIdOrLegacyId}")]
    [RequirePermission(Permissions.ReservasEdit)]
    [RequireOwnership(OwnedEntity.Assignment, "assignmentPublicIdOrLegacyId", bypassPermission: Permissions.ReservasViewAll)]
    public async Task<IActionResult> RemoveAssignment(string assignmentPublicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            await _reservaService.RemoveAssignmentAsync(assignmentPublicIdOrLegacyId, cancellationToken);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    // ADR-031 v2.1: cobertura nominal POR SERVICIO. El front la usa para el mini-form de nombres: que
    // pasajeros integran el SET del servicio (X de N) y que datos faltan, segun el tipo. Mismo gate de
    // ownership/permiso que GetAssignments — expone nombres (sensibles) pero NUNCA numeros de documento.
    [HttpGet("{publicIdOrLegacyId}/services/{serviceType}/{servicePublicIdOrLegacyId}/nominal-coverage")]
    [RequirePermission(Permissions.ReservasView)]
    [RequireOwnership(OwnedEntity.Reserva, bypassPermission: Permissions.ReservasViewAll)]
    public async Task<ActionResult<ServiceNominalCoverageDto>> GetServiceNominalCoverage(
        string publicIdOrLegacyId, string serviceType, string servicePublicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            var dto = await _reservaService.GetServiceNominalCoverageAsync(
                publicIdOrLegacyId, serviceType, servicePublicIdOrLegacyId, cancellationToken);
            return Ok(dto);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    // ADR-031 v2.1: REEMPLAZO TOTAL ATOMICO del set de pasajeros de un servicio. El front antes hacia
    // "borrar todas las asignaciones + crear N" en llamadas separadas; si fallaba a la mitad el set quedaba
    // inconsistente. Este endpoint hace todo en UNA transaccion (o entra todo o no entra nada) y devuelve el
    // ServiceNominalCoverageDto resultante (mismo shape que el GET) para que el front no re-pida. Mismo gate
    // de edicion + ownership que CreateAssignment (ReservasEdit + RequireOwnership de la Reserva).
    [HttpPut("{publicIdOrLegacyId}/services/{serviceType}/{servicePublicIdOrLegacyId}/assignments")]
    [RequirePermission(Permissions.ReservasEdit)]
    [RequireOwnership(OwnedEntity.Reserva, bypassPermission: Permissions.ReservasViewAll)]
    public async Task<ActionResult<ServiceNominalCoverageDto>> ReplaceServiceAssignments(
        string publicIdOrLegacyId, string serviceType, string servicePublicIdOrLegacyId,
        [FromBody] ReplaceServiceAssignmentsRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var dto = await _reservaService.ReplaceServiceAssignmentsAsync(
                publicIdOrLegacyId, serviceType, servicePublicIdOrLegacyId, request, cancellationToken);
            return Ok(dto);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    // ============= /Phase 2.1 =============

    // ============= Phase 2.4 — Revert status con autorizacion =============

    [HttpGet("{publicIdOrLegacyId}/revert-options")]
    [RequirePermission(Permissions.ReservasView)]
    [RequireOwnership(OwnedEntity.Reserva, bypassPermission: Permissions.ReservasViewAll)]
    public async Task<ActionResult<RevertOptionsDto>> GetRevertOptions(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            var actorUserId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "System";
            var isAdmin = User.IsInRole("Admin");
            var dto = await _reservaService.GetRevertOptionsAsync(publicIdOrLegacyId, actorUserId, isAdmin, cancellationToken);
            return Ok(dto);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPost("{publicIdOrLegacyId}/revert-status")]
    [RequirePermission(Permissions.ReservasEdit)]
    [RequireOwnership(OwnedEntity.Reserva, bypassPermission: Permissions.ReservasViewAll)]
    public async Task<ActionResult<ReservaDto>> RevertStatus(string publicIdOrLegacyId, [FromBody] RevertStatusRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var actorUserId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "System";
            var actorUserName = User.FindFirstValue("FullName") ?? User.FindFirstValue(ClaimTypes.Name) ?? User.Identity?.Name;
            var isAdmin = User.IsInRole("Admin");
            var dto = await _reservaService.RevertStatusAsync(publicIdOrLegacyId, request, actorUserId, actorUserName, isAdmin, cancellationToken);
            return Ok(dto);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    // ADR-036 (2026-06-22): "Sacar de viaje" — correccion de EXCEPCION de una reserva que entro a "En viaje"
    // por error (fecha mal cargada o el viaje no salio). Endpoint DEDICADO (no reabre la matriz de revert, que
    // a proposito ya no tiene Traveling->Confirmed). Gate de permiso reservas.correct_traveling (solo Admin) +
    // ownership (bypass con view_all). El SERVICE valida las reglas de negocio (estado En viaje, sin factura
    // viva, sin voucher vivo, motivo) que NO son bypasseables. Mapeo de excepciones igual que RevertStatus.
    [HttpPost("{publicIdOrLegacyId}/correct-traveling-entry")]
    [RequirePermission(Permissions.ReservasCorrectTraveling)]
    [RequireOwnership(OwnedEntity.Reserva, bypassPermission: Permissions.ReservasViewAll)]
    public async Task<ActionResult<ReservaDto>> CorrectTravelingEntry(
        string publicIdOrLegacyId, [FromBody] CorrectTravelingEntryRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var actorUserId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "System";
            var actorUserName = User.FindFirstValue("FullName") ?? User.FindFirstValue(ClaimTypes.Name) ?? User.Identity?.Name;
            var dto = await _reservaService.CorrectTravelingEntryAsync(
                publicIdOrLegacyId, request, actorUserId, actorUserName, cancellationToken);
            return Ok(dto);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    // (2026-06-25) Caso (3) del flujo unificado de "Anular reserva": anular una reserva en firme SIN factura con
    // CAE vivo pero CON cobros vivos -> pasa a Cancelada y la plata cobrada queda como SALDO A FAVOR del cliente
    // (reutilizable), SIN emitir Nota de Credito. Endpoint DEDICADO: es plata sensible, no se mezcla con el
    // PUT /status generico. Gate de permiso reservas.cancel_with_payment (anular con plata exige la autorizacion
    // reforzada) + ownership (bypass con view_all). El SERVICE revalida server-side TODO lo no bypasseable:
    // estado firme, SIN factura CAE viva (si la hay -> 409, derivar al camino formal de NC) y CON cobros vivos.
    [HttpPost("{publicIdOrLegacyId}/annul-with-credit")]
    [RequirePermission(Permissions.ReservasCancelWithPayment)]
    [RequireOwnership(OwnedEntity.Reserva, bypassPermission: Permissions.ReservasViewAll)]
    public async Task<ActionResult<ReservaDto>> AnnulWithCredit(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            var actorUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var actorUserName = User.FindFirstValue("FullName") ?? User.FindFirstValue(ClaimTypes.Name) ?? User.Identity?.Name;
            var dto = await _reservaService.AnnulWithPaymentsToCreditAsync(
                publicIdOrLegacyId, actorUserId, actorUserName, cancellationToken);
            return Ok(dto);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            // Precondiciones de negocio (estado no firme, factura CAE viva, sin cobros): conflicto de estado.
            return Conflict(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error annulling reserva with credit {ReservaId}", publicIdOrLegacyId);
            if (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
            }

            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo anular la reserva.");
        }
    }

    // ADR-020 F4 (candado): destrabar la edicion de una reserva confirmada. Cualquiera que pueda
    // editar la reserva (ReservasEdit + ownership) puede pedir la autorizacion; el SERVICE valida
    // que quien autoriza tenga reservas.authorize_locked_edit (auto-autorizacion si el actor lo
    // tiene, Admin incluido — siempre queda registrado). Devuelve la ventana viva.
    [HttpPost("{publicIdOrLegacyId}/edit-authorizations")]
    [RequirePermission(Permissions.ReservasEdit)]
    [RequireOwnership(OwnedEntity.Reserva, bypassPermission: Permissions.ReservasViewAll)]
    public async Task<ActionResult<ReservaEditAuthorizationDto>> CreateEditAuthorization(
        string publicIdOrLegacyId, [FromBody] CreateEditAuthorizationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var actorUserId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "System";
            var actorUserName = User.FindFirstValue("FullName") ?? User.FindFirstValue(ClaimTypes.Name) ?? User.Identity?.Name;
            var isAdmin = User.IsInRole("Admin");
            var dto = await _reservaService.CreateEditAuthorizationAsync(
                publicIdOrLegacyId, request, actorUserId, actorUserName, isAdmin, cancellationToken);
            return Ok(dto);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    // ============= /Phase 2.4 =============

    // ADR-027 (auditoria ERP, hallazgo #10): el dueño da el OK a una reserva "confirmada con cambios".
    //
    // Gate (decision del dueño, auditoria ERP 2026-06-13): SOLO un administrador puede dar el OK. Antes
    // estaba gateado con ReservasEdit (que tiene el Vendedor) + ownership, asi que el propio vendedor podia
    // auto-acusar el cambio de precio/costo de SU reserva, anulando el control. Ahora exige rol Admin: el
    // OK es una validacion del responsable del negocio, no del vendedor que hizo (o sufrio) el cambio. Se
    // quita el RequireOwnership porque el Admin opera sobre TODAS las reservas (no hay scope por vendedor
    // para esta accion). Limpia la marca y registra quien/cuando. Idempotente: acusar una reserva no marcada
    // devuelve 200 sin tocar nada (no es error). El candado de edicion (ADR-020 F4) NO aplica: dar el OK NO
    // edita servicios.
    [HttpPost("{publicIdOrLegacyId}/acknowledge-changes")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<ReservaDto>> AcknowledgeChanges(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            var actorUserId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "System";
            var actorUserName = User.FindFirstValue("FullName") ?? User.FindFirstValue(ClaimTypes.Name) ?? User.Identity?.Name;
            var dto = await _reservaService.AcknowledgeChangesAsync(publicIdOrLegacyId, actorUserId, actorUserName, cancellationToken);
            return Ok(dto);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpGet("{publicIdOrLegacyId}/transition-readiness")]
    [RequirePermission(Permissions.ReservasView)]
    [RequireOwnership(OwnedEntity.Reserva, bypassPermission: Permissions.ReservasViewAll)]
    public async Task<ActionResult<TransitionReadinessDto>> GetTransitionReadiness(string publicIdOrLegacyId, [FromQuery] string to, CancellationToken cancellationToken)
    {
        try
        {
            var dto = await _reservaService.GetTransitionReadinessAsync(publicIdOrLegacyId, to, cancellationToken);
            return Ok(dto);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPatch("{publicIdOrLegacyId}/dates")]
    [RequirePermission(Permissions.ReservasEdit)]
    [RequireOwnership(OwnedEntity.Reserva, bypassPermission: Permissions.ReservasViewAll)]
    public async Task<ActionResult> UpdateDates(string publicIdOrLegacyId, UpdateReservaDatesRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var dto = await _reservaService.UpdateDatesAsync(publicIdOrLegacyId, request, cancellationToken);
            return Ok(dto);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    // REPROGRAMAR VIAJE (2026-06-23): mueve JUNTAS las fechas de todos los servicios de la reserva por un
    // desplazamiento de N dias (o derivando el shift de una nueva fecha de salida). Misma autorizacion que
    // editar un servicio: ReservasEdit + ownership de la reserva. Los guards de negocio (estado terminal,
    // candado de autorizacion en Confirmada, factura/voucher emitidos) los aplica el BookingService y se
    // mapean a 409. No toca plata. El front muestra el boton segun Capabilities.CanEditServices.
    [HttpPost("{publicIdOrLegacyId}/reschedule")]
    [RequirePermission(Permissions.ReservasEdit)]
    [RequireOwnership(OwnedEntity.Reserva, bypassPermission: Permissions.ReservasViewAll)]
    public async Task<ActionResult<RescheduleReservaResult>> Reschedule(
        string publicIdOrLegacyId, [FromBody] RescheduleReservaRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _bookingService.RescheduleAsync(publicIdOrLegacyId, request, cancellationToken);
            return Ok(result);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ArgumentException ex)
        {
            // Request mal formado (los dos modos, ninguno, o nueva fecha sin salida desde donde derivar).
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            // Candado por estado / candado de autorizacion / guard fiscal (CAE-voucher) -> 409, mismo
            // contrato que la edicion de un servicio individual.
            return Conflict(new { message = ex.Message });
        }
    }

    [HttpPatch("{publicIdOrLegacyId}/passenger-counts")]
    [RequirePermission(Permissions.ReservasEdit)]
    [RequireOwnership(OwnedEntity.Reserva, bypassPermission: Permissions.ReservasViewAll)]
    public async Task<ActionResult> UpdatePassengerCounts(string publicIdOrLegacyId, PassengerCountsRequest counts, CancellationToken cancellationToken)
    {
        try
        {
            var dto = await _reservaService.UpdatePassengerCountsAsync(publicIdOrLegacyId, counts, cancellationToken);
            return Ok(dto);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error updating passenger counts for reserva {ReservaId}", publicIdOrLegacyId);
            if (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
            }

            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudieron actualizar las cantidades.");
        }
    }

    // B1.15 Fase 2a (FIX 9): ownership por Passenger. Resolver hace el join
    // Passenger -> Reserva.ResponsibleUserId. bypassPermission para roles con view_all.
    [HttpPut("passengers/{passengerPublicIdOrLegacyId}")]
    [RequirePermission(Permissions.ReservasEdit)]
    [RequireOwnership(OwnedEntity.Passenger, "passengerPublicIdOrLegacyId", bypassPermission: Permissions.ReservasViewAll)]
    public async Task<ActionResult> UpdatePassenger(string passengerPublicIdOrLegacyId, PassengerUpsertRequest updated, CancellationToken cancellationToken)
    {
        try
        {
            var dto = await _reservaService.UpdatePassengerAsync(passengerPublicIdOrLegacyId, updated, cancellationToken);
            return Ok(dto);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ArgumentException)
        {
            return BadRequest(new { message = "No se pudo actualizar el pasajero." });
        }
        catch (InvalidOperationException ex)
        {
            // B1.15 Fase 0' (CODE-14): MutationGuards rechaza cambios de datos
            // personales con voucher emitido o factura AFIP viva. 409 Conflict.
            return Conflict(new { message = ex.Message });
        }
        catch (DbUpdateException ex) when (!DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
        {
            // BUG 4 (2026-06-08): constraint/dato invalido al persistir = error de datos (422), no 503.
            // Ver AddPassenger para el detalle del bug y LogPassengerPersistenceFailure.
            LogPassengerPersistenceFailure(ex, "updating", passengerPublicIdOrLegacyId);
            return UnprocessableEntity(new { message = "No se pudo guardar el pasajero: los datos no cumplen una restriccion de la base. Revisá los campos e intentá de nuevo." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error updating passenger {PassengerId}", passengerPublicIdOrLegacyId);
            if (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
            }

            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo actualizar el pasajero.");
        }
    }

    // TODO B1.15 Fase 2a: ownership en service layer (solo passengerId en ruta).
    // Mantenemos Admin-only como defense-in-depth hasta resolver via service.
    [HttpDelete("passengers/{passengerPublicIdOrLegacyId}")]
    [RequirePermission(Permissions.ReservasDelete)]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> RemovePassenger(string passengerPublicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            await _reservaService.RemovePassengerAsync(passengerPublicIdOrLegacyId, cancellationToken);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error removing passenger {PassengerId}", passengerPublicIdOrLegacyId);
            if (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
            }

            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo eliminar el pasajero.");
        }
    }

    [HttpGet("{publicIdOrLegacyId}/payments")]
    [RequirePermission(Permissions.CobranzasView)]
    [RequireOwnership(OwnedEntity.Reserva, bypassPermission: Permissions.CobranzasViewAll)]
    public async Task<ActionResult> GetReservaPayments(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        var payments = await _reservaService.GetReservaPaymentsAsync(publicIdOrLegacyId, cancellationToken);
        return Ok(payments);
    }

    [HttpPost("{publicIdOrLegacyId}/payments")]
    [RequirePermission(Permissions.CobranzasEdit)]
    [RequireOwnership(OwnedEntity.Reserva, bypassPermission: Permissions.CobranzasViewAll)]
    public async Task<ActionResult> AddPayment(string publicIdOrLegacyId, ReservationPaymentUpsertRequest payment, CancellationToken cancellationToken)
    {
        try
        {
            var dto = await _reservaService.AddPaymentAsync(publicIdOrLegacyId, payment, cancellationToken);
            return CreatedAtAction(nameof(GetReserva), new { publicIdOrLegacyId }, dto);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error adding payment to reserva {ReservaId}", publicIdOrLegacyId);
            if (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
            }

            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo registrar el pago.");
        }
    }

    [HttpPut("{publicIdOrLegacyId}/payments/{paymentPublicIdOrLegacyId}")]
    [RequirePermission(Permissions.CobranzasEdit)]
    [RequireOwnership(OwnedEntity.Reserva, bypassPermission: Permissions.CobranzasViewAll)]
    public async Task<ActionResult> UpdatePayment(string publicIdOrLegacyId, string paymentPublicIdOrLegacyId, ReservationPaymentUpsertRequest updatedPayment, CancellationToken cancellationToken)
    {
        try
        {
            var dto = await _reservaService.UpdatePaymentAsync(publicIdOrLegacyId, paymentPublicIdOrLegacyId, updatedPayment, cancellationToken);
            return Ok(dto);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ArgumentException)
        {
            return BadRequest(new { message = "No se pudo actualizar el pago." });
        }
        catch (InvalidOperationException ex)
        {
            // B1.15 Fase 0' (CODE-01): MutationGuards rechaza editar pagos con
            // recibo emitido o factura AFIP viva. 409 Conflict.
            return Conflict(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error updating payment {PaymentId} for reserva {ReservaId}", paymentPublicIdOrLegacyId, publicIdOrLegacyId);
            if (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
            }

            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo actualizar el pago.");
        }
    }

    // B1.15 Fase 2a: NO tocar este endpoint. Fase 2b lo reemplaza por
    // POST /api/payments/{id}/annul. Mantenemos Admin-only como defense-in-depth
    // hasta entonces. RequirePermission(cobranzas.edit) + Admin role = AND.
    [HttpDelete("{publicIdOrLegacyId}/payments/{paymentPublicIdOrLegacyId}")]
    [RequirePermission(Permissions.CobranzasEdit)]
    [RequireOwnership(OwnedEntity.Reserva, bypassPermission: Permissions.CobranzasViewAll)]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> DeletePayment(string publicIdOrLegacyId, string paymentPublicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            await _reservaService.DeletePaymentAsync(publicIdOrLegacyId, paymentPublicIdOrLegacyId, cancellationToken);
            return Ok();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ArgumentException)
        {
            return BadRequest(new { message = "No se pudo eliminar el pago." });
        }
        catch (InvalidOperationException ex)
        {
            // 409 Conflict: el pago tiene recibo o factura asociada (C28).
            return Conflict(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error deleting payment {PaymentId} for reserva {ReservaId}", paymentPublicIdOrLegacyId, publicIdOrLegacyId);
            if (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
            }

            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo eliminar el pago.");
        }
    }

    [HttpPut("{publicIdOrLegacyId}/status")]
    [RequirePermission(Permissions.ReservasEdit)]
    [RequireOwnership(OwnedEntity.Reserva, bypassPermission: Permissions.ReservasViewAll)]
    public async Task<IActionResult> UpdateStatus(string publicIdOrLegacyId, [FromBody] StatusUpdateRequest request, CancellationToken cancellationToken)
    {
        try
        {
            // B1.15 Fase 2a (Decision 6): el service valida reservas.cancel y
            // reservas.cancel_with_payment cuando target == Cancelled.
            var actorUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var reserva = await _reservaService.UpdateStatusAsync(publicIdOrLegacyId, request.Status, actorUserId, cancellationToken);
            return Ok(reserva);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (UnauthorizedAccessException ex)
        {
            // B1.15 Fase 2a: el service traduce permisos faltantes a 403.
            return StatusCode(StatusCodes.Status403Forbidden, new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error updating status for reserva {ReservaId}", publicIdOrLegacyId);
            if (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
            }

            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo actualizar el estado de la reserva.");
        }
    }

    [HttpPut("{publicIdOrLegacyId}/archive")]
    [RequirePermission(Permissions.ReservasEdit)]
    [RequireOwnership(OwnedEntity.Reserva, bypassPermission: Permissions.ReservasViewAll)]
    public async Task<IActionResult> ArchiveReserva(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            var reserva = await _reservaService.ArchiveReservaAsync(publicIdOrLegacyId, cancellationToken);
            return Ok(reserva);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error archiving reserva {ReservaId}", publicIdOrLegacyId);
            if (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
            }

            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo archivar la reserva.");
        }
    }

    // B1.15 Fase 2a: defense-in-depth — el permiso reservas.delete + Admin role
    // se requieren ambos (AND). Permite mantener compat con tests historicos
    // que asumen Admin-only para borrado destructivo.
    [HttpDelete("{publicIdOrLegacyId}")]
    [RequirePermission(Permissions.ReservasDelete)]
    [RequireOwnership(OwnedEntity.Reserva, bypassPermission: Permissions.ReservasViewAll)]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> DeleteReserva(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            await _reservaService.DeleteReservaAsync(publicIdOrLegacyId, cancellationToken);
            return Ok();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            // 409 Conflict: el recurso existe pero su estado actual impide la operacion
            // (estado de la reserva no es Budget, hay pagos, vouchers o facturas).
            return Conflict(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error deleting reserva {ReservaId}", publicIdOrLegacyId);
            if (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
            }

            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo eliminar la reserva.");
        }
    }

    [HttpGet("{publicIdOrLegacyId}/voucher")]
    [RequirePermission(Permissions.VouchersGenerate)]
    [RequireOwnership(OwnedEntity.Reserva)]
    public async Task<IActionResult> GenerateVoucher(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            var htmlBytes = await _voucherService.GenerateVoucherHtmlAsync(publicIdOrLegacyId, cancellationToken);
            return File(htmlBytes, "text/html", $"voucher-{publicIdOrLegacyId}.html");
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error generating voucher for reserva {ReservaId}", publicIdOrLegacyId);
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo generar el voucher.");
        }
    }

    [HttpGet("{publicIdOrLegacyId}/voucher/preview")]
    [RequirePermission(Permissions.VouchersGenerate)]
    [RequireOwnership(OwnedEntity.Reserva)]
    public async Task<IActionResult> GetVoucherHtmlPreview(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            var htmlBytes = await _voucherService.GenerateVoucherHtmlAsync(publicIdOrLegacyId, cancellationToken);
            var html = System.Text.Encoding.UTF8.GetString(htmlBytes);
            return Ok(new { html });
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error generating voucher preview for reserva {ReservaId}", publicIdOrLegacyId);
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo generar la vista previa del voucher.");
        }
    }

    [HttpGet("{publicIdOrLegacyId}/voucher/pdf")]
    [RequirePermission(Permissions.VouchersGenerate)]
    [RequireOwnership(OwnedEntity.Reserva)]
    public async Task<IActionResult> GenerateVoucherPdf(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            var pdf = await _voucherService.GenerateVoucherPdfAsync(publicIdOrLegacyId, cancellationToken);
            return File(pdf, "application/pdf", $"voucher-{publicIdOrLegacyId}.pdf");
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error generating voucher PDF for reserva {ReservaId}", publicIdOrLegacyId);
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo generar el voucher PDF.");
        }
    }

}
