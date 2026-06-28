using System.ComponentModel.DataAnnotations;

namespace TravelApi.Application.DTOs.Cancellation;

/// <summary>
/// FC1.3.6b (ADR-009 §2.12 round 3 + plan tactico FC1.3 §FC1.3.6b, 2026-05-21):
/// payload del admin para forzar la re-emision del callback del bridge sobre
/// un <c>PartialCreditNoteApproval</c> que el job de reconciliacion agoto sus
/// reintentos.
///
/// <para><b>Que pasa antes de llegar aca</b>: el job
/// <c>PartialCreditNoteBridgeReconciliationJob</c> intento N veces (default 5)
/// reaplicar el callback del bridge sobre una <c>ApprovalRequest</c> Approved
/// o Rejected cuyo BC quedo en <c>ManualReviewPending</c>. Todos fallaron. El
/// job dispara UNA notificacion al admin y deja de reintentar. El admin abre
/// la UI, investiga el error (visible en <c>BridgeLastError</c>) y, si confirma
/// que la situacion se puede destrabar (ej. la integracion AFIP ya esta sana),
/// genera un <c>ApprovalRequest</c> tipo <c>InvariantOverride</c> apuntando a
/// este target approval e invoca el endpoint <c>force-bridge-callback</c>.
/// </para>
///
/// <para><b>Reglas de validacion</b> (mas las que valida el service):
/// <list type="bullet">
///   <item><c>ApprovalRequestOverridePublicId</c>: publicId del approval tipo
///     <c>InvariantOverride=7</c> ya APROBADO que respalda esta accion.</item>
///   <item><c>Reason</c>: motivo libre del admin (texto en espanol), &gt;= 50
///     chars, distinto del <c>ResolverNotes</c> del approval target (el service
///     chequea esto para evitar "copy-paste del comentario original").</item>
/// </list>
/// </para>
///
/// <para><b>Por que el reason aca en el body Y en el InvariantOverride</b>:
/// son dos cosas distintas:
/// <list type="bullet">
///   <item>El <c>Reason</c> del <c>InvariantOverride</c> es el motivo de pedir
///     PERMISO ("autorizame a forzar este callback").</item>
///   <item>El <c>Reason</c> de este body es el motivo de la ACCION concreta
///     ("este es el caso especifico donde estoy ejecutando el override").</item>
/// </list>
/// Se persisten ambos en audit log para que un reviewer fiscal pueda separar
/// "decision politica" de "decision operativa".</para>
/// </summary>
public record ForceBridgeCallbackRequest(
    [Required(ErrorMessage = "Falta indicar la solicitud de autorización.")]
    Guid ApprovalRequestOverridePublicId,

    [Required(ErrorMessage = "El motivo es obligatorio.")]
    [MinLength(50, ErrorMessage = "El motivo debe tener al menos 50 caracteres.")]
    [MaxLength(1000)]
    string Reason);
