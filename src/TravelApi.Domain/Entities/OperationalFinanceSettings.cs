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

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
