using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using TravelApi.Application.Contracts.Users;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;

namespace TravelApi.Infrastructure.Services;

public class UserService : IUserService
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RoleManager<IdentityRole> _roleManager;

    public UserService(UserManager<ApplicationUser> userManager, RoleManager<IdentityRole> roleManager)
    {
        _userManager = userManager;
        _roleManager = roleManager;
    }

    public async Task<IEnumerable<UserSummaryResponse>> GetUsersAsync()
    {
        var users = await _userManager.Users
            .OrderBy(user => user.FullName)
            .ToListAsync();

        var response = new List<UserSummaryResponse>();
        foreach (var user in users)
        {
            var roles = await _userManager.GetRolesAsync(user);
            response.Add(new UserSummaryResponse(
                user.Id,
                user.FullName,
                user.Email ?? string.Empty,
                roles.ToList(),
                user.IsActive));
        }

        return response;
    }

    public async Task<IEnumerable<string>> GetRolesAsync()
    {
        return await _roleManager.Roles
            .OrderBy(role => role.Name)
            .Select(role => role.Name ?? string.Empty)
            .ToListAsync();
    }

    public async Task<UserServiceResult> CreateRoleAsync(CreateRoleRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RoleName))
        {
            return new UserServiceResult(false, new[] { "El nombre del rol es requerido." });
        }

        if (await _roleManager.RoleExistsAsync(request.RoleName))
        {
            return new UserServiceResult(false, new[] { "El rol ya existe." });
        }

        var result = await _roleManager.CreateAsync(new IdentityRole(request.RoleName));
        return new UserServiceResult(result.Succeeded, result.Errors.Select(e => e.Description));
    }

    public async Task<UserServiceResult> DeleteRoleAsync(string roleName)
    {
        var role = await _roleManager.FindByNameAsync(roleName);
        if (role is null)
        {
            return new UserServiceResult(false, new[] { "Rol no encontrado." });
        }

        if (role.Name == "Admin" || role.Name == "Colaborador")
        {
            return new UserServiceResult(false, new[] { "No se pueden eliminar los roles del sistema." });
        }

        var result = await _roleManager.DeleteAsync(role);
        return new UserServiceResult(result.Succeeded, result.Errors.Select(e => e.Description));
    }

    public async Task<UserSummaryResponse> CreateUserAsync(CreateUserRequest request)
    {
        var user = new ApplicationUser
        {
            UserName = request.Email,
            Email = request.Email,
            FullName = request.FullName
        };

        var result = await _userManager.CreateAsync(user, request.Password);
        if (!result.Succeeded)
        {
            throw new Exception(string.Join(", ", result.Errors.Select(e => e.Description)));
        }

        if (!string.IsNullOrWhiteSpace(request.Role))
        {
            var roleResult = await _userManager.AddToRoleAsync(user, request.Role);
            if (!roleResult.Succeeded)
            {
                throw new Exception(string.Join(", ", roleResult.Errors.Select(e => e.Description)));
            }
        }

        var roles = await _userManager.GetRolesAsync(user);
        return new UserSummaryResponse(
            user.Id,
            user.FullName,
            user.Email ?? string.Empty,
            roles.ToList(),
            user.IsActive);
    }

    public async Task<UserSummaryResponse> UpdateUserAsync(string id, UpdateUserRequest request)
    {
        var user = await _userManager.FindByIdAsync(id);
        if (user is null) throw new KeyNotFoundException("Usuario no encontrado");

        user.FullName = request.FullName;
        user.Email = request.Email;
        user.UserName = request.Email;
        user.IsActive = request.IsActive;

        var updateResult = await _userManager.UpdateAsync(user);
        if (!updateResult.Succeeded)
        {
            throw new Exception(string.Join(", ", updateResult.Errors.Select(e => e.Description)));
        }

        var currentRoles = await _userManager.GetRolesAsync(user);
        if (!string.IsNullOrWhiteSpace(request.Role))
        {
            var removeRoles = currentRoles.Where(role => role != request.Role).ToList();
            if (removeRoles.Count > 0)
            {
                var removeResult = await _userManager.RemoveFromRolesAsync(user, removeRoles);
                if (!removeResult.Succeeded)
                {
                    throw new Exception(string.Join(", ", removeResult.Errors.Select(e => e.Description)));
                }
            }

            if (!currentRoles.Contains(request.Role))
            {
                var addResult = await _userManager.AddToRoleAsync(user, request.Role);
                if (!addResult.Succeeded)
                {
                    throw new Exception(string.Join(", ", addResult.Errors.Select(e => e.Description)));
                }
            }
        }

        var roles = await _userManager.GetRolesAsync(user);
        return new UserSummaryResponse(
            user.Id,
            user.FullName,
            user.Email ?? string.Empty,
            roles.ToList(),
            user.IsActive);
    }

    public async Task<UserServiceResult> ChangePasswordAsync(string id, ChangePasswordRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.NewPassword))
        {
            return new UserServiceResult(false, new[] { "La contraseña no puede estar vacía." });
        }

        var user = await _userManager.FindByIdAsync(id);
        if (user is null)
        {
            return new UserServiceResult(false, new[] { "Usuario no encontrado." });
        }

        var resetToken = await _userManager.GeneratePasswordResetTokenAsync(user);
        var result = await _userManager.ResetPasswordAsync(user, resetToken, request.NewPassword);
        return new UserServiceResult(result.Succeeded, result.Errors.Select(e => e.Description));
    }

    public async Task<UserServiceResult> DeleteUserAsync(string id)
    {
        var user = await _userManager.FindByIdAsync(id);
        if (user is null)
        {
            return new UserServiceResult(false, new[] { "Usuario no encontrado." });
        }

        var result = await _userManager.DeleteAsync(user);
        return new UserServiceResult(result.Succeeded, result.Errors.Select(e => e.Description));
    }
}
