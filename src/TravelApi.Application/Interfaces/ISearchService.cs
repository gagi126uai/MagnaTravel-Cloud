namespace TravelApi.Application.Interfaces;

public interface ISearchService
{
    Task<SearchResultsResponse> SearchAsync(string query, CancellationToken cancellationToken);
}

public record SearchResultsResponse(
    string Query,
    List<CustomerSearchResult> Customers,
    List<ReservaSearchResult> Reservas,
    List<PaymentSearchResult> Payments);

public record CustomerSearchResult(Guid PublicId, string FullName, string? Email, string? Phone);
public record ReservaSearchResult(Guid PublicId, string NumeroReserva, string Name, string Status, string? PayerName);
public record PaymentSearchResult(Guid PublicId, decimal Amount, string Status, string Method, string? NumeroReserva);
