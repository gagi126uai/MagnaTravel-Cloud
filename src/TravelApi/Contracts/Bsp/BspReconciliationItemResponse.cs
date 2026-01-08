namespace TravelApi.Contracts.Bsp;

public record BspReconciliationItemResponse(
    int NormalizedRecordId,
    string TicketNumber,
    string ReservationReference,
    decimal TotalAmount,
    string Status,
    decimal? DifferenceAmount);
