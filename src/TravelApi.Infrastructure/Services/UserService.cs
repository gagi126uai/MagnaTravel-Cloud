using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using TravelApi.Application.Contracts.Users;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

public class UserService : IUserService
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RoleManager<IdentityRole> _roleManager;
    private readonly AppDbContext _dbContext;
    private readonly IUserPermissionResolver? _userPermissionResolver;

    public UserService(
        UserManager<ApplicationUser> userManager,
        RoleManager<IdentityRole> roleManager,
        AppDbContext dbContext,
        IUserPermissionResolver? userPermissionResolver = null)
    {
        _userManager = userManager;
        _roleManager = roleManager;
        _dbContext = dbContext;
        // Optional para no romper tests existentes que construyen UserService a mano.
        // El resolver es scoped y siempre se inyecta desde DI en runtime.
        _userPermissionResolver = userPermissionResolver;
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
            throw new InvalidOperationException(string.Join(", ", result.Errors.Select(e => e.Description)));
        }

        if (!string.IsNullOrWhiteSpace(request.Role))
        {
            var roleResult = await _userManager.AddToRoleAsync(user, request.Role);
            if (!roleResult.Succeeded)
            {
                throw new InvalidOperationException(string.Join(", ", roleResult.Errors.Select(e => e.Description)));
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
        var wasActive = user.IsActive;

        user.FullName = request.FullName;
        user.Email = request.Email;
        user.UserName = request.Email;
        user.IsActive = request.IsActive;

        var updateResult = await _userManager.UpdateAsync(user);
        if (!updateResult.Succeeded)
        {
            throw new InvalidOperationException(string.Join(", ", updateResult.Errors.Select(e => e.Description)));
        }

        var currentRoles = await _userManager.GetRolesAsync(user);
        if (!string.IsNullOrWhiteSpace(request.Role))
        {
            var removeRoles = currentRoles.Where(role => role != request.Role).ToList();
            var rolesChanged = removeRoles.Count > 0 || !currentRoles.Contains(request.Role);
            if (removeRoles.Count > 0)
            {
                var removeResult = await _userManager.RemoveFromRolesAsync(user, removeRoles);
                if (!removeResult.Succeeded)
                {
                    throw new InvalidOperationException(string.Join(", ", removeResult.Errors.Select(e => e.Description)));
                }
            }

            if (!currentRoles.Contains(request.Role))
            {
                var addResult = await _userManager.AddToRoleAsync(user, request.Role);
                if (!addResult.Succeeded)
                {
                    throw new InvalidOperationException(string.Join(", ", addResult.Errors.Select(e => e.Description)));
                }
            }

            if (rolesChanged)
            {
                await RevokeAllRefreshTokensAsync(user.Id);
                // B1.15 Fase 1: invalidar cache de permisos al cambiar de rol.
                _userPermissionResolver?.Invalidate(user.Id);
            }
        }

        if (wasActive && !request.IsActive)
        {
            await RevokeAllRefreshTokensAsync(user.Id);
            // B1.15 Fase 1: invalidar cache al desactivar usuario.
            _userPermissionResolver?.Invalidate(user.Id);
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
        if (result.Succeeded)
        {
            await RevokeAllRefreshTokensAsync(user.Id);
        }
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

    private async Task RevokeAllRefreshTokensAsync(string userId)
    {
        var tokens = await _dbContext.RefreshTokens
            .Where(token => token.UserId == userId && token.RevokedAt == null && token.ExpiresAt > DateTime.UtcNow)
            .ToListAsync();

        if (tokens.Count == 0)
        {
            return;
        }

        foreach (var token in tokens)
        {
            token.RevokedAt = DateTime.UtcNow;
        }

        await _dbContext.SaveChangesAsync();
    }

    // --- Permissions ---

    public async Task<IEnumerable<string>> GetPermissionsForRoleAsync(string roleName)
    {
        return await _dbContext.RolePermissions
            .Where(rp => rp.RoleName == roleName)
            .Select(rp => rp.Permission)
            .OrderBy(p => p)
            .ToListAsync();
    }

    public async Task<UserServiceResult> UpdatePermissionsForRoleAsync(string roleName, string[] permissions)
    {
        if (!await _roleManager.RoleExistsAsync(roleName))
        {
            return new UserServiceResult(false, new[] { "Rol no encontrado." });
        }

        var existing = await _dbContext.RolePermissions
            .Where(rp => rp.RoleName == roleName)
            .ToListAsync();

        _dbContext.RolePermissions.RemoveRange(existing);

        var validPermissions = Permissions.All.ToHashSet();
        foreach (var perm in permissions.Where(p => validPermissions.Contains(p)))
        {
            _dbContext.RolePermissions.Add(new RolePermission
            {
                RoleName = roleName,
                Permission = perm
            });
        }

        await _dbContext.SaveChangesAsync();

        // B1.15 Fase 1: cuando cambian los permisos de un rol, invalidar el cache
        // de permisos y revocar los refresh tokens de TODOS los usuarios con ese
        // rol. Esto evita ventanas con permisos viejos en sesiones activas.
        var usersInRole = await _userManager.GetUsersInRoleAsync(roleName);
        foreach (var user in usersInRole)
        {
            await RevokeAllRefreshTokensAsync(user.Id);
            _userPermissionResolver?.Invalidate(user.Id);
        }

        return new UserServiceResult(true);
    }

    public async Task<IEnumerable<string>> GetUserPermissionsAsync(string userId)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user is null) return Enumerable.Empty<string>();

        var roles = await _userManager.GetRolesAsync(user);

        var permissions = await _dbContext.RolePermissions
            .Where(rp => roles.Contains(rp.RoleName))
            .Select(rp => rp.Permission)
            .Distinct()
            .OrderBy(p => p)
            .ToListAsync();

        return permissions;
    }

    public Task<Dictionary<string, string[]>> GetAllPermissionCatalogAsync()
    {
        return Task.FromResult(Permissions.AllByModule);
    }
}
