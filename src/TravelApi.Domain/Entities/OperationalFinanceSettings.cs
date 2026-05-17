using System.ComponentModel.DataAnnotations;

namespace TravelApi.Domain.Entities;

public static class AfipInvoiceControlModes
{
    public const string FullPaymentRequired = "FullPaymentRequired";
    public const string AllowAgentOverrideWithReason = "AllowAgentOverrideWithReason";
}

public class OperationalFinanceSettings
{
    public int Id { get; set; }

    public bool RequireFullPaymentForOperativeStatus { get; set; } = true;
    public bool RequireFullPaymentForVoucher { get; set; } = true;

    [MaxLength(50)]
    public string AfipInvoiceControlMode { get; set; } = AfipInvoiceControlModes.AllowAgentOverrideWithReason;

    public bool EnableUpcomingUnpaidReservationNotifications { get; set; } = true;
    public int UpcomingUnpaidReservationAlertDays { get; set; } = 7;

    /// <summary>
    /// B1.15 Fase 2a (Decision 5 de Gaston): tope de descuento (% sobre precio
    /// de referencia) que un Vendedor puede aplicar sin requerir el permiso
    /// <c>reservas.discount_above_threshold</c>. Default 10%. Rango valido 0..100.
    /// </summary>
    [System.ComponentModel.DataAnnotations.Schema.Column(TypeName = "numeric(5,2)")]
    public decimal MaxDiscountPercentWithoutOverride { get; set; } = 10m;

    /// <summary>
    /// B1.15 Fase B' (2026-05-11): dias hasta que una <c>ApprovalRequest</c>
    /// aprobada/pending expira automaticamente. Si pasan, el solicitante debe
    /// re-pedir. Default 7. Configurable por tipo via override.
    /// </summary>
    public int ApprovalDefaultExpirationDays { get; set; } = 7;

    /// <summary>
    /// B1.15 Fase B' (2026-05-11): horas durante las cuales el solicitante NO
    /// puede re-pedir la misma combinacion <c>(RequestType, EntityId)</c> tras
    /// un rechazo. Anti-spam. Default 1 hora.
    /// </summary>
    public int ApprovalRejectionCooldownHours { get; set; } = 1;

    /// <summary>
    /// B1.15 Fase D (2026-05-11): si <c>true</c>, anular factura requiere un
    /// <c>ApprovalRequest</c> aprobado previamente (Vendedor solicita, Admin/
    /// Colaborador aprueba). Admin bypassea este check. Si <c>false</c>,
    /// cualquier user con <c>cobranzas.invoice_annul</c> puede anular directo.
    /// Default <c>true</c> (recomendacion fiscal).
    /// </summary>
    public bool RequireApprovalForInvoiceAnnulment { get; set; } = true;

    // ============================================================
    // FC1.2.0 (plan tactico v3, 2026-05-17): settings del modulo de
    // cancelacion/refund. Todas las columnas agregadas en la migracion
    // FC1_2_0_AddSettingsAndApprovalRequestFK. Defaults pensados para
    // que la habilitacion en prod sea segura (flag OFF, politicas
    // conservadoras).
    // ============================================================

    /// <summary>
    /// FC1.2.0 v3 §10.1 — feature flag maestro del modulo FC1.2.
    /// Si <c>false</c> los services nuevos (BookingCancellationService,
    /// OperatorRefundService, ClientCreditService) rechazan todas las
    /// operaciones con un mensaje "modulo no habilitado". Permite mergear
    /// y deployar la infra sin exponer el flujo a usuarios hasta que QA
    /// y signoff fiscal (OPS-FISCAL-001) lo aprueben.
    /// Default <c>false</c> en prod; QA lo levanta a <c>true</c> en staging.
    /// Precondicion antes de prender en prod: ver query SQL §10.2.1 del plan
    /// (ResponsibleUserId backfill).
    /// </summary>
    public bool EnableNewCancellationFlow { get; set; } = false;

    /// <summary>
    /// FC1.2.0 v3 §10.1 — INV-100 precondicion. Si <c>true</c>, la cancelacion
    /// rechaza reservas con mas de una factura activa (no anulada) para evitar
    /// ambiguedad sobre cual factura anular. Mantenido en <c>true</c> hasta que
    /// FC1.3 modele cancelaciones con multiples facturas (caso real raro pero
    /// existente: invoice A inicial + invoice complementaria por upgrade).
    /// </summary>
    public bool OnePerReservaInvoicePolicy { get; set; } = true;

    /// <summary>
    /// FC1.2.0 v3 §10.1 — dias desde T0 (confirmacion con cliente) hasta que
    /// el job nocturno <c>OperatorRefundTimeoutJob</c> (FC2) mueve el BC a
    /// <c>AbandonedByOperator</c>. Default 60 dias siguiendo la practica
    /// retail de Argentina (mayoria de operadores devuelve dentro de 30-45
    /// dias; 60 deja buffer). Configurable por agencia desde el panel admin.
    /// </summary>
    public int OperatorRefundTimeoutDays { get; set; } = 60;

    /// <summary>
    /// FC1.2.0 v3 §10.1 — Ley 25.345 (FC4): umbral en ARS sobre el cual los
    /// retiros en efectivo (<c>WithdrawalKind.PhysicalCash</c>) requieren validacion
    /// adicional (CUIT, comprobante bancario, etc.). Default 1.000.000 ARS.
    /// **Confirmar con contador** antes de prender en prod — el valor depende
    /// de la resolucion AFIP vigente y se actualiza periodicamente.
    /// </summary>
    [System.ComponentModel.DataAnnotations.Schema.Column(TypeName = "numeric(18,2)")]
    public decimal Ley25345ThresholdAmount { get; set; } = 1_000_000m;

    /// <summary>
    /// FC1.2.0 v3 §10.1 — umbral en ARS por encima del cual un retiro fisico
    /// dispara una alerta en el panel Admin (auditoria diaria de movimientos
    /// grandes). Default 50.000 ARS. Pensado para que el Admin vea por dashboard
    /// los retiros importantes sin tener que filtrar por monto manualmente.
    /// Es informativo, no bloquea operaciones.
    /// </summary>
    [System.ComponentModel.DataAnnotations.Schema.Column(TypeName = "numeric(18,2)")]
    public decimal PhysicalRefundAlertThreshold { get; set; } = 50_000m;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
