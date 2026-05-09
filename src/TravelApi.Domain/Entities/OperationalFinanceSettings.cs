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

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
