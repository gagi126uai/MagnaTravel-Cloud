using TravelApi.Models;

namespace TravelApi.Contracts.Quotes;

public record CreateQuoteVersionRequest(
    string ProductType,
    Currency? Currency,
    decimal TotalAmount,
    DateTime? ValidUntil,
    string? Notes
);
