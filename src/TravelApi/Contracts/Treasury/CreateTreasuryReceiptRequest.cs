using TravelApi.Models;

namespace TravelApi.Contracts.Treasury;

public record CreateTreasuryReceiptRequest(
    string Reference,
    string Method,
    Currency? Currency,
    decimal Amount,
    DateTime? ReceivedAt,
    string? Notes
);
