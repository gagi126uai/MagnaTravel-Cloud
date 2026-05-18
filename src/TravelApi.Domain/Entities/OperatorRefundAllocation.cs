using System.ComponentModel.DataAnnotations;

namespace TravelApi.Domain.Entities;

/// <summary>
/// FC1 (ADR-002 §2.3, 2026-05-13): tabla de relacion N:M entre
/// <see cref="OperatorRefundReceived"/> (UN ingreso) y
/// <see cref="BookingCancellation"/> (UNA cancelacion). Es el "vinculo de plata"
/// que dice "del ingreso X de $1000 le tocan $300 a esta cancelacion Y".
///
/// Refactor BC-1 (accounting round 2): las deducciones (cargos, retenciones, etc.)
/// NO se persisten como columnas planas aca — viven en una collection
/// <see cref="DeductionLine"/> 1:N. La motivacion es que <c>DeductionKind</c> tiene
/// 10 valores diferentes con reglas de validacion distintas, imposible de modelar
/// con columnas planas. Resultado: <c>GrossAmount = NetAmount + SUM(Deductions.Amount)</c>.
///
/// Invariantes (validados con CHECK SQL + en domain layer):
///  - <c>NetAmount &gt;= 0</c> y <c>GrossAmount &gt;= NetAmount</c> (chk_alloc_net_positive).
///  - Unique partial index: una sola allocation activa por (refundId, bookingCancellationId).
///    Si la cashier se equivoca, debe marcar <see cref="IsVoided"/> y crear una nueva
///    (ApprovalRequest.MisassociationReversal, ADR-002 §2.10).
/// </summary>
public class OperatorRefundAllocation : IHasPublicId
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();

    public int OperatorRefundReceivedId { get; set; }
    public OperatorRefundReceived Refund { get; set; } = null!;

    public int BookingCancellationId { get; set; }
    public BookingCancellation BookingCancellation { get; set; } = null!;

    /// <summary>Monto bruto indicado por el operador para esta cancelacion (antes de descontar deducciones).</summary>
    public decimal GrossAmount { get; set; }

    /// <summary>
    /// Monto neto que efectivamente le llega al cliente como saldo. Igual a
    /// <c>GrossAmount - SUM(Deductions.Amount)</c>. Validado en domain layer al
    /// guardar; la BD valida con <c>chk_alloc_net_positive</c>.
    /// </summary>
    public decimal NetAmount { get; set; }

    /// <summary>
    /// Marca de soft-void: cuando la cashier reasocia el dinero (BC equivocada o
    /// cliente devolvio), la allocation original queda <c>IsVoided=true</c> y se
    /// crea una allocation reemplazo. El unique partial index excluye esta condicion
    /// para permitir el reemplazo. La fila NO se borra (audit trail).
    /// </summary>
    public bool IsVoided { get; set; }

    /// <summary>FK retro-reverse: si esta allocation REEMPLAZA a una previa voided, apunta a su Id.</summary>
    public int? VoidsAllocationId { get; set; }
    public OperatorRefundAllocation? VoidsAllocation { get; set; }

    // ============================================================
    // FC1.2.2 (2026-05-18): metadata del soft-void.
    //
    // Por que separamos VoidedAt/By/Reason de IsVoided:
    //  - IsVoided es el flag operativo: lo usa el unique partial index
    //    (`WHERE IsVoided = false`) para permitir que una allocation
    //    nueva tome el slot de una vieja anulada.
    //  - Los 3 campos abajo capturan QUIEN, CUANDO y POR QUE — datos
    //    obligatorios para auditoria fiscal / contable cuando se anula
    //    o reasocia un movimiento de plata.
    //
    // Quedan nullable porque las allocations creadas activas no los
    // setean. Cuando IsVoided pasa a true, el service de FC1.2.2
    // (OperatorRefundService.VoidAllocationAsync / Reassociate) los
    // completa con el contexto del actor.
    // ============================================================

    /// <summary>FC1.2.2: UTC en que se marco <see cref="IsVoided"/> = true. Null mientras la allocation este activa.</summary>
    public DateTime? VoidedAt { get; set; }

    /// <summary>FC1.2.2: UserId del cashier/admin que ejecuto el void o el reassociate. Auditoria fiscal.</summary>
    [MaxLength(450)]
    public string? VoidedByUserId { get; set; }

    /// <summary>FC1.2.2: razon textual de la anulacion (min 20 chars del request). Para que el contador entienda el movimiento.</summary>
    [MaxLength(500)]
    public string? VoidedReason { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Required]
    [MaxLength(450)]
    public string CreatedByUserId { get; set; } = string.Empty;

    /// <summary>
    /// BC-2 (ADR-002 §2.12): referencia futura al asiento contable cuando se
    /// implemente <c>AccountingEntry</c> (out-of-scope FC1, in-scope cuando
    /// MagnaTravel migre a RI). INV-110: una vez seteado, la allocation se
    /// vuelve inmutable salvo contra-asiento. Null en FC1 siempre.
    /// </summary>
    public int? AccountingEntryRef { get; set; }

    /// <summary>BC-1 (ADR-002 §2.3 / arca-tax round 2): N deducciones tipificadas. 0..N por allocation.</summary>
    public ICollection<DeductionLine> Deductions { get; set; } = new List<DeductionLine>();
}
