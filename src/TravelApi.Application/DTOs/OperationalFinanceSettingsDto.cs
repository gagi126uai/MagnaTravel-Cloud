namespace TravelApi.Application.DTOs;

public class OperationalFinanceSettingsDto
{
    public bool RequireFullPaymentForOperativeStatus { get; set; } = true;
    public bool RequireFullPaymentForVoucher { get; set; } = true;
    public string AfipInvoiceControlMode { get; set; } = "AllowAgentOverrideWithReason";
    public bool EnableUpcomingUnpaidReservationNotifications { get; set; } = true;
    public int UpcomingUnpaidReservationAlertDays { get; set; } = 7;
}
