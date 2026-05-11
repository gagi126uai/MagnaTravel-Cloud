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

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
