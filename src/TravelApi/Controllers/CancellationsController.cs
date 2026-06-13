using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TravelApi.Application.DTOs;
using TravelApi.Application.DTOs.Cancellation;
using TravelApi.Application.Exceptions;
using TravelApi.Application.Interfaces;
using TravelApi.Authorization;
using TravelApi.Domain.Entities;
using TravelApi.Errors;

namespace TravelApi.Controllers;

/// <summary>
/// FC1.2.4 v3 (2026-05-18): expone el flujo de cancelacion de reservas
/// (BookingCancellation) por HTTP. El controller es thin: parsea request,
/// invoca al service, traduce excepciones a HTTP. La logica de negocio vive
/// 100% en <see cref="IBookingCancellationService"/>.
///
/// <para>
/// <b>Diseño de permisos</b> (decision de auditoria 2026-05-18):
/// reusamos los permisos existentes de B1.15 — <c>reservas.cancel</c> ya esta
/// pensado para "el usuario puede cancelar reservas". No creamos
/// <c>cancellations.create</c>/<c>cancellations.confirm</c>/etc nuevos porque:
/// <list type="bullet">
///   <item>El modulo BC reemplaza el cancel legacy: una sola politica de quien
///         puede cancelar es mas simple que dos modulos con permisos paralelos.</item>
///   <item>El gating fino (cancelar reserva con pagos -> approval, override de
///         invariantes -> approval) lo decide el service via
///         <c>ApprovalRequiredException</c>, no el controller.</item>
/// </list>
/// La unica excepcion es <c>CancellationsForceArcaConfirmation</c> que existe
/// como permiso dedicado (Admin-only, escape hatch fiscal) creado en FC1.2.1.
/// </para>
///
/// <para>
/// <b>Ownership</b>: el responsable del BC es el responsable de la reserva
/// padre. <see cref="OwnedEntity.BookingCancellation"/> ya esta soportado por
/// <c>OwnershipResolver</c> (FC1.2.0). El POST de creacion no usa el decorator
/// porque el id viene en el body, no en la ruta — la validacion se hace
/// manualmente con <see cref="IOwnershipResolver"/>.
/// </para>
/// </summary>
[ApiController]
[Route("api/cancellations")]
[Authorize]
public class CancellationsController : ControllerBase
{
    private readonly IBookingCancellationService _bcService;
    private readonly IOwnershipResolver _ownershipResolver;
    // ADR-013: para resolver server-side si el usuario puede clasificar la penalidad
    // como ingreso propio de la agencia (permiso elevado que dispara la ND fiscal).
    private readonly IUserPermissionResolver _permissionResolver;

    public CancellationsController(
        IBookingCancellationService bcService,
        IOwnershipResolver ownershipResolver,
        IUserPermissionResolver permissionResolver)
    {
        _bcService = bcService;
        _ownershipResolver = ownershipResolver;
        _permissionResolver = permissionResolver;
    }

