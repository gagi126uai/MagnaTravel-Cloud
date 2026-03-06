using TravelApi.Application.Contracts.Auth;

namespace TravelApi.Application.Interfaces;

public interface IAuthService
{
    Task<AuthResponse> RegisterAsync(RegisterRequest request);
    Task<AuthResponse> LoginAsync(LoginRequest request);
    Task<UserServiceResult> ChangePasswordAsync(string userId, ChangePasswordRequest request);
}
