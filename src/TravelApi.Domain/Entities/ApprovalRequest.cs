using System.ComponentModel.DataAnnotations;

namespace TravelApi.Domain.Entities;

/// <summary>
/// B1.15 Fase B' (2026-05-11): solicitud de aprobacion generica.
///
/// Modelo unico polimorfico para TODAS las acciones que requieren autorizacion
/// del Admin/Colaborador. Centraliza el flujo "solicitante -> reviewer -> resolucion".
/// Reemplaza las puertas dispersas (ej. cancel-with-payment, annul, override discount).
///
/// Flow:
///  1. Solicitante (ej. Vendedor) crea: <see cref="ApprovalStatus.Pending"/>.
///  2. Reviewer (Admin/Colaborador) aprueba/rechaza:
///     - Approve -> <see cref="ApprovalStatus.Approved"/> + <c>ResolvedBy*</c>.
///     - Reject  -> <see cref="ApprovalStatus.Rejected"/> + <c>CooldownUntil</c>.
///  3. Solicitante reintenta el endpoint original (ej. <c>/annul</c>) que verifica
///     existe ApprovalRequest Approved no-consumida matcheando RequestType+EntityId+
///     RequestedByUserId, y al ejecutar la accion marca <see cref="ApprovalStatus.Consumed"/>.
///  4. Si nadie acciona antes de <see cref="ExpiresAt"/>: job nightly marca
///     <see cref="ApprovalStatus.Expired"/>.
///
/// Validacion fiscal/seguridad:
///  - Aprobacion atada a EntityId especifico — no hay "cheque en blanco".
///  - Cooldown post-rechazo evita spam de la misma solicitud.
///  - Idempotencia: re-pedir lo mismo con Pending devuelve la existente (no crea).
/// </summary>
public class ApprovalRequest : IHasPublicId
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();

    public ApprovalRequestType RequestType { get; set; }

    /// <summary>User que solicito (typicamente Vendedor).</summary>
    [Required]
    [MaxLength(450)]
    public string RequestedByUserId { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? RequestedByUserName { get; set; }

    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Entidad objetivo: "Invoice", "Reserva", "Service", etc.</summary>
    [Required]
    [MaxLength(50)]
    public string EntityType { get; set; } = string.Empty;

    /// <summary>Id legacy (int) de la entidad objetivo.</summary>
    public int EntityId { get; set; }

    /// <summary>Motivo declarado por el solicitante (auditoria).</summary>
    [MaxLength(1000)]
    public string? Reason { get; set; }

    public ApprovalStatus Status { get; set; } = ApprovalStatus.Pending;

    [MaxLength(450)]
    public string? ResolvedByUserId { get; set; }

    [MaxLength(200)]
    public string? ResolvedByUserName { get; set; }

    public DateTime? ResolvedAt { get; set; }

    /// <summary>Comentario del reviewer al aprobar/rechazar (auditoria).</summary>
    [MaxLength(1000)]
    public string? ResolverNotes { get; set; }

    /// <summary>Fecha hasta la cual la aprobacion es valida (no consumida).</summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>Momento en que el solicitante consumio la aprobacion ejecutando la accion.</summary>
    public DateTime? ConsumedAt { get; set; }

    /// <summary>Hasta cuando NO se puede re-pedir lo mismo tras rechazo (anti-spam).</summary>
    public DateTime? CooldownUntil { get; set; }

    /// <summary>JSON arbitrario con context del request (montos, fechas, etc.). Frontend interpreta segun RequestType.</summary>
    public string? Metadata { get; set; }

    // ============================================================
    // FC1.3.6b (ADR-009 §2.12 round 3, 2026-05-21): trazabilidad
    // del job de reconciliacion bridge. Aplica SOLO a approvals tipo
    // PartialCreditNoteApproval=11; el resto los ignora.
    //
    // Por que viven en la entidad generica y no en una tabla aparte:
    //  - El job lee y filtra por (RequestType, Status, BridgeRetryCount)
    //    en una sola query. Tabla 1:1 nos forzaria a LEFT JOIN o a
    //    nullables igual de feos, sin ganancia real.
    //  - Cardinality: las 3 columnas estan en NULL/0 para el 99% de los
    //    approvals (los no-FC1.3) — costo de storage despreciable
    //    (8 bytes int + null = 1 byte de payload por fila no-FC1.3).
    // ============================================================

    /// <summary>
    /// FC1.3.6b: cuantas veces el job de reconciliacion intento invocar el
    /// bridge para este approval. Resetea a 0 cuando el bridge tiene exito o
    /// cuando un admin fuerza el callback via endpoint dedicado. Una vez que
    /// supera <c>BridgeReconciliationMaxRetries</c>, el job deja de intentar
    /// y exige force-callback manual con InvariantOverride.
    /// </summary>
    public int BridgeRetryCount { get; set; } = 0;

    /// <summary>
    /// FC1.3.6b: ultimo mensaje de error que el bridge devolvio (truncado a
    /// 2000 chars). Sirve para el admin que abre el endpoint force-callback
    /// y entiende por que el job no pudo conciliar. Null si nunca fallo o si
    /// el ultimo intento fue exitoso.
    /// </summary>
    [MaxLength(2000)]
    public string? BridgeLastError { get; set; }

    /// <summary>
    /// FC1.3.6b: timestamp UTC del ultimo intento del job (haya sido exitoso
    /// o no). Sirve para diagnostico ("el job lo tomo o el problema es que
    /// el approval no entra en la query del job?").
    /// </summary>
    public DateTime? BridgeLastAttemptAt { get; set; }
}
