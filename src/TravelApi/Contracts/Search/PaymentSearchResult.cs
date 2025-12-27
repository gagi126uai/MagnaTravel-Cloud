namespace TravelApi.Contracts.Search;

public record PaymentSearchResult(int Id, decimal Amount, string Status, string Method, string ReservationCode);
