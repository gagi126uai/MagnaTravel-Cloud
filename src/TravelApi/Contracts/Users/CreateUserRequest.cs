namespace TravelApi.Contracts.Users;

public record CreateUserRequest(string FullName, string Email, string Password, string Role);
