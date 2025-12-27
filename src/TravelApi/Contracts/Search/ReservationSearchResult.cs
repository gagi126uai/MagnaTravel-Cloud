namespace TravelApi.Contracts.Search;

public record ReservationSearchResult(int Id, string ReferenceCode, string Status, decimal TotalAmount, string CustomerName);
