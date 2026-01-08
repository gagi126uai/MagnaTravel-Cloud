namespace TravelApi.Contracts.Treasury;

public record ApplyTreasuryReceiptRequest(
    int ReservationId,
    decimal AmountApplied
);
