namespace TravelApi.Contracts.Search;

public record SearchResultsResponse(
    string Query,
    IReadOnlyList<CustomerSearchResult> Customers,
    IReadOnlyList<ReservationSearchResult> Vouchers,
    IReadOnlyList<PaymentSearchResult> Payments);
