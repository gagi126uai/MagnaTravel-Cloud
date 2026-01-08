namespace TravelApi.Contracts.Quotes;

public record CreateQuoteRequest(
    string ReferenceCode,
    int CustomerId,
    string? Status,
    CreateQuoteVersionRequest Version
);
