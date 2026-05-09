using System.ComponentModel.DataAnnotations;

namespace TravelApi.Application.DTOs;

public class OperationalFinanceSettingsDto
{
    public bool RequireFullPaymentForOperativeStatus { get; set; } = true;
    public bool RequireFullPaymentForVoucher { get; set; } = true;
    public string AfipInvoiceControlMode { get; set; } = "AllowAgentOverrideWithReason";
    public bool EnableUpcomingUnpaidReservationNotifications { get; set; } = true;
    public int UpcomingUnpaidReservationAlertDays { get; set; } = 7;
    /// <summary>
    /// B1.15 Fase 2a: tope de descuento (% sobre precio de referencia) que un
    /// vendedor puede aplicar sin permiso <c>reservas.discount_above_threshold</c>.
    /// Rango valido 0..100. Default 10%.
    /// </summary>
    [Range(0, 100, ErrorMessage = "MaxDiscountPercentWithoutOverride debe estar entre 0 y 100.")]
    public decimal MaxDiscountPercentWithoutOverride { get; set; } = 10m;
}
