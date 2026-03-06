namespace TravelApi.Application.Interfaces;

public interface ISearchService
{
    Task<SearchResultsResponse> SearchAsync(string query, CancellationToken cancellationToken);
}

public record SearchResultsResponse(
    string Query,
    List<CustomerSearchResult> Customers,
    List<FileSearchResult> Files,
    List<PaymentSearchResult> Payments);

public record CustomerSearchResult(int Id, string FullName, string? Email, string? Phone);
public record FileSearchResult(int Id, string FileNumber, string Name, string Status, string? PayerName);
public record PaymentSearchResult(int Id, decimal Amount, string Status, string Method, string? FileNumber);
