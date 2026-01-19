using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TravelApi.Contracts.Users;
using TravelApi.Models;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/users")]
// [Authorize(Roles = "Admin")] // EMERGENCY UNLOCK for User Recovery
[Authorize] 
public class UsersController : ControllerBase
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RoleManager<IdentityRole> _roleManager;

    public UsersController(UserManager<ApplicationUser> userManager, RoleManager<IdentityRole> roleManager)
    {
        _userManager = userManager;
        _roleManager = roleManager;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<UserSummaryResponse>>> GetUsers()
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

        return Ok(response);
    }

    [HttpGet("roles")]
    public async Task<ActionResult<IReadOnlyList<string>>> GetRoles()
    {
        var roles = await _roleManager.Roles
            .OrderBy(role => role.Name)
            .Select(role => role.Name ?? string.Empty)
            .ToListAsync();

        return Ok(roles);
    }

    [HttpPost("roles")]
    public async Task<IActionResult> CreateRole([FromBody] CreateRoleRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RoleName))
        {
            return BadRequest(new[] { "El nombre del rol es requerido." });
        }

        if (await _roleManager.RoleExistsAsync(request.RoleName))
        {
            return BadRequest(new[] { "El rol ya existe." });
        }

        var result = await _roleManager.CreateAsync(new IdentityRole(request.RoleName));
        if (!result.Succeeded)
        {
            return BadRequest(result.Errors.Select(error => error.Description));
        }

        return Ok(new { Message = "Rol creado correctamente." });
    }

    [HttpDelete("roles/{roleName}")]
    public async Task<IActionResult> DeleteRole(string roleName)
    {
        var role = await _roleManager.FindByNameAsync(roleName);
        if (role is null)
        {
            return NotFound("Rol no encontrado.");
        }

        if (role.Name == "Admin" || role.Name == "Colaborador")
        {
            return BadRequest("No se pueden eliminar los roles del sistema.");
        }

        var result = await _roleManager.DeleteAsync(role);
        if (!result.Succeeded)
        {
            return BadRequest(result.Errors.Select(error => error.Description));
        }

        return NoContent();
    }

    [HttpPost]
    public async Task<ActionResult<UserSummaryResponse>> CreateUser(CreateUserRequest request)
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
            return BadRequest(result.Errors.Select(error => error.Description));
        }

        if (!string.IsNullOrWhiteSpace(request.Role))
        {
            var roleResult = await _userManager.AddToRoleAsync(user, request.Role);
            if (!roleResult.Succeeded)
            {
                return BadRequest(roleResult.Errors.Select(error => error.Description));
            }
        }

        var roles = await _userManager.GetRolesAsync(user);
        return Ok(new UserSummaryResponse(
            user.Id,
            user.FullName,
            user.Email ?? string.Empty,
            roles.ToList(),
            user.IsActive));
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<UserSummaryResponse>> UpdateUser(string id, UpdateUserRequest request)
    {
        var user = await _userManager.FindByIdAsync(id);
        if (user is null)
        {
            return NotFound();
        }

        user.FullName = request.FullName;
        user.Email = request.Email;
        user.UserName = request.Email;
        user.IsActive = request.IsActive;

        var updateResult = await _userManager.UpdateAsync(user);
        if (!updateResult.Succeeded)
        {
            return BadRequest(updateResult.Errors.Select(error => error.Description));
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
                    return BadRequest(removeResult.Errors.Select(error => error.Description));
                }
            }

            if (!currentRoles.Contains(request.Role))
            {
                var addResult = await _userManager.AddToRoleAsync(user, request.Role);
                if (!addResult.Succeeded)
                {
                    return BadRequest(addResult.Errors.Select(error => error.Description));
                }
            }
        }

        var roles = await _userManager.GetRolesAsync(user);
        return Ok(new UserSummaryResponse(
            user.Id,
            user.FullName,
            user.Email ?? string.Empty,
            roles.ToList(),
            user.IsActive));
    }

    [HttpPut("{id}/password")]
    public async Task<IActionResult> ChangePassword(string id, ChangePasswordRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.NewPassword))
        {
            return BadRequest(new[] { "La contraseña no puede estar vacía." });
        }

        var user = await _userManager.FindByIdAsync(id);
        if (user is null)
        {
            return NotFound();
        }

        var resetToken = await _userManager.GeneratePasswordResetTokenAsync(user);
        var result = await _userManager.ResetPasswordAsync(user, resetToken, request.NewPassword);
        if (!result.Succeeded)
        {
            return BadRequest(result.Errors.Select(error => error.Description));
        }

        return NoContent();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteUser(string id)
    {
        var user = await _userManager.FindByIdAsync(id);
        if (user is null)
        {
            return NotFound();
        }

        var result = await _userManager.DeleteAsync(user);
        if (!result.Succeeded)
        {
            return BadRequest(result.Errors.Select(error => error.Description));
        }

        return NoContent();
    }
}
