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
    private readonly IServiceProvider _serviceProvider;

    public UsersController(IUserService userService, IServiceProvider serviceProvider)
    {
        _userService = userService;
        _serviceProvider = serviceProvider;
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

    [HttpDelete("{userId}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> DeleteUser([FromRoute] string userId)
    {
        var result = await _userService.DeleteUserAsync(userId);
        if (!result.Succeeded) return BadRequest(new { errors = result.Errors });
        return NoContent();
    }

    [HttpPost("fix-uuids")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> FixUuids()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TravelApi.Infrastructure.Persistence.AppDbContext>();
        
        var sql = @"
            UPDATE ""Vouchers""
            SET ""CreatedByUserName"" = u.""FullName""
            FROM ""AspNetUsers"" u
            WHERE ""Vouchers"".""CreatedByUserId"" = u.""Id"" AND length(""Vouchers"".""CreatedByUserName"") = 36;

            UPDATE ""Vouchers""
            SET ""IssuedByUserName"" = u.""FullName""
            FROM ""AspNetUsers"" u
            WHERE ""Vouchers"".""IssuedByUserId"" = u.""Id"" AND length(""Vouchers"".""IssuedByUserName"") = 36;

            UPDATE ""Vouchers""
            SET ""AuthorizedBySuperiorUserName"" = u.""FullName""
            FROM ""AspNetUsers"" u
            WHERE ""Vouchers"".""AuthorizedBySuperiorUserId"" = u.""Id"" AND length(""Vouchers"".""AuthorizedBySuperiorUserName"") = 36;

            UPDATE ""AuditLogs""
            SET ""UserName"" = u.""FullName""
            FROM ""AspNetUsers"" u
            WHERE ""AuditLogs"".""UserId"" = u.""Id"" AND length(""AuditLogs"".""UserName"") = 36;

            UPDATE ""Invoices""
            SET ""ForcedByUserName"" = u.""FullName""
            FROM ""AspNetUsers"" u
            WHERE ""Invoices"".""ForcedByUserId"" = u.""Id"" AND length(""Invoices"".""ForcedByUserName"") = 36;
            
            UPDATE ""MessageDeliveries""
            SET ""SentByUserName"" = u.""FullName""
            FROM ""AspNetUsers"" u
            WHERE ""MessageDeliveries"".""SentByUserId"" = u.""Id"" AND length(""MessageDeliveries"".""SentByUserName"") = 36;
        ";
        
        await Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.ExecuteSqlRawAsync(db.Database, sql);
        
        return Ok(new { Message = "UUIDs fixed in database." });
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
