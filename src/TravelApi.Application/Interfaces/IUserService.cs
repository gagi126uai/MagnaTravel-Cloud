using TravelApi.Application.Contracts.Users;

namespace TravelApi.Application.Interfaces;

public interface IUserService
{
    Task<IEnumerable<UserSummaryResponse>> GetUsersAsync();
    Task<IEnumerable<string>> GetRolesAsync();
    Task<UserServiceResult> CreateRoleAsync(CreateRoleRequest request);
    Task<UserServiceResult> DeleteRoleAsync(string roleName);
    Task<UserSummaryResponse> CreateUserAsync(CreateUserRequest request);
    Task<UserSummaryResponse> UpdateUserAsync(string id, UpdateUserRequest request);
    Task<UserServiceResult> ChangePasswordAsync(string id, ChangePasswordRequest request);
    Task<UserServiceResult> DeleteUserAsync(string id);
}

// IdentityResult needs Microsoft.AspNetCore.Identity
// To avoid bringing Identity reference to Application if we want it pure,
// we could return custom result objects. But the project already has some overlap.
// Given previous refactors, let's keep it simple or use a custom result.
// Let's use custom result or just the bool/string tuple for simplicity if we want to avoid Identity in Application.
// However, the project seems to use Identity in Domain/Entities (ApplicationUser).
// Let's create a simple UserServiceResult to avoid leakage of Identity to Application.

public record UserServiceResult(bool Succeeded, IEnumerable<string>? Errors = null);
