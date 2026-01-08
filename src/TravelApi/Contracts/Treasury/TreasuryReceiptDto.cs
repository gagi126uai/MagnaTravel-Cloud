using TravelApi.Models;

namespace TravelApi.Contracts.Treasury;

public record TreasuryReceiptDto(
    int Id,
    string Reference,
    string Method,
    Currency? Currency,
    decimal Amount,
    decimal AppliedAmount,
    decimal RemainingAmount,
    DateTime ReceivedAt,
    string? Notes,
    IReadOnlyCollection<TreasuryApplicationDto> Applications
);
