using TravelApi.Models;

namespace TravelApi.Contracts.Quotes;

public record QuoteSummaryDto(
    int Id,
    string ReferenceCode,
    string Status,
    string CustomerName,
    int LatestVersion,
    string ProductType,
    Currency? Currency,
    decimal TotalAmount,
    DateTime CreatedAt
);
