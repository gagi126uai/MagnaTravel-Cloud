namespace TravelApi.Application.Contracts.Auth;

public record CurrentUserResponse(
    string UserId,
    string Email,
    string FullName,
    IReadOnlyList<string> Roles,
    bool IsAdmin);

public record AuthSessionResponse(CurrentUserResponse User, DateTime ExpiresAt);

public record AuthTokensResult(
    string AccessToken,
    string RefreshToken,
    string CsrfToken,
    DateTime AccessTokenExpiresAt,
    DateTime RefreshTokenExpiresAt,
    CurrentUserResponse User,
    bool IsPersistent);
