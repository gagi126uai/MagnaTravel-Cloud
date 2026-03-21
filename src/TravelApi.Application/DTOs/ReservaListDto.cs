namespace TravelApi.Application.DTOs;

public class ReservaListDto
{
    public int Id { get; set; }
    public string NumeroReserva { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = "Presupuesto";
    public string? CustomerName { get; set; }
    public decimal TotalSale { get; set; }
    public decimal Balance { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public decimal TotalCost { get; set; }
    public decimal TotalPaid { get; set; }
    public int PassengerCount { get; set; }
    public string? ResponsibleUserId { get; set; }
    public string? ResponsibleUserName { get; set; }
    public bool IsEconomicallySettled { get; set; }
    public bool CanMoveToOperativo { get; set; }
    public bool CanEmitVoucher { get; set; }
    public bool CanEmitAfipInvoice { get; set; }
    public string? EconomicBlockReason { get; set; }
}
