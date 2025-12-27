namespace TravelApi.Contracts.Auth;

public record AuthResponse(string Token, DateTime ExpiresAt, string Email, string FullName);