    /// <summary>
    /// T-1: crea un BookingCancellation en estado <c>Drafted</c>.
    /// El usuario puede arrepentirse (abort) hasta T0 (Confirm).
    /// </summary>
    [HttpPost]
    [RequirePermission(Permissions.ReservasCancel)]
    public async Task<ActionResult<BookingCancellationDto>> Draft(
        DraftCancellationRequest request,
        CancellationToken cancellationToken)
    {
        // El body trae el ReservaPublicId — no podemos usar el decorator de
        // ownership que solo lee de route params. Validamos a mano contra la
        // reserva original, con bypass por ReservasViewAll (mismo patron que
        // PaymentService.CreatePaymentAsync que usa el service para validar).
        if (!await UserIsAllowedOverReservaAsync(request.ReservaPublicId.ToString(), cancellationToken))
        {
            return new ObjectResult(PermissionDeniedProblemFactory.OwnershipRequired(OwnedEntity.Reserva.ToString()))
            {
                StatusCode = StatusCodes.Status403Forbidden
            };
        }

        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "System";
        var userName = User.FindFirst("FullName")?.Value ?? User.FindFirst(ClaimTypes.Name)?.Value;

        try
        {
            var dto = await _bcService.DraftAsync(request, userId, userName, cancellationToken);
            return CreatedAtAction(
                actionName: nameof(GetByPublicId),
                routeValues: new { publicId = dto.PublicId },
                value: dto);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            // Feature flag off, reserva sin invoice activa, multiples invoices
            // con OnePerReservaInvoicePolicy on, etc. 409 expresa "estado actual
            // incompatible con la operacion" — coherente con PaymentsController.
            return Conflict(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        // BusinessInvariantViolationException lo atrapa GlobalExceptionHandler
        // (409 con invariantCode + constraintName). No la catcheamos aca.
        catch (Exception ex) when (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
        }
    }

    /// <summary>
    /// ADR-025 (DT.3.1): cancela UN servicio dentro de una reserva (cancelacion PARCIAL). El resto del
    /// file sigue vivo y la reserva NO cambia de estado (decision #1). Baja el saldo del cliente del
    /// servicio cancelado y la deuda del operador de ESE servicio (B1), en la misma operacion. NO emite
    /// NC automatica (decision #3): el calculo fiscal queda en revision manual.
    ///
    /// <para>Mismo permiso que cancelar una reserva (<c>reservas.cancel</c>). El ReservaPublicId viene en
    /// el body, asi que se valida ownership a mano (igual que Draft). El service revalida server-side que
    /// el servicio pertenece a la reserva.</para>
    /// </summary>
    [HttpPost("cancel-service")]
    [RequirePermission(Permissions.ReservasCancel)]
    public async Task<ActionResult<CancelServiceResultDto>> CancelService(
        CancelServiceRequest request,
        CancellationToken cancellationToken)
    {
        if (!await UserIsAllowedOverReservaAsync(request.ReservaPublicId.ToString(), cancellationToken))
        {
            return new ObjectResult(PermissionDeniedProblemFactory.OwnershipRequired(OwnedEntity.Reserva.ToString()))
            {
                StatusCode = StatusCodes.Status403Forbidden
            };
        }

        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "System";
        var userName = User.FindFirst("FullName")?.Value ?? User.FindFirst(ClaimTypes.Name)?.Value;

        try
        {
            var result = await _bcService.CancelServiceAsync(request, userId, userName, cancellationToken);
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
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        // BusinessInvariantViolationException la atrapa GlobalExceptionHandler (409 con invariantCode).
        catch (Exception ex) when (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
        }
    }

    /// <summary>
    /// T0: transiciona <c>Drafted</c> → <c>AwaitingFiscalConfirmation</c>.
    /// Encola la NC en AFIP via InvoiceService.EnqueueAnnulmentAsync.
    /// Si requiere override de invariantes, el service tira ApprovalRequiredException.
    /// </summary>
    [HttpPatch("{publicId:guid}/confirm")]
    [RequirePermission(Permissions.ReservasCancel)]
    [RequireOwnership(OwnedEntity.BookingCancellation, "publicId", bypassPermission: Permissions.ReservasViewAll)]
    public async Task<ActionResult<BookingCancellationDto>> Confirm(
        Guid publicId,
        ConfirmCancellationRequest request,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "System";
        var userName = User.FindFirst("FullName")?.Value ?? User.FindFirst(ClaimTypes.Name)?.Value;
        var requesterIsAdmin = User.IsInRole("Admin");

        // ADR-013: resolvemos SERVER-SIDE si el usuario puede clasificar la penalidad como
        // ingreso propio de la agencia (lo que dispara una ND fiscal real). El Admin lo
        // puede siempre (bypass de rol). El resto necesita el permiso dedicado. No confiamos
        // en el frontend para esta decision fiscalmente sensible. El service exige este flag
        // cuando la clasificacion del request es de ingreso propio.
        var userCanClassifyAgencyPenalty = requesterIsAdmin
            || (await _permissionResolver.GetPermissionsAsync(userId, cancellationToken))
                .Contains(Permissions.CancellationsClassifyAgencyPenalty);

        try
        {
            var dto = await _bcService.ConfirmAsync(
                publicId, request, userId, userName, requesterIsAdmin, cancellationToken,
                userCanClassifyAgencyPenalty: userCanClassifyAgencyPenalty);
            return Ok(dto);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ApprovalRequiredException ex)
        {
            // Mismo contrato 409 + body que InvoicesController.AnnulInvoice para
            // que el frontend (RequestApprovalModal) consuma el mismo shape.
            return Conflict(new
            {
                message = "Esta acción requiere autorización previa del Administrador o Colaborador.",
                requiresApproval = true,
                requestType = ex.RequestType.ToString(),
                entityType = ex.EntityType,
                entityId = ex.EntityId,
            });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex) when (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
        }
    }

    /// <summary>
    /// ADR-014 (§3.1, 2026-06-02): confirmacion DIFERIDA de la penalidad propia de la
    /// agencia, DIAS DESPUES de la cancelacion, cuando el operador confirma el monto. Dispara
    /// la emision de la Nota de Debito. Controller thin: resuelve el permiso server-side y
    /// traduce las excepciones al mismo shape que <c>Confirm</c> / <c>EditLiquidation</c>.
    ///
    /// <para><b>Permiso</b>: <see cref="Permissions.ReservasCancel"/> para llegar al endpoint
    /// + <see cref="Permissions.CancellationsClassifyAgencyPenalty"/> (o Admin) resuelto
    /// server-side para poder disparar la ND fiscal. La decision fiscalmente sensible NO se
    /// confia al frontend.</para>
    ///
    /// <para><b>Mapeo de errores</b>: 404 (BC no existe); 409 INV-ADR014-PERM (sin permiso: el
    /// service lo enforza lanzando <see cref="BusinessInvariantViolationException"/>, que el
    /// GlobalExceptionHandler mapea a 409 con invariantCode, igual que el path sincrono Confirm
    /// para falta de permiso explicita; NO es 403); 409 <c>requiresApproval</c> (4-eyes); 409
    /// <c>CONCURRENT_EDIT</c> (xmin); 409 invariantes (INV-ADR014-*); 400 (fecha invalida);
    /// 503 (DB caida).</para>
    /// </summary>
    [HttpPatch("{publicId:guid}/confirm-penalty")]
    [RequirePermission(Permissions.ReservasCancel)]
    [RequireOwnership(OwnedEntity.BookingCancellation, "publicId", bypassPermission: Permissions.ReservasViewAll)]
    public async Task<ActionResult<BookingCancellationDto>> ConfirmPenalty(
        Guid publicId,
        ConfirmPenaltyRequest request,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "System";
        var userName = User.FindFirst("FullName")?.Value ?? User.FindFirst(ClaimTypes.Name)?.Value;
        var requesterIsAdmin = User.IsInRole("Admin");

        // ADR-014: resolvemos SERVER-SIDE si el usuario puede confirmar la penalidad propia
        // (lo que dispara una ND fiscal real). Admin siempre; el resto necesita el permiso
        // dedicado. El service lo EXIGE (no degrada) en este flujo diferido.
        var userCanClassifyAgencyPenalty = requesterIsAdmin
            || (await _permissionResolver.GetPermissionsAsync(userId, cancellationToken))
                .Contains(Permissions.CancellationsClassifyAgencyPenalty);

        try
        {
            var dto = await _bcService.ConfirmPenaltyAsync(
                publicId, request, userId, userName, requesterIsAdmin, cancellationToken,
                userCanClassifyAgencyPenalty: userCanClassifyAgencyPenalty);
            return Ok(dto);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ApprovalRequiredException ex)
        {
            // Mismo contrato 409 + body que Confirm para que el frontend (RequestApprovalModal)
            // consuma el mismo shape.
            return Conflict(new
            {
                message = "Esta acción requiere autorización previa del Administrador o Colaborador.",
                requiresApproval = true,
                requestType = ex.RequestType.ToString(),
                entityType = ex.EntityType,
                entityId = ex.EntityId,
            });
        }
        catch (DbUpdateConcurrencyException)
        {
            // xmin: otro proceso toco el BC entre el read y el write. Gracias a que la marca
            // PenaltyStatus=Confirmed se persiste en su commit propio ANTES de crear la ND
            // (B1), el 409 es seguro de reintentar: el reintento rebota por INV-ADR014-003 o
            // procede limpio si el commit no llego a suceder.
            return Conflict(new
            {
                code = "CONCURRENT_EDIT",
                message = "Otra edicion fue procesada primero, reintente.",
            });
        }
        catch (InvalidOperationException ex)
        {
            // Flag OFF, etc. 409.
            return Conflict(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            // Fecha de confirmacion invalida (futura / anterior a la cancelacion).
            return BadRequest(new { message = ex.Message });
        }
        // BusinessInvariantViolationException (INV-ADR014-* + permiso) la atrapa
        // GlobalExceptionHandler (409 con invariantCode). No la catcheamos aca, mismo
        // criterio que Confirm/EditLiquidation.
        catch (Exception ex) when (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
        }
    }

    /// <summary>
    /// Aborta un BC en estado <c>Drafted</c> (el operador se arrepintio antes
    /// de tocar AFIP). Idempotente: si ya esta Aborted, retorna 200 con el DTO.
    /// </summary>
    [HttpPatch("{publicId:guid}/abort")]
    [RequirePermission(Permissions.ReservasCancel)]
    [RequireOwnership(OwnedEntity.BookingCancellation, "publicId", bypassPermission: Permissions.ReservasViewAll)]
    public async Task<ActionResult<BookingCancellationDto>> Abort(
        Guid publicId,
        AbortCancellationRequest request,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "System";

        try
        {
            var dto = await _bcService.AbortAsync(publicId, request.Reason, userId, cancellationToken);
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
        catch (Exception ex) when (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
        }
    }

    /// <summary>
    /// FC1.2.1 BR-V2-01: escape hatch admin. Cuando AFIP devolvio CAE pero el
    /// callback automatico fallo, un Admin puede empatar el estado del BC con
    /// la realidad fiscal. Solo Admin (permiso dedicado, no va a Vendedor ni
    /// Colaborador). Requiere ApprovalRequest aprobado tipo InvariantOverride
    /// scoped al BC.
    /// </summary>
    [HttpPost("{publicId:guid}/force-arca-confirmation")]
    [RequirePermission(Permissions.CancellationsForceArcaConfirmation)]
    [RequireOwnership(OwnedEntity.BookingCancellation, "publicId", bypassPermission: Permissions.ReservasViewAll)]
    public async Task<ActionResult<BookingCancellationDto>> ForceArcaConfirmation(
        Guid publicId,
        ForceArcaConfirmationRequest request,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "System";
        var userName = User.FindFirst("FullName")?.Value ?? User.FindFirst(ClaimTypes.Name)?.Value;

        try
        {
            var dto = await _bcService.ForceArcaConfirmationAsync(publicId, request, userId, userName, cancellationToken);
            return Ok(dto);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ApprovalRequiredException ex)
        {
            return Conflict(new
            {
                message = "Esta acción requiere autorización previa.",
                requiresApproval = true,
                requestType = ex.RequestType.ToString(),
                entityType = ex.EntityType,
                entityId = ex.EntityId,
            });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex) when (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
        }
    }

    /// <summary>
    /// Lectura de un BC por PublicId. Lo usa la UI de cancelaciones y los
    /// tests de E2E (refetch despues de cada accion).
    /// </summary>
    [HttpGet("{publicId:guid}")]
    [RequirePermission(Permissions.ReservasView)]
    [RequireOwnership(OwnedEntity.BookingCancellation, "publicId", bypassPermission: Permissions.ReservasViewAll)]
    public async Task<ActionResult<BookingCancellationDto>> GetByPublicId(
        Guid publicId,
        CancellationToken cancellationToken)
    {
        var dto = await _bcService.GetByPublicIdAsync(publicId, cancellationToken);
        if (dto is null) return NotFound();
        return Ok(dto);
    }

    /// <summary>
    /// ADR-013 §3.10 (M4, 2026-06-01): bandeja "cancelaciones con NC emitida pero sin su
    /// ND". Devuelve las cancelaciones cuya NC total ya salio con CAE pero cuya Nota de
    /// Debito quedo pendiente o fallida -> fiscalmente incompletas. El service reconcilia de
    /// paso los Pending leyendo el resultado de la ND emitida async.
    ///
    /// <para><b>Permiso</b>: <see cref="Permissions.CobranzasInvoiceAnnul"/> — es una vista
    /// de back-office fiscal (cross-reserva), mismo dominio que el annul/edicion fiscal. No
    /// lleva ownership por fila porque es una lista agregada para el back-office, no una
    /// vista del vendedor sobre "sus" reservas.</para>
    /// </summary>
    [HttpGet("debit-notes/pending")]
    [RequirePermission(Permissions.CobranzasInvoiceAnnul)]
    public async Task<ActionResult<IReadOnlyList<CancellationDebitNotePendingDto>>> GetPendingDebitNotes(
        CancellationToken cancellationToken)
    {
        var rows = await _bcService.GetCancellationsWithMissingDebitNoteAsync(cancellationToken);
        return Ok(rows);
    }

    /// <summary>
    /// ADR-009/ADR-025 (read-model, 2026-06-13): bandeja "Notas de credito por revisar" (dentro de
    /// Cobranza). Devuelve las cancelaciones cuya NC parcial esta esperando revision/emision manual
    /// (estado <c>ManualReviewPending</c>). Es una vista de LECTURA pura: el approve/reject de cada una
    /// se hace por el flujo de approvals + <c>edit-liquidation</c>.
    ///
    /// <para><b>Permiso</b>: <see cref="Permissions.CobranzasViewAll"/> (cobranzas.view_all) — es el permiso
    /// de back-office que habilita ver TODAS las cobranzas, no solo las propias. Esta bandeja es una lista
    /// agregada cross-reserva SIN filtro de ownership por fila y EXPONE el nombre del cliente, asi que debe
    /// quedar restringida a back-office. NO usamos <c>CobranzasInvoiceAnnul</c> porque el rol Vendedor lo
    /// tiene (para anular SUS propias facturas, con ownership), y eso le filtraria nombres de clientes de
    /// reservas ajenas (fuga horizontal — SEC-1). El Vendedor NO tiene <c>cobranzas.view_all</c>.</para>
    /// </summary>
    [HttpGet("pending-credit-note-review")]
    [RequirePermission(Permissions.CobranzasViewAll)]
    public async Task<ActionResult<IReadOnlyList<PendingCreditNoteReviewDto>>> GetPendingCreditNoteReview(
        CancellationToken cancellationToken)
    {
        var rows = await _bcService.GetCancellationsPendingCreditNoteReviewAsync(cancellationToken);
        return Ok(rows);
    }

    /// <summary>
    /// FC1.3.5 (ADR-009 §2.7 G3 + §2.11, 2026-05-21): G3 self-loop. El admin
    /// (o el Vendedor que tenga <c>CobranzasInvoiceAnnul</c> + ownership)
    /// edita los inputs de la liquidacion fiscal de un BC que esta esperando
    /// revision manual. El service re-corre el calculator con los overrides,
    /// persiste el diff en <c>ApprovalRequest.Metadata.edits[]</c> y deja el
    /// BC en <c>ManualReviewPending</c> (self-loop). El approve/reject final
    /// lo hace el endpoint generico de approvals, no este metodo.
    ///
    /// <para><b>Permiso</b>: <see cref="Permissions.CobranzasInvoiceAnnul"/>
    /// — reusamos el mismo permiso que usa el annul de facturas, porque la
    /// edicion de la liquidacion fiscal queda en el mismo dominio funcional
    /// (decision plan tactico FC1.3 §1574). La regla 4-eyes (INV-FC1.3-004)
    /// la enforza el service, no el controller — no podemos saber aca si el
    /// que edita es el mismo que solicito sin cargar el BC.</para>
    ///
    /// <para><b>Ownership</b>: el responsable del BC es el responsable de la
    /// reserva padre. Reusamos <see cref="OwnedEntity.BookingCancellation"/>
    /// con bypass por <c>ReservasViewAll</c> (admin/back-office).</para>
    ///
    /// <para><b>Mapeo de errores</b>:
    /// <list type="bullet">
    ///   <item><c>KeyNotFoundException</c> → 404.</item>
    ///   <item><c>BusinessInvariantViolationException</c> → 409 via
    ///     <see cref="Middleware.GlobalExceptionHandler"/>. No la catcheamos
    ///     aca (mismo patron que <c>Draft</c> y <c>Confirm</c>).</item>
    ///   <item><c>InvalidOperationException</c> → 409 (ej. re-clasificacion
    ///     dio <c>TotalPlusNewInvoice</c> que Fase 1 no soporta).</item>
    ///   <item><c>DbUpdateConcurrencyException</c> → 409 con
    ///     <c>code=CONCURRENT_EDIT</c> (otro admin edito el mismo
    ///     <c>ApprovalRequest</c> en simultaneo y EF detecto el conflicto
    ///     por <c>xmin</c> — el frontend pide reintentar).</item>
    /// </list>
    /// </para>
    /// </summary>
    [HttpPost("{publicId:guid}/edit-liquidation")]
    [RequirePermission(Permissions.CobranzasInvoiceAnnul)]
    [RequireOwnership(OwnedEntity.BookingCancellation, "publicId", bypassPermission: Permissions.ReservasViewAll)]
    public async Task<ActionResult<BookingCancellationDto>> EditLiquidation(
        Guid publicId,
        EditLiquidationRequest request,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "System";
        var userName = User.FindFirst("FullName")?.Value ?? User.FindFirst(ClaimTypes.Name)?.Value;

        try
        {
            var dto = await _bcService.EditLiquidationAsync(publicId, request, userId, userName, cancellationToken);
            return Ok(dto);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            // Re-clasificacion a TotalPlusNewInvoice (no soportado Fase 1),
            // feature flag off, supplier no encontrado, etc. 409 expresa
            // "estado actual incompatible con la operacion".
            return Conflict(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            // ArgumentNullException(req) o validacion de argumentos del service.
            return BadRequest(new { message = ex.Message });
        }
        catch (DbUpdateConcurrencyException)
        {
            // RH-006: dos admins editaron el mismo ApprovalRequest en simultaneo.
            // EF detecta el conflicto por xmin del approval. El frontend muestra
            // "reintentar" — el segundo admin lee el estado fresco y vuelve a
            // mandar su edit.
            return Conflict(new
            {
                code = "CONCURRENT_EDIT",
                message = "Otra edicion fue procesada primero, reintente.",
            });
        }
        // BusinessInvariantViolationException la atrapa GlobalExceptionHandler
        // (409 ProblemDetails con invariantCode + constraintName + code).
        // Mismo criterio que Draft/Confirm — no la catcheamos aca.
        catch (Exception ex) when (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
        }
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    /// <summary>
    /// Validacion manual de ownership para endpoints sin id en la ruta (POST).
    /// Replica la logica del <see cref="RequireOwnershipAttribute"/>: Admin
    /// bypass + bypass por permiso global + IOwnershipResolver para los demas.
    /// </summary>
    private async Task<bool> UserIsAllowedOverReservaAsync(string reservaPublicIdOrLegacyId, CancellationToken ct)
    {
        if (User.IsInRole("Admin"))
        {
            return true;
        }

        // ReservasViewAll permite operar sobre cualquier reserva (admin de
        // back-office). Lo verificamos via las claims emitidas por
        // PermissionAuthorizationHandler (en runtime se carga en el handler
        // via IUserPermissionResolver, pero el RequirePermission attribute ya
        // dejo al usuario "autorizado" para llegar hasta aca — la mejor forma
        // de saber si tiene ViewAll extra es consultar el resolver).
        var resolver = HttpContext.RequestServices.GetService<IUserPermissionResolver>();
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId)) return false;

        if (resolver is not null)
        {
            var perms = await resolver.GetPermissionsAsync(userId, ct);
            if (perms.Contains(Permissions.ReservasViewAll))
            {
                return true;
            }
        }

        return await _ownershipResolver.IsOwnerAsync(userId, OwnedEntity.Reserva, reservaPublicIdOrLegacyId, ct);
    }
}

/// <summary>
/// FC1.2.4: payload del abort. Reason es libre del operador para auditoria
/// (queda en BC.Reason o en audit log segun donde el service lo guarde).
/// </summary>
public record AbortCancellationRequest(
    [System.ComponentModel.DataAnnotations.Required]
    [System.ComponentModel.DataAnnotations.MinLength(5)]
    [System.ComponentModel.DataAnnotations.MaxLength(500)]
    string Reason
);
