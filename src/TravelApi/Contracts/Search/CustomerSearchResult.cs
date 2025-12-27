namespace TravelApi.Contracts.Search;

public record CustomerSearchResult(int Id, string FullName, string? Email, string? Phone);
