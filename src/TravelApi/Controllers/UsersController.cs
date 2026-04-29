using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.Contracts.Users;
using TravelApi.Application.Interfaces;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/users")]
public class UsersController : ControllerBase
{
    private readonly IUserService _userService;

    public UsersController(IUserService userService)
    {
        _userService = userService;
    }

    [HttpGet]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<IEnumerable<UserSummaryResponse>>> GetUsers()
    {
        var response = await _userService.GetUsersAsync();
        return Ok(response);
    }

    [HttpGet("supervisors")]
    [Authorize]
    public async Task<ActionResult<IEnumerable<UserSummaryResponse>>> GetSupervisors()
    {
        var users = await _userService.GetUsersAsync();
        // Filtrar usuarios que tienen rol Admin o algo equivalente que pueda autorizar
        var supervisors = users.Where(u => u.Roles.Contains("Admin") || u.Roles.Contains("Supervisor")).ToList();
        return Ok(supervisors);
    }

    [HttpGet("roles")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<IEnumerable<string>>> GetRoles()
    {
        var roles = await _userService.GetRolesAsync();
        return Ok(roles);
    }

    [HttpPost("roles")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> CreateRole([FromBody] CreateRoleRequest request)
    {
        var result = await _userService.CreateRoleAsync(request);
        if (!result.Succeeded)
        {
            return BadRequest(result.Errors);
        }

        return Ok(new { Message = "Rol creado correctamente." });
    }

    [HttpDelete("roles/{roleName}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> DeleteRole(string roleName)
    {
        var result = await _userService.DeleteRoleAsync(roleName);
        if (!result.Succeeded)
        {
            return result.Errors?.Contains("no encontrado") == true ? NotFound(result.Errors) : BadRequest(result.Errors);
        }

        return NoContent();
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<UserSummaryResponse>> CreateUser(CreateUserRequest request)
    {
        try
        {
            var response = await _userService.CreateUserAsync(request);
            return Ok(response);
        }
        catch (InvalidOperationException)
        {
            return BadRequest(new[] { "No se pudo crear el usuario." });
        }
    }

    [HttpPut("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<UserSummaryResponse>> UpdateUser(string id, UpdateUserRequest request)
    {
        try
        {
            var response = await _userService.UpdateUserAsync(id, request);
            return Ok(response);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException)
        {
            return BadRequest(new[] { "No se pudo actualizar el usuario." });
        }
    }

    [HttpPut("{id}/password")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> ChangePassword(string id, ChangePasswordRequest request)
    {
        var result = await _userService.ChangePasswordAsync(id, request);
        if (!result.Succeeded)
        {
            return result.Errors?.Contains("no encontrado") == true ? NotFound(result.Errors) : BadRequest(result.Errors);
        }

        return NoContent();
    }

    [HttpDelete("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> DeleteUser(string id)
    {
        var result = await _userService.DeleteUserAsync(id);
        if (!result.Succeeded)
        {
            return result.Errors?.Contains("no encontrado") == true ? NotFound(result.Errors) : BadRequest(result.Errors);
        }

        return NoContent();
    }

    // --- Permission Endpoints ---

    [HttpGet("permissions/catalog")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<Dictionary<string, string[]>>> GetPermissionCatalog()
    {
        var catalog = await _userService.GetAllPermissionCatalogAsync();
        return Ok(catalog);
    }

    [HttpGet("roles/{roleName}/permissions")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<IEnumerable<string>>> GetRolePermissions(string roleName)
    {
        var permissions = await _userService.GetPermissionsForRoleAsync(roleName);
        return Ok(permissions);
    }

    [HttpPut("roles/{roleName}/permissions")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> UpdateRolePermissions(string roleName, [FromBody] string[] permissions)
    {
        var result = await _userService.UpdatePermissionsForRoleAsync(roleName, permissions);
        if (!result.Succeeded)
        {
            return BadRequest(result.Errors);
        }

        return NoContent();
    }

    [HttpGet("me/permissions")]
    [Authorize]
    public async Task<ActionResult<IEnumerable<string>>> GetMyPermissions()
    {
        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (userId == null) return Unauthorized();

        var permissions = await _userService.GetUserPermissionsAsync(userId);
        return Ok(permissions);
    }
}
