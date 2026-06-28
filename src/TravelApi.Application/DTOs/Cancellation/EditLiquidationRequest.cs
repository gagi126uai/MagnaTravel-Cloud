using System.ComponentModel.DataAnnotations;
using TravelApi.Domain.Entities;

namespace TravelApi.Application.DTOs.Cancellation;

/// <summary>
/// FC1.3.3 (ADR-009 §2.7 G3, 2026-05-21): payload del admin para editar la
/// liquidacion fiscal de un BC en <see cref="BookingCancellationStatus.ManualReviewPending"/>.
///
/// <para><b>Que hace el admin con esto</b>: revisa lo que el calculator
/// propuso (penalidad del operador, items no reintegrables, kind de NC),
/// detecta que algun input cambio (ej. el operador mando email con
/// penalidad actualizada por antelacion diferente) y manda nuevos valores
/// para que el calculator re-corra. El BC se queda en
/// <c>ManualReviewPending</c> (self-loop), el approval persiste el cambio
/// en su Metadata JSON (<c>edits[]</c>) y el audit log captura el diff.</para>
///
/// <para><b>Reglas de validacion</b>:
/// <list type="bullet">
///   <item><c>Comment</c> obligatorio &gt;= 20 chars. Si el caller cae en el
///     bypass GR-005 (single admin self-edit), el service exige &gt;= 100 chars
///     adicionalmente (validacion en el service, NO en DataAnnotations, porque
///     depende de runtime data).</item>
///   <item>Los overrides son opcionales (null = no cambiar ese input).</item>
///   <item><c>OperatorPenaltyAmountOverride</c> y
///     <c>NonRefundableItemsAmountOverride</c> deben ser &gt;= 0 si no son null.</item>
/// </list>
/// </para>
///
/// <para><b>Importante</b>: el admin NO setea el <c>FiscalAmountToCredit</c>
/// directamente. Eso lo calcula el calculator con la formula del ADR. El admin
/// solo edita los inputs (penalty, items no reintegrables, eventualmente kind si
/// el contador lo pide), y el calculator devuelve la nueva liquidacion. Esto
/// preserva la trazabilidad fiscal: cualquier monto fiscal viene de la formula,
/// no de una decision manual sin justificar.</para>
/// </summary>
public record EditLiquidationRequest(
    [Range(0, double.MaxValue, ErrorMessage = "El monto de la multa del operador no puede ser negativo.")]
    decimal? OperatorPenaltyAmountOverride,

    [Range(0, double.MaxValue, ErrorMessage = "El monto de los conceptos no reembolsables no puede ser negativo.")]
    decimal? NonRefundableItemsAmountOverride,

    // Opcional. Si null, el calculator decide el kind segun la matriz 8. Si el
    // admin manda PartialOnOriginal explicitamente cuando el calculator hubiera
    // clasificado distinto, queda en metadata para auditoria — pero Fase 1 NO
    // permite forzar TotalPlusNewInvoice (GR-001 lo rechazaria en re-clasificacion).
    CreditNoteKind? CreditNoteKindOverride,

    [Required(ErrorMessage = "El comentario es obligatorio.")]
    [MinLength(20, ErrorMessage = "El comentario debe tener al menos 20 caracteres.")]
    [MaxLength(1000)]
    string Comment
);
