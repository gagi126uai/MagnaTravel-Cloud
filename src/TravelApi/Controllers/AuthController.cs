using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.RateLimiting;
using TravelApi.Application.Contracts.Auth;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using System.Security.Claims;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _authService;
    private readonly UserManager<ApplicationUser> _userManager;

    public AuthController(IAuthService authService, UserManager<ApplicationUser> userManager)
    {
        _authService = authService;
        _userManager = userManager;
    }

    [HttpPost("register")]
    [EnableRateLimiting("auth")]
    public async Task<ActionResult<AuthResponse>> Register(RegisterRequest request)
    {
        try
        {
            var hasUsers = await _userManager.Users.AnyAsync();
            var isAdminBootstrap = User.Identity?.IsAuthenticated == true && User.IsInRole("Admin");

            if (hasUsers && !isAdminBootstrap)
            {
                return StatusCode(403, new[]
                {
                    "El registro publico esta deshabilitado. Solo un administrador puede crear usuarios."
                });
            }

            var response = await _authService.RegisterAsync(request);
            return Ok(response);
        }
        catch (Exception ex)
        {
            return BadRequest(new[] { ex.Message });
        }
    }

    [HttpPost("login")]
    [EnableRateLimiting("auth")]
    public async Task<ActionResult<AuthResponse>> Login(LoginRequest request)
    {
        try
        {
            var response = await _authService.LoginAsync(request);
            return Ok(response);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(ex.Message);
        }
        catch (Exception ex)
        {
            return BadRequest(new[] { ex.Message });
        }
    }

    [Authorize]
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        var result = await _authService.ChangePasswordAsync(userId, request);
        if (!result.Succeeded)
        {
            return result.Errors?.Contains("no encontrado") == true ? NotFound(result.Errors) : BadRequest(result.Errors);
        }

        return Ok(new { Message = "Contraseña actualizada correctamente." });
    }
}
