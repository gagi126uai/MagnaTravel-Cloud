using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using TravelApi.Application.Contracts.Auth;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;

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
    public async Task<ActionResult<AuthSessionResponse>> Register(RegisterRequest request)
    {
        var hasUsers = await _userManager.Users.AnyAsync();
        if (hasUsers)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new[]
            {
                "El registro publico esta deshabilitado. Usa la administracion de usuarios."
            });
        }

        try
        {
            var response = await _authService.RegisterAsync(request, GetIpAddress(), GetUserAgent());
            WriteSessionCookies(response);
            return Ok(new AuthSessionResponse(response.User, response.AccessTokenExpiresAt));
        }
        catch (InvalidOperationException)
        {
            return BadRequest(new[] { "No se pudo completar el registro." });
        }
    }

    [HttpPost("login")]
    [EnableRateLimiting("auth")]
    public async Task<ActionResult<AuthSessionResponse>> Login(LoginRequest request)
    {
        try
        {
            var response = await _authService.LoginAsync(request, GetIpAddress(), GetUserAgent());
            WriteSessionCookies(response);
            return Ok(new AuthSessionResponse(response.User, response.AccessTokenExpiresAt));
        }
        catch (UnauthorizedAccessException)
        {
            ClearAuthCookies();
            return Unauthorized(new { message = "No se pudo iniciar sesion con las credenciales provistas." });
        }
        catch (InvalidOperationException)
        {
            ClearAuthCookies();
            return Unauthorized(new { message = "No se pudo iniciar sesion con las credenciales provistas." });
        }
    }

    [HttpPost("refresh")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<ActionResult<AuthSessionResponse>> Refresh()
    {
        if (!Request.Cookies.TryGetValue(AuthCookieNames.Refresh, out var refreshToken) || string.IsNullOrWhiteSpace(refreshToken))
        {
            ClearAuthCookies();
            return Unauthorized(new { message = "La sesion no pudo renovarse." });
        }

        try
        {
            var response = await _authService.RefreshAsync(refreshToken, GetIpAddress(), GetUserAgent());
            WriteSessionCookies(response);
            return Ok(new AuthSessionResponse(response.User, response.AccessTokenExpiresAt));
        }
        catch (UnauthorizedAccessException)
        {
            ClearAuthCookies();
            return Unauthorized(new { message = "La sesion no pudo renovarse." });
        }
    }

    [HttpPost("logout")]
    [AllowAnonymous]
    public async Task<IActionResult> Logout()
    {
        if (Request.Cookies.TryGetValue(AuthCookieNames.Refresh, out var refreshToken) && !string.IsNullOrWhiteSpace(refreshToken))
        {
            await _authService.RevokeRefreshTokenAsync(refreshToken);
        }

        ClearAuthCookies();
        return NoContent();
    }

    [Authorize]
    [HttpGet("me")]
    public async Task<ActionResult<CurrentUserResponse>> Me()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized();
        }

        var user = await _authService.GetCurrentUserAsync(userId);
        if (user is null)
        {
            ClearAuthCookies();
            return Unauthorized();
        }

        return Ok(user);
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

        ClearAuthCookies();
        return Ok(new { message = "Contraseña actualizada correctamente. Volvé a iniciar sesión." });
    }

    [Authorize(Roles = "Admin")]
    [HttpPost("hangfire-session")]
    public async Task<IActionResult> CreateHangfireSession()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized();
        }

        var token = await _authService.CreateHangfireTokenAsync(userId, TimeSpan.FromMinutes(5));
        Response.Cookies.Append(AuthCookieNames.Hangfire, token, BuildCookieOptions(DateTime.UtcNow.AddMinutes(5), httpOnly: true));
        return NoContent();
    }

    private string? GetIpAddress()
    {
        return HttpContext.Connection.RemoteIpAddress?.ToString();
    }

    private string? GetUserAgent()
    {
        return Request.Headers.UserAgent.ToString();
    }

    private void WriteSessionCookies(AuthTokensResult session)
    {
        Response.Cookies.Append(AuthCookieNames.Access, session.AccessToken, BuildCookieOptions(session.AccessTokenExpiresAt, httpOnly: true));
        Response.Cookies.Append(
            AuthCookieNames.Refresh,
            session.RefreshToken,
            BuildCookieOptions(session.RefreshTokenExpiresAt, httpOnly: true, persistent: session.IsPersistent));
        Response.Cookies.Append(
            AuthCookieNames.Csrf,
            session.CsrfToken,
            BuildCookieOptions(session.RefreshTokenExpiresAt, httpOnly: false, persistent: session.IsPersistent));
    }

    private void ClearAuthCookies()
    {
        Response.Cookies.Delete(AuthCookieNames.Access, BuildCookieOptions(null, httpOnly: true));
        Response.Cookies.Delete(AuthCookieNames.Refresh, BuildCookieOptions(null, httpOnly: true));
        Response.Cookies.Delete(AuthCookieNames.Csrf, BuildCookieOptions(null, httpOnly: false));
        Response.Cookies.Delete(AuthCookieNames.Hangfire, BuildCookieOptions(null, httpOnly: true));
    }

    private CookieOptions BuildCookieOptions(DateTime? expiresAt, bool httpOnly, bool persistent = true)
    {
        var options = new CookieOptions
        {
            HttpOnly = httpOnly,
            Secure = true, // Force secure for SameSite=None
            SameSite = SameSiteMode.None, // Required for cross-site auth (api.magna... vs backoffice.magna...)
            Path = "/"
        };

        if (persistent && expiresAt.HasValue)
        {
            options.Expires = expiresAt.Value;
        }

        return options;
    }
}
