namespace TravelApi.Contracts.Users;

public record UpdateUserRequest(string FullName, string Email, string Role, bool IsActive);
