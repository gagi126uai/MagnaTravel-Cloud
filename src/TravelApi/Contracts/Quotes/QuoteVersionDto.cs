using TravelApi.Models;

namespace TravelApi.Contracts.Quotes;

public record QuoteVersionDto(
    int Id,
    int VersionNumber,
    string ProductType,
    Currency? Currency,
    decimal TotalAmount,
    DateTime? ValidUntil,
    string? Notes,
    DateTime CreatedAt
);
