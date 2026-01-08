namespace TravelApi.Contracts.Quotes;

public record QuoteDetailDto(
    int Id,
    string ReferenceCode,
    string Status,
    string CustomerName,
    DateTime CreatedAt,
    IReadOnlyCollection<QuoteVersionDto> Versions
);
