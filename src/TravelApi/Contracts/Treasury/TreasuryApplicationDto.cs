namespace TravelApi.Contracts.Treasury;

public record TreasuryApplicationDto(
    int Id,
    int ReservationId,
    decimal AmountApplied,
    DateTime AppliedAt
);
