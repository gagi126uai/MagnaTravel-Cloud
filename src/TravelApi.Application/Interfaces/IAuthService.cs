using TravelApi.Application.Contracts.Auth;

namespace TravelApi.Application.Interfaces;

public interface IAuthService
{
    Task<AuthTokensResult> RegisterAsync(RegisterRequest request, string? ipAddress = null, string? userAgent = null);
    Task<AuthTokensResult> LoginAsync(LoginRequest request, string? ipAddress = null, string? userAgent = null);
    Task<AuthTokensResult> RefreshAsync(string refreshToken, string? ipAddress = null, string? userAgent = null);
    Task<CurrentUserResponse?> GetCurrentUserAsync(string userId);
    Task RevokeRefreshTokenAsync(string refreshToken);
    Task RevokeAllRefreshTokensAsync(string userId);
    Task<string> CreateHangfireTokenAsync(string userId, TimeSpan? lifetime = null);
    Task<UserServiceResult> ChangePasswordAsync(string userId, ChangePasswordRequest request);
}
