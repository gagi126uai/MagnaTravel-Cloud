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
using TravelApi.Domain.Helpers;
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
    // ADR-044 Fix B (2026-07-13): sugerencia de TC del dolar oficial BNA para pre-escribir el modal de correccion.
    private readonly IBnaExchangeRateService _bnaExchangeRateService;
    private readonly ILogger<CancellationsController> _logger;

    public CancellationsController(
        IBookingCancellationService bcService,
        IOwnershipResolver ownershipResolver,
        IUserPermissionResolver permissionResolver,
        IBnaExchangeRateService bnaExchangeRateService,
        ILogger<CancellationsController> logger)
    {
        _bcService = bcService;
        _ownershipResolver = ownershipResolver;
        _permissionResolver = permissionResolver;
        _bnaExchangeRateService = bnaExchangeRateService;
        _logger = logger;
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
            // Feature flag off, reserva sin invoice activa, etc. 409 = "estado actual incompatible". El texto
            // de negocio limpio se muestra; el ruido tecnico .NET/EF se sanea (FUGA 3).
            return SanitizedConflict(ex, request.ReservaPublicId);
        }
        catch (ArgumentException ex)
        {
            return SanitizedBadRequest(ex);
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
            return SanitizedBadRequest(ex);
        }
        catch (InvalidOperationException ex)
        {
            return SanitizedConflict(ex, request.ReservaPublicId);
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
            // Business limpio se muestra; ruido tecnico .NET/EF se sanea (FUGA 3).
            return SanitizedConflict(ex, publicId);
        }
        catch (ArgumentException ex)
        {
            return SanitizedBadRequest(ex);
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
            // Flag OFF / estado incompatible. Business limpio se muestra; ruido tecnico .NET/EF se sanea (FUGA 3).
            return SanitizedConflict(ex, publicId);
        }
        catch (ArgumentException ex)
        {
            // Fecha de confirmacion invalida (futura / anterior a la cancelacion).
            return SanitizedBadRequest(ex);
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
    /// RECUPERACION (fix 2026-07-01): reintenta EMITIR la Nota de Debito de una cancelacion cuya multa ya quedo
    /// confirmada pero cuya ND no se llego a emitir (quedo a medias por un fallo). Destraba la reserva SIN
    /// re-confirmar la multa. Idempotente y anti doble-emision (re-vincula una ND ya creada si existe).
    ///
    /// <para><b>Permiso</b>: MISMO gate fiscal que <see cref="ConfirmPenalty"/>
    /// (<see cref="Permissions.ReservasCancel"/> para llegar + <see cref="Permissions.CancellationsClassifyAgencyPenalty"/>
    /// o Admin resuelto server-side): emite el MISMO comprobante fiscal. La decision NO se confia al frontend.</para>
    ///
    /// <para><b>Mapeo de errores</b>: 404 (BC no existe); 409 (flag OFF / INV-ADR014-RETRY-* / CONCURRENT_EDIT);
    /// 503 (DB caida). Las <c>BusinessInvariantViolationException</c> las mapea el GlobalExceptionHandler a 409.</para>
    /// </summary>
    [HttpPost("{publicId:guid}/retry-debit-note")]
    [RequirePermission(Permissions.ReservasCancel)]
    [RequireOwnership(OwnedEntity.BookingCancellation, "publicId", bypassPermission: Permissions.ReservasViewAll)]
    public async Task<ActionResult<BookingCancellationDto>> RetryDebitNote(
        Guid publicId,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "System";
        var userName = User.FindFirst("FullName")?.Value ?? User.FindFirst(ClaimTypes.Name)?.Value;
        var requesterIsAdmin = User.IsInRole("Admin");

        // Mismo gate que confirmar la multa: Admin siempre; el resto necesita el permiso dedicado. El service lo EXIGE.
        var userCanClassifyAgencyPenalty = requesterIsAdmin
            || (await _permissionResolver.GetPermissionsAsync(userId, cancellationToken))
                .Contains(Permissions.CancellationsClassifyAgencyPenalty);

        try
        {
            var dto = await _bcService.RetryDebitNoteEmissionAsync(
                publicId, userId, userName, cancellationToken,
                userCanClassifyAgencyPenalty: userCanClassifyAgencyPenalty);
            return Ok(dto);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new
            {
                code = "CONCURRENT_EDIT",
                message = "Otra edicion fue procesada primero, reintente.",
            });
        }
        catch (InvalidOperationException ex)
        {
            // Flag OFF / estado incompatible. Business limpio se muestra; ruido tecnico .NET/EF se sanea (FUGA 3).
            return SanitizedConflict(ex, publicId);
        }
        // BusinessInvariantViolationException (INV-ADR014-RETRY-* + permiso) la atrapa el GlobalExceptionHandler
        // (409 con invariantCode), mismo criterio que ConfirmPenalty.
        catch (Exception ex) when (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
        }
    }

    /// <summary>
    /// Spec "el paso de multa vive en la ficha" (A4, 2026-07-08): corrige el MONTO + MONEDA de una multa YA
    /// CONFIRMADA cuya Nota de Debito quedo TRABADA (revision manual por moneda distinta, o fallida) y SIN
    /// comprobante fiscal emitido con CAE. Es la version ATOMICA del circuito cerrar-sin-multa -> reabrir -> volver a
    /// confirmar (deshacer imputacion vieja + grabar monto/moneda + re-encolar la ND, todo bajo el lock del padre).
    ///
    /// <para><b>Permiso</b> (B2 security 2026-07-08): MISMO gate que confirm-penalty / retry / waive
    /// (<see cref="Permissions.ReservasCancel"/> para llegar + <see cref="Permissions.CancellationsClassifyAgencyPenalty"/>
    /// o Admin, resuelto server-side + ownership). Correct es un SUPERSET de retry (re-emite la ND Y cambia
    /// monto/moneda), asi que no puede estar gateado mas debil en el eje de la decision de plata. NO se usa
    /// <c>cobranzas.invoice_annul</c>: ese permiso anula comprobantes con CAE, no clasifica la multa del operador.</para>
    ///
    /// <para><b>Mapeo de errores</b>: 400 (monto &lt;= 0 / moneda no ARS-USD / motivo vacio); 404 (BC no existe);
    /// 409 INV-CORRECT-PERM (sin permiso); 409 INV-CORRECT-001 (multa no confirmada); 409 INV-CORRECT-002 (ND ya
    /// emitida con CAE); 409 INV-CORRECT-003 (ND en vuelo); 409 CONCURRENT_EDIT (xmin); 409 (flag OFF); 503 (DB caida).</para>
    /// </summary>
    [HttpPatch("{publicId:guid}/correct-penalty")]
    [RequirePermission(Permissions.ReservasCancel)]
    [RequireOwnership(OwnedEntity.BookingCancellation, "publicId", bypassPermission: Permissions.ReservasViewAll)]
    public async Task<ActionResult<BookingCancellationDto>> CorrectPenalty(
        Guid publicId,
        CorrectPenaltyRequest request,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "System";
        var userName = User.FindFirst("FullName")?.Value ?? User.FindFirst(ClaimTypes.Name)?.Value;
        var requesterIsAdmin = User.IsInRole("Admin");

        // Mismo gate fiscal que confirmar/reintentar la multa: Admin siempre; el resto necesita el permiso dedicado.
        // Se resuelve server-side (no se confia en el frontend) y el service lo EXIGE (defensa en profundidad).
        var userCanClassifyAgencyPenalty = requesterIsAdmin
            || (await _permissionResolver.GetPermissionsAsync(userId, cancellationToken))
                .Contains(Permissions.CancellationsClassifyAgencyPenalty);

        try
        {
            var dto = await _bcService.CorrectPenaltyAsync(
                publicId, request.Amount, request.Currency, request.Reason, userId, userName, cancellationToken,
                userCanClassifyAgencyPenalty: userCanClassifyAgencyPenalty,
                // ADR-044 Fix B (2026-07-13): datos del TC para convertir una multa cross-currency (Caso A).
                exchangeRate: request.ExchangeRate,
                exchangeRateSource: request.ExchangeRateSource,
                exchangeRateDate: request.ExchangeRateDate,
                exchangeRateJustification: request.ExchangeRateJustification);
            return Ok(dto);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new
            {
                code = "CONCURRENT_EDIT",
                message = "Otra edicion fue procesada primero, reintente.",
            });
        }
        catch (InvalidOperationException ex)
        {
            // Flag OFF / estado incompatible. Business limpio se muestra; ruido tecnico .NET/EF se sanea (FUGA 3).
            return SanitizedConflict(ex, publicId);
        }
        catch (ArgumentException ex)
        {
            // Monto <= 0, moneda no ISO ARS/USD, motivo vacio. 400.
            return SanitizedBadRequest(ex);
        }
        // BusinessInvariantViolationException (INV-CORRECT-*) la atrapa GlobalExceptionHandler (409 con invariantCode).
        catch (Exception ex) when (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
        }
    }

    /// <summary>
    /// ADR-044 "Deshacer una multa ya emitida" (2026-07-14): la Nota de Debito de la multa salio con CAE y
    /// estaba MAL (monto/moneda equivocada, o no correspondia). Emite una Nota de Credito ESPEJO de esa ND que
    /// la anula fiscalmente; al conseguir CAE, la ND queda desvinculada y el paso vuelve a estar abierto
    /// (corregir y re-emitir, o cerrar sin multa). Molde de <see cref="CorrectPenalty"/>.
    ///
    /// <para><b>Permiso</b>: MISMO gate que confirm-penalty/correct-penalty/retry/waive
    /// (<see cref="Permissions.ReservasCancel"/> para llegar + <see cref="Permissions.CancellationsClassifyAgencyPenalty"/>
    /// o Admin, resuelto server-side + ownership). Deshacer un comprobante fiscal ya emitido es, como minimo, tan
    /// sensible como corregirlo.</para>
    ///
    /// <para><b>Mapeo de errores</b>: 400 (motivo vacio); 404 (BC no existe); 409 INV-UNDO-PERM (sin permiso);
    /// 409 INV-UNDO-001 (no hay ND con CAE para deshacer); 409 INV-UNDO-002 (ya hay una anulacion en curso o
    /// consumada); 409 INV-UNDO-MANUAL (factura original ya anulada del todo -> revision manual); 409
    /// INV-UNDO-MULTIOP (ambiguedad entre operadores -> revision manual); 409 CONCURRENT_EDIT (xmin); 409 (flag
    /// OFF); 503 (DB caida).</para>
    /// </summary>
    [HttpPost("{publicId:guid}/undo-debit-note")]
    [RequirePermission(Permissions.ReservasCancel)]
    [RequireOwnership(OwnedEntity.BookingCancellation, "publicId", bypassPermission: Permissions.ReservasViewAll)]
    public async Task<ActionResult<BookingCancellationDto>> UndoDebitNote(
        Guid publicId,
        UndoDebitNoteRequest request,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "System";
        var userName = User.FindFirst("FullName")?.Value ?? User.FindFirst(ClaimTypes.Name)?.Value;
        // SOLO ADMINISTRADORES (spec UX firmada, gate B1 2026-07-14): deshacer un comprobante fiscal ya emitido
        // con CAE es la acción más sensible del paso de multa; a diferencia de confirmar/corregir/reintentar
        // (permiso classify), esto se restringe al rol Admin. El service lo EXIGE de nuevo (defensa en profundidad).
        var requesterIsAdmin = User.IsInRole("Admin");

        try
        {
            var dto = await _bcService.UndoIssuedDebitNoteAsync(
                publicId, request.Reason, userId, userName, cancellationToken,
                requesterIsAdmin: requesterIsAdmin);
            return Ok(dto);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new
            {
                code = "CONCURRENT_EDIT",
                message = "Otra edicion fue procesada primero, reintente.",
            });
        }
        catch (InvalidOperationException ex)
        {
            return SanitizedConflict(ex, publicId);
        }
        catch (ArgumentException ex)
        {
            return SanitizedBadRequest(ex);
        }
        // BusinessInvariantViolationException (INV-UNDO-*) la atrapa GlobalExceptionHandler (409 con invariantCode).
        catch (Exception ex) when (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
        }
    }

    /// <summary>
    /// ADR-042 §3.6 (2026-07-01): reintenta SOLO las notas de credito faltantes de una anulacion multi-factura
    /// que quedo a medias (una NC salio y otra no). Idempotente: no re-emite la NC que ya salio; serializado
    /// bajo el mismo lock del padre que los callbacks de ARCA. Destraba la reserva desde la UI ("Reintentar la
    /// que falta" / "Reintentar anulacion").
    ///
    /// <para><b>Permiso</b>: MISMO que anular la reserva (<see cref="Permissions.ReservasCancel"/> + ownership),
    /// enforzado por los atributos server-side. No se restringe a Admin: reintentar no es deshacer nada, es
    /// COMPLETAR lo que ya empezo (P7 de la spec UX).</para>
    ///
    /// <para><b>Mapeo de errores</b>: 404 (BC no existe); 409 (INV-042-RETRY-001 estado no reintentable /
    /// CONCURRENT_EDIT); 503 (DB caida). Las <c>BusinessInvariantViolationException</c> las mapea el
    /// GlobalExceptionHandler a 409.</para>
    /// </summary>
    [HttpPost("{publicId:guid}/retry-credit-notes")]
    [RequirePermission(Permissions.ReservasCancel)]
    [RequireOwnership(OwnedEntity.BookingCancellation, "publicId", bypassPermission: Permissions.ReservasViewAll)]
    public async Task<ActionResult<BookingCancellationDto>> RetryCreditNotes(
        Guid publicId,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "System";
        var userName = User.FindFirst("FullName")?.Value ?? User.FindFirst(ClaimTypes.Name)?.Value;

        try
        {
            var dto = await _bcService.RetryCreditNotesAsync(publicId, userId, userName, cancellationToken);
            return Ok(dto);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new
            {
                code = "CONCURRENT_EDIT",
                message = "Otra edicion fue procesada primero, reintente.",
            });
        }
        catch (InvalidOperationException ex)
        {
            // Data-exposure (2026-07-02): NUNCA devolver ex.Message crudo en el body. Un InvalidOperationException
            // aca puede venir de .NET/EF por una carrera (ej. "Sequence contains no elements." si el BC
            // desaparece, o "The instance of entity type 'BookingCancellation' cannot be tracked ... {Id: 42}"
            // — nombre de entidad + Id interno). El crudo va SOLO al log; al usuario un generico en criollo.
            _logger.LogWarning(ex,
                "retry-credit-notes: InvalidOperationException para BC {PublicId} (usuario {UserId}).",
                publicId, userId);
            return Conflict(new { message = "No se pudo completar la operación. Volvé a intentar." });
        }
        // BusinessInvariantViolationException (INV-042-RETRY-001) la atrapa el GlobalExceptionHandler (409).
        catch (Exception ex) when (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
        }
    }

    /// <summary>
    /// ADR-044 T5-emision (2026-07-15, diseño §6.1/§7): confirma y EMITE la Nota de Credito real de una
    /// cancelacion PARCIAL (se canceló UN servicio de una reserva facturada; la factura sigue viva por el
    /// resto). Sella el snapshot fiscal (heredado de la factura destino, nunca recotizado) y dispara la NC via
    /// el pipeline de bajo nivel — la ficha hace polling del resultado (mismo patron "procesando" del modulo).
    ///
    /// <para><b>Permiso</b>: <see cref="Permissions.CobranzasInvoiceAnnul"/> — el MISMO que la anulacion TOTAL
    /// y la bandeja "Comprobantes por resolver" (diseño §11: emision fiscal real contra ARCA es una accion
    /// sensible, no <c>ReservasCancel</c>). + ownership de la reserva.</para>
    ///
    /// <para><b>Body opcional</b> (<see cref="EmitPartialCreditNoteRequest"/>): a diferencia de <c>Confirm</c>
    /// (anulacion total), esta pantalla es de SOLO LECTURA sobre el monto/moneda/TC (criterio matriculado +
    /// regla dura multimoneda): no hay ningun dato de plata que el usuario tipee. La moneda/TC se heredan
    /// server-side de la factura destino; las condiciones fiscales se leen del dato VIVO al momento de emitir
    /// (spec UX P3=A: "sin motivo nuevo, registro automático"). Lo UNICO que el body puede traer es
    /// <c>TargetInvoicePublicId</c> — spec UX 2026-07-17: cuando la cancelación tiene VARIOS servicios
    /// resueltos contra facturas DISTINTAS (el caso real de Gastón: hotel USD + excursión ARS), cada
    /// devolución se emite por separado y el botón "Emitir la devolución" de esa fila indica cuál factura.
    /// Sin body (o sin ese campo), se mantiene el comportamiento de siempre.</para>
    ///
    /// <para><b>Mapeo de errores</b>: 404 (BC no existe); 409 <c>INV-T5-EMIT-STATE</c> (ya no está Drafted/
    /// puramente parcial); 409 <c>INV-T5-EMIT-UNRESOLVED</c> (falta resolver factura o monto, o la factura
    /// indicada no tiene nada pendiente); 409 <c>INV-T5-EMIT-CAP</c> (el saldo de la factura cambió); 409
    /// <c>INV-T5-EMIT-RI-SIGNOFF</c> (agencia RI, pendiente firma del contador); 409
    /// <c>INV-T5-EMIT-MULTI-INVOICE</c> (2+ facturas pendientes y no se indicó cuál); 409
    /// <c>CONCURRENT_EDIT</c> (xmin); 503 (DB caída). Todas sin jerga/IDs/enums en el body (gate data-exposure).</para>
    /// </summary>
    [HttpPost("{publicId:guid}/emit-partial-credit-note")]
    [RequirePermission(Permissions.CobranzasInvoiceAnnul)]
    [RequireOwnership(OwnedEntity.BookingCancellation, "publicId", bypassPermission: Permissions.ReservasViewAll)]
    public async Task<ActionResult<BookingCancellationDto>> EmitPartialCreditNote(
        Guid publicId,
        CancellationToken cancellationToken,
        [FromBody] EmitPartialCreditNoteRequest? request = null)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "System";
        var userName = User.FindFirst("FullName")?.Value ?? User.FindFirst(ClaimTypes.Name)?.Value;

        try
        {
            var dto = await _bcService.ConfirmPartialCancellationEmissionAsync(publicId, userId, userName, cancellationToken, request);
            return Ok(dto);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new
            {
                code = "CONCURRENT_EDIT",
                message = "Otra edicion fue procesada primero, reintente.",
            });
        }
        catch (InvalidOperationException ex)
        {
            // Business limpio se muestra; ruido tecnico .NET/EF por carreras se sanea (FUGA 3).
            return SanitizedConflict(ex, publicId);
        }
        // BusinessInvariantViolationException (INV-T5-EMIT-*) la atrapa el GlobalExceptionHandler (409 con
        // invariantCode), mismo criterio que Draft/Confirm/EditLiquidation — no la catcheamos aca.
        catch (Exception ex) when (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
        }
    }

    /// <summary>
    /// T5 legacy: elige la factura y confirma el monto de un servicio cancelado (una devolución vieja
    /// pendiente). No emite la NC; únicamente deja esa línea lista para la confirmación de emisión.
    ///
    /// <para><b>Spec UX 2026-07-17 (varios pendientes)</b>: con VARIOS servicios pendientes al mismo tiempo
    /// (el caso real de Gastón: hotel USD + excursión ARS), <c>request.BookingCancellationLinePublicId</c>
    /// indica cuál se está resolviendo — el resto queda intacto. Con uno solo pendiente, se puede omitir
    /// (compatibilidad con el formulario viejo).</para>
    /// </summary>
    [HttpPatch("{publicId:guid}/resolve-partial-credit-note")]
    [RequirePermission(Permissions.CobranzasInvoiceAnnul)]
    [RequireOwnership(OwnedEntity.BookingCancellation, "publicId", bypassPermission: Permissions.ReservasViewAll)]
    public async Task<ActionResult<BookingCancellationDto>> ResolvePartialCreditNote(
        Guid publicId,
        ResolvePartialCreditNoteRequest request,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "System";
        var userName = User.FindFirst("FullName")?.Value ?? User.FindFirst(ClaimTypes.Name)?.Value;
        try
        {
            return Ok(await _bcService.ResolvePartialCreditNoteAsync(publicId, request, userId, userName, cancellationToken));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new { code = "CONCURRENT_EDIT", message = "Otra edición fue procesada primero, reintente." });
        }
        catch (InvalidOperationException ex)
        {
            return SanitizedConflict(ex, publicId);
        }
        catch (Exception ex) when (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
        }
    }

    /// <summary>
    /// Fase A (2026-06-28): cierre SIN multa de la pata del operador ("el operador no cobro multa / devuelve
    /// todo"). Es la rama ALTERNATIVA a <see cref="ConfirmPenalty"/>: el front ofrece las dos acciones cuando hay
    /// una multa pendiente. Limpia el boton pendiente dejando la penalidad en estado terminal "sin multa" y
    /// registrando el rastro obligatorio, SIN emitir ninguna Nota de Debito. Controller thin: resuelve el permiso
    /// server-side y traduce las excepciones al mismo shape que <c>ConfirmPenalty</c>.
    ///
    /// <para><b>Permiso</b>: <see cref="Permissions.ReservasCancel"/> para llegar al endpoint +
    /// <see cref="Permissions.CancellationsClassifyAgencyPenalty"/> (o Admin) resuelto server-side (mismo gate que
    /// confirmar la multa). NO se confia la decision al frontend.</para>
    ///
    /// <para><b>Mapeo de errores</b>: 404 (BC no existe); 409 INV-WAIVE-PERM (sin permiso); 409 INV-WAIVE-001
    /// (estado no post-NC); 409 INV-WAIVE-003 (ya cerrada sin multa — idempotencia); 409 INV-WAIVE-004 (la multa
    /// tiene una Nota de Débito en juego — se resuelve desde administración); 409 INV-WAIVE-005 (cerrar sin multa
    /// una penalidad ya confirmada sin ser Admin); 409 CONCURRENT_EDIT (xmin); 400 (motivo vacio); 503 (DB caida).</para>
    /// </summary>
    [HttpPatch("{publicId:guid}/waive-penalty")]
    [RequirePermission(Permissions.ReservasCancel)]
    [RequireOwnership(OwnedEntity.BookingCancellation, "publicId", bypassPermission: Permissions.ReservasViewAll)]
    public async Task<ActionResult<BookingCancellationDto>> WaivePenalty(
        Guid publicId,
        WaivePenaltyRequest request,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "System";
        var userName = User.FindFirst("FullName")?.Value ?? User.FindFirst(ClaimTypes.Name)?.Value;
        var requesterIsAdmin = User.IsInRole("Admin");

        // Mismo gate que confirmar la multa: Admin siempre; el resto necesita el permiso dedicado. El service
        // lo EXIGE (no degrada).
        var userCanClassifyAgencyPenalty = requesterIsAdmin
            || (await _permissionResolver.GetPermissionsAsync(userId, cancellationToken))
                .Contains(Permissions.CancellationsClassifyAgencyPenalty);

        try
        {
            var dto = await _bcService.WaiveOperatorPenaltyAsync(
                publicId, request.Reason, userId, userName, cancellationToken,
                userCanClassifyAgencyPenalty: userCanClassifyAgencyPenalty,
                requesterIsAdmin: requesterIsAdmin,
                supplierPublicId: request.SupplierPublicId);
            return Ok(dto);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new
            {
                code = "CONCURRENT_EDIT",
                message = "Otra edicion fue procesada primero, reintente.",
            });
        }
        catch (InvalidOperationException ex)
        {
            // Flag OFF / estado incompatible. Business limpio se muestra; ruido tecnico .NET/EF se sanea (FUGA 3).
            return SanitizedConflict(ex, publicId);
        }
        catch (ArgumentException ex)
        {
            // Motivo vacio. 400.
            return SanitizedBadRequest(ex);
        }
        // BusinessInvariantViolationException (INV-WAIVE-*) la atrapa GlobalExceptionHandler (409 con invariantCode).
        catch (Exception ex) when (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
        }
    }

    /// <summary>
    /// Fase A (2026-06-28): REABRE un cierre sin multa del operador. El cierre sin multa
    /// (<see cref="WaivePenalty"/>) es terminal, pero un error de carga o una multa TARDIA del operador hacen falta
    /// poder corregirlo. Vuelve la penalidad de "cerrada sin multa" a "pendiente", de modo que tanto "Confirmar
    /// multa" como "cerrar sin multa" quedan disponibles otra vez. Controller thin: resuelve el rol server-side y
    /// traduce las excepciones al mismo shape que las demas acciones del modulo.
    ///
    /// <para><b>Solo Admin</b>: el owner lo decidio Admin-only (no el permiso <c>classify_agency_penalty</c>). Se
    /// resuelve por rol server-side; a quien no es Admin se le responde 403 (<c>Forbid</c>) ANTES de tocar nada. El
    /// service lo EXIGE igual (defensa en profundidad).</para>
    ///
    /// <para><b>Mapeo de errores</b>: 403 (no Admin); 404 (BC no existe); 409 INV-WAIVE-REVERT-001 (no esta cerrada
    /// sin multa — idempotencia); 409 INV-WAIVE-REVERT-002 (tiene ND asociada); 409 CONCURRENT_EDIT (xmin); 400
    /// (motivo vacio); 503 (DB caida). El flag OFF cae en 409 (InvalidOperationException).</para>
    /// </summary>
    [HttpPatch("{publicId:guid}/revert-waive")]
    [RequirePermission(Permissions.ReservasCancel)]
    [RequireOwnership(OwnedEntity.BookingCancellation, "publicId", bypassPermission: Permissions.ReservasViewAll)]
    public async Task<ActionResult<BookingCancellationDto>> RevertWaivePenalty(
        Guid publicId,
        RevertWaivePenaltyRequest request,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "System";
        var userName = User.FindFirst("FullName")?.Value ?? User.FindFirst(ClaimTypes.Name)?.Value;

        // Solo Admin: rechazamos ANTES de llamar al service. Reabrir un cierre sin multa es una accion sensible y
        // poco habitual; el owner la limito a Admin por rol (no por permiso). El service ademas lo re-valida.
        if (!User.IsInRole("Admin"))
            return Forbid();

        try
        {
            var dto = await _bcService.RevertWaivedOperatorPenaltyAsync(
                publicId, request.Reason, userId, userName, requesterIsAdmin: true, cancellationToken,
                supplierPublicId: request.SupplierPublicId);
            return Ok(dto);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ClientCreditAlreadyUsedException ex)
        {
            // A6 (2026-07-08): freno de consistencia. El saldo a favor del cliente originado por esta anulacion ya se
            // uso/retiro por completo -> no se puede reabrir para cobrar una multa. Body de negocio con code estable
            // para que el front muestre el cartel exacto (registrar la multa con quien maneje la cuenta del cliente).
            return Conflict(new
            {
                code = ClientCreditAlreadyUsedException.ErrorCode,
                message = ex.Message,
            });
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new
            {
                code = "CONCURRENT_EDIT",
                message = "Otra edicion fue procesada primero, reintente.",
            });
        }
        catch (InvalidOperationException ex)
        {
            // Flag OFF / estado incompatible. Business limpio se muestra; ruido tecnico .NET/EF se sanea (FUGA 3).
            return SanitizedConflict(ex, publicId);
        }
        catch (ArgumentException ex)
        {
            // Motivo vacio. 400.
            return SanitizedBadRequest(ex);
        }
        // BusinessInvariantViolationException (INV-WAIVE-REVERT-*) la atrapa GlobalExceptionHandler (409 con invariantCode).
        catch (Exception ex) when (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
        }
    }

    /// <summary>
    /// ADR-044 T2 Addendum (2026-07-10): agrega un cargo SECUNDARIO del operador sobre una multa YA confirmada
    /// (ej. una retencion fiscal ademas del cargo administrativo automatico). Accion OPCIONAL: NO se muestra ni
    /// se pregunta en el flujo simple de confirmar la multa.
    ///
    /// <para><b>Permiso</b>: MISMO gate fiscal que confirm-penalty/correct-penalty
    /// (<see cref="Permissions.ReservasCancel"/> para llegar + <see cref="Permissions.CancellationsClassifyAgencyPenalty"/>
    /// o Admin, resuelto server-side + ownership).</para>
    ///
    /// <para><b>Mapeo de errores</b>: 400 (moneda del cargo no coincide con ninguna linea del operador /
    /// <c>DocumentRef</c> vacio con FacturadaAparte); 404 (BC no existe); 409 INV-ADR044-CHARGE-PERM (sin
    /// permiso); 409 INV-ADR044-CHARGE-001 (la multa de este operador todavia no esta confirmada); 409
    /// INV-ADR044-T2-COMMISSIONONLY (operador intermediario); 409 (flag OFF); 409 CONCURRENT_EDIT (xmin); 503
    /// (DB caida).</para>
    /// </summary>
    [HttpPost("{publicId:guid}/operator-charges")]
    [RequirePermission(Permissions.ReservasCancel)]
    [RequireOwnership(OwnedEntity.BookingCancellation, "publicId", bypassPermission: Permissions.ReservasViewAll)]
    public async Task<ActionResult<BookingCancellationDto>> AddOperatorCharge(
        Guid publicId,
        AddOperatorChargeRequest request,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "System";
        var userName = User.FindFirst("FullName")?.Value ?? User.FindFirst(ClaimTypes.Name)?.Value;
        var requesterIsAdmin = User.IsInRole("Admin");

        var userCanClassifyAgencyPenalty = requesterIsAdmin
            || (await _permissionResolver.GetPermissionsAsync(userId, cancellationToken))
                .Contains(Permissions.CancellationsClassifyAgencyPenalty);

        try
        {
            var dto = await _bcService.AddOperatorChargeAsync(
                publicId, request, userId, userName, cancellationToken,
                userCanClassifyAgencyPenalty: userCanClassifyAgencyPenalty);
            return Ok(dto);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new
            {
                code = "CONCURRENT_EDIT",
                message = "Otra edicion fue procesada primero, reintente.",
            });
        }
        catch (InvalidOperationException ex)
        {
            // Flag OFF. Business limpio se muestra; ruido tecnico .NET/EF se sanea (FUGA 3).
            return SanitizedConflict(ex, publicId);
        }
        catch (ArgumentException ex)
        {
            // Moneda del cargo no coincide con ninguna linea del operador / DocumentRef vacio con FacturadaAparte.
            return SanitizedBadRequest(ex);
        }
        // BusinessInvariantViolationException (INV-ADR044-CHARGE-* / INV-ADR044-T2-COMMISSIONONLY) la atrapa
        // GlobalExceptionHandler (409 con invariantCode).
        catch (Exception ex) when (DatabaseExceptionClassifier.IsDatabaseUnavailable(ex))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, DatabaseExceptionClassifier.CreateProblemDetails());
        }
    }

    /// <summary>
    /// ADR-044 T3b Decision 1 (2026-07-10): elige o corrige a que factura de venta se traslada UN cargo puntual
    /// del operador, para cuando la reserva tiene 2+ facturas de venta activas (ADR-042) y el motor de emision
    /// de la Nota de Debito no pudo autocompletarla sola. Pantalla T4: desplegable de facturas activas, oculto
    /// si hay 1 sola.
    ///
    /// <para><b>Permiso</b>: MISMO gate fiscal que agregar/confirmar un cargo del operador.</para>
    ///
    /// <para><b>Mapeo de errores</b>: 404 (BC/cargo no existen); 409 INV-ADR044-CHARGE-PERM (sin permiso); 409
    /// INV-ADR044-TARGETINVOICE-001 (la factura elegida no es una factura de venta activa de la reserva); 409
    /// INV-ADR044-TARGETINVOICE-002 (M2: otro cargo de la misma linea ya apunta a una factura distinta); 409
    /// INV-ADR044-TARGETINVOICE-003 (la ND al cliente ya se emitio / esta en vuelo); 409 (flag OFF); 503 (DB caida).</para>
    /// </summary>
    [HttpPatch("{publicId:guid}/operator-charges/{chargePublicId:guid}/target-invoice")]
    [RequirePermission(Permissions.ReservasCancel)]
    [RequireOwnership(OwnedEntity.BookingCancellation, "publicId", bypassPermission: Permissions.ReservasViewAll)]
    public async Task<ActionResult<BookingCancellationDto>> SetOperatorChargeTargetInvoice(
        Guid publicId,
        Guid chargePublicId,
        SetOperatorChargeTargetInvoiceRequest request,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "System";
        var userName = User.FindFirst("FullName")?.Value ?? User.FindFirst(ClaimTypes.Name)?.Value;
        var requesterIsAdmin = User.IsInRole("Admin");

        var userCanClassifyAgencyPenalty = requesterIsAdmin
            || (await _permissionResolver.GetPermissionsAsync(userId, cancellationToken))
                .Contains(Permissions.CancellationsClassifyAgencyPenalty);

        try
        {
            var dto = await _bcService.SetOperatorChargeTargetInvoiceAsync(
                publicId, chargePublicId, request, userId, userName, cancellationToken,
                userCanClassifyAgencyPenalty: userCanClassifyAgencyPenalty);
            return Ok(dto);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return SanitizedConflict(ex, publicId);
        }
        // BusinessInvariantViolationException (INV-ADR044-CHARGE-PERM / INV-ADR044-TARGETINVOICE-*) la atrapa
        // GlobalExceptionHandler (409 con invariantCode).
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
            // Business limpio se muestra; ruido tecnico .NET/EF se sanea (FUGA 3).
            return SanitizedConflict(ex, publicId);
        }
        catch (ArgumentException ex)
        {
            return SanitizedBadRequest(ex);
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
            // Business limpio se muestra; ruido tecnico .NET/EF se sanea (FUGA 3).
            return SanitizedConflict(ex, publicId);
        }
        catch (ArgumentException ex)
        {
            return SanitizedBadRequest(ex);
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
    /// ADR-014 (read-model, 2026-06-23): lectura de la cancelacion VIGENTE de una reserva por el
    /// PublicId de la RESERVA. Lo usa el panel "Confirmar multa del operador" de la ficha de
    /// reserva: va directo a la cancelacion del file en vez de buscarla en la bandeja back-office
    /// de NDs pendientes (que filtra por estado de la ND y dejaba afuera el caso pass-through).
    ///
    /// <para><b>Permiso/ownership</b>: <see cref="Permissions.ReservasView"/> + ownership sobre la
    /// reserva por la ruta (bypass <c>ReservasViewAll</c> para back-office). Mismo criterio que
    /// las otras lecturas scoped al vendedor — NO usa <c>cobranzas.*</c> porque es una vista del
    /// vendedor sobre SU reserva, no la bandeja agregada cross-reserva.</para>
    ///
    /// <para>404 si la reserva no tiene ninguna cancelacion no-abortada.</para>
    /// </summary>
    [HttpGet("by-reserva/{reservaPublicId:guid}")]
    [RequirePermission(Permissions.ReservasView)]
    [RequireOwnership(OwnedEntity.Reserva, "reservaPublicId", bypassPermission: Permissions.ReservasViewAll)]
    public async Task<ActionResult<BookingCancellationDto>> GetByReserva(
        Guid reservaPublicId,
        CancellationToken cancellationToken)
    {
        var dto = await _bcService.GetByReservaAsync(reservaPublicId, cancellationToken);
        if (dto is null) return NotFound();
        return Ok(dto);
    }

    /// <summary>
    /// ADR-044 Fix B (2026-07-13): sugerencia del dolar oficial BNA para una FECHA pasada, para pre-escribir el
    /// tipo de cambio del modal "corregir monto y moneda" (la conversion de una multa en dolares sobre una
    /// factura en pesos). SOLO LECTURA de lo ya guardado: NO llama a Banco Nacion en vivo.
    ///
    /// <para><b>Permiso</b>: <see cref="Permissions.ReservasView"/> — mismo nivel que las otras lecturas de la
    /// ficha de reserva (cualquier usuario que opera reservas). Es una cotizacion publica, sin ownership por fila
    /// (no expone datos de una reserva concreta).</para>
    ///
    /// <para><b>Respuesta</b>: 200 con <c>{ rate, rateDate }</c> cuando hay un dato razonable para la fecha; 204
    /// (sin contenido) cuando no hay dato util para esa fecha (el modal cae a "escribilo a mano"); 400 si falta
    /// la fecha. <b>Limitacion</b> (dato real del modelo): la tabla de cotizaciones es un SINGLETON (guarda solo
    /// la ULTIMA), asi que solo se ofrece ese unico snapshot y unicamente si su fecha cae en una ventana corta
    /// (&lt;= la pedida, hasta 5 dias antes). Ver <c>IBnaExchangeRateService.GetPersistedUsdSellerRateForDateAsync</c>.</para>
    /// </summary>
    /// <param name="date">Fecha (YYYY-MM-DD) del dia en que el operador cobro la multa.</param>
    [HttpGet("bna-usd-rate")]
    [RequirePermission(Permissions.ReservasView)]
    public async Task<ActionResult<BnaRateForDateDto>> GetBnaUsdRateForDate(
        [FromQuery] DateOnly? date,
        CancellationToken cancellationToken)
    {
        if (date is null)
            return BadRequest(new { message = "Indicá la fecha para buscar el dólar oficial." });

        var suggestion = await _bnaExchangeRateService
            .GetPersistedUsdSellerRateForDateAsync(date.Value, cancellationToken);
        if (suggestion is null)
            return NoContent(); // sin dato util para esa fecha: el front permite cargar el TC a mano.

        return Ok(suggestion);
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
            // Re-clasificacion a TotalPlusNewInvoice (no soportado Fase 1), feature flag off, etc. Business
            // limpio se muestra; ruido tecnico .NET/EF (ej. "supplier no encontrado" del EF) se sanea (FUGA 3).
            return SanitizedConflict(ex, publicId);
        }
        catch (ArgumentException ex)
        {
            // ArgumentNullException(req) o validacion de argumentos del service.
            return SanitizedBadRequest(ex);
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
    /// FUGA 3 data-exposure (2026-07-03): 409 saneado para un <see cref="InvalidOperationException"/>. CRITERIO
    /// (blocklist, no allowlist): el texto de NEGOCIO en espanol limpio SI se muestra (ej. "La reserva X no
    /// tiene factura activa para anular." — util para el vendedor); solo el RUIDO TECNICO (.NET/EF por carreras
    /// como "Sequence contains no elements." o "The instance of entity type 'BookingCancellation' cannot be
    /// tracked ... {Id: 42}", XML/SOAP de ARCA) se reemplaza por un generico. El crudo SIEMPRE va al log (con
    /// identificadores seguros), nunca al body. La deteccion la centraliza <see cref="ArcaErrorSanitizer"/>.
    /// </summary>
    private ConflictObjectResult SanitizedConflict(InvalidOperationException ex, object? identifier)
    {
        // RouteData puede no estar seteada (ej. tests que construyen el controller a mano) -> null-safe.
        var routeValues = ControllerContext?.RouteData?.Values;
        var endpoint = routeValues is not null && routeValues.TryGetValue("action", out var a)
            ? a?.ToString()
            : "cancellation";
        _logger.LogWarning(ex,
            "{Endpoint}: InvalidOperationException para {Identifier} (usuario {UserId}).",
            endpoint, identifier, User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "System");

        var message = ArcaErrorSanitizer.IsLikelyTechnical(ex.Message)
            ? "No se pudo completar la operación. Volvé a intentar."
            : ex.Message;
        return Conflict(new { message });
    }

    /// <summary>
    /// FUGA B6 data-exposure (2026-07-03): 400 saneado para un <see cref="ArgumentException"/>, mismo criterio
    /// blocklist que <see cref="SanitizedConflict"/>. Detalle propio de esta rama: ArgumentException con
    /// paramName agrega el sufijo del framework " (Parameter 'request')" al final del mensaje — se RECORTA ese
    /// sufijo primero (si no, el "Parameter '" del blocklist taparia tambien los mensajes de negocio limpios) y
    /// recien despues se evalua el resto. Un ArgumentNullException del binding ("Value cannot be null. ...")
    /// queda sin texto al recortar -> cae al generico. El crudo va al log, nunca al body.
    /// </summary>
    private BadRequestObjectResult SanitizedBadRequest(ArgumentException ex)
    {
        var routeValues = ControllerContext?.RouteData?.Values;
        var endpoint = routeValues is not null && routeValues.TryGetValue("action", out var a)
            ? a?.ToString()
            : "cancellation";
        _logger.LogWarning(ex,
            "{Endpoint}: ArgumentException (usuario {UserId}).",
            endpoint, User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "System");

        var stripped = System.Text.RegularExpressions.Regex
            .Replace(ex.Message, @"\s*\(Parameter '[^']*'\)\s*$", "")
            .Trim();
        var message = stripped.Length == 0 || ArcaErrorSanitizer.IsLikelyTechnical(stripped)
            ? "Los datos enviados no son válidos. Revisá el formulario y volvé a intentar."
            : stripped;
        return BadRequest(new { message });
    }

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

/// <summary>
/// Fase A (2026-06-28): payload del cierre SIN multa ("el operador no cobro multa"). El <c>Reason</c> es
/// OBLIGATORIO porque cerrar sin multa es una DECISION DE NEGOCIO que el contador debe poder rastrear
/// (distinguir "no hubo multa" de un error). Queda en el audit <c>OperatorPenaltyWaived</c>.
/// </summary>
public record WaivePenaltyRequest(
    [System.ComponentModel.DataAnnotations.Required]
    [System.ComponentModel.DataAnnotations.MinLength(5)]
    [System.ComponentModel.DataAnnotations.MaxLength(500)]
    string Reason,

    /// <summary>
    /// ADR-044 T1 (2026-07-10): identificador PUBLICO del operador cuya pata de multa se cierra sin multa, para
    /// cancelaciones con servicios de MAS de un operador (ADR-025). Mismo criterio retrocompatible que
    /// <see cref="ConfirmPenaltyRequest.SupplierPublicId"/>: opcional cuando hay un solo operador en juego.
    /// </summary>
    Guid? SupplierPublicId = null
);

/// <summary>
/// Fase A (2026-06-28): payload de la REVERSA del cierre sin multa. El <c>Reason</c> es OBLIGATORIO porque reabrir
/// una penalidad ya cerrada es una accion sensible que el contador debe poder rastrear (por que se reabrio: error
/// de carga vs multa tardia del operador). Queda en el audit <c>OperatorPenaltyWaiveReverted</c>.
/// </summary>
public record RevertWaivePenaltyRequest(
    [System.ComponentModel.DataAnnotations.Required]
    [System.ComponentModel.DataAnnotations.MinLength(5)]
    [System.ComponentModel.DataAnnotations.MaxLength(500)]
    string Reason,

    /// <summary>
    /// ADR-044 T1 (2026-07-10): identificador PUBLICO del operador cuya pata de multa se reabre, para cancelaciones
    /// con servicios de MAS de un operador (ADR-025). Opcional y retrocompatible: si hay un solo operador en juego
    /// se resuelve solo. Mismo criterio que <see cref="ConfirmPenaltyRequest.SupplierPublicId"/> / <see cref="WaivePenaltyRequest.SupplierPublicId"/>.
    /// </summary>
    Guid? SupplierPublicId = null
);

/// <summary>
/// Spec "el paso de multa vive en la ficha" (A4, 2026-07-08): payload de la correccion de monto + moneda de una
/// multa confirmada con la ND trabada. El <c>Reason</c> es OBLIGATORIO (rastro para el contador de por que se
/// corrigio). El service valida ademas monto &gt; 0 y moneda ISO ARS/USD (defensa server-side, no confiamos en el front).
/// </summary>
public record CorrectPenaltyRequest(
    [System.ComponentModel.DataAnnotations.Range(0.01, double.MaxValue,
        ErrorMessage = "El monto de la multa debe ser mayor a cero.")]
    decimal Amount,

    [System.ComponentModel.DataAnnotations.Required]
    [System.ComponentModel.DataAnnotations.MaxLength(3,
        ErrorMessage = "La moneda debe ser un código de 3 letras (por ejemplo: ARS o USD).")]
    string Currency,

    [System.ComponentModel.DataAnnotations.Required]
    [System.ComponentModel.DataAnnotations.MinLength(5)]
    [System.ComponentModel.DataAnnotations.MaxLength(500)]
    string Reason,

    // ADR-044 Fix B (2026-07-13): tipo de cambio para convertir una multa declarada en una moneda distinta a la
    // de la factura (Caso A: multa en dólares, factura en pesos). Van AL FINAL, nullable con default, para no
    // romper la firma posicional del record. Se REQUIEREN cuando la moneda elegida no coincide con la de la
    // factura y todo se puede convertir; el service revalida server-side (no se confía en el front) y devuelve
    // 400 con mensaje claro si faltan.
    decimal? ExchangeRate = null,

    // Origen del tipo de cambio (ver ExchangeRateSource). Manual (o sin especificar) exige justificación.
    int? ExchangeRateSource = null,

    // Fecha del tipo de cambio (el día en que el operador cobró la multa). Requerida cuando hay que convertir.
    DateTime? ExchangeRateDate = null,

    [System.ComponentModel.DataAnnotations.MaxLength(500)]
    string? ExchangeRateJustification = null
);

/// <summary>
/// ADR-044 "Deshacer una multa ya emitida" (2026-07-14): payload de deshacer una Nota de Debito de multa YA
/// EMITIDA con CAE. El <c>Reason</c> es OBLIGATORIO (auditoria del contador: por que la ND estaba mal).
/// </summary>
public record UndoDebitNoteRequest(
    [System.ComponentModel.DataAnnotations.Required]
    [System.ComponentModel.DataAnnotations.MinLength(5)]
    [System.ComponentModel.DataAnnotations.MaxLength(500)]
    string Reason
);
