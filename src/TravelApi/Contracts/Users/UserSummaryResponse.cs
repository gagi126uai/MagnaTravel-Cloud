namespace TravelApi.Contracts.Users;

public record UserSummaryResponse(string Id, string FullName, string Email, IReadOnlyList<string> Roles, bool IsActive);
